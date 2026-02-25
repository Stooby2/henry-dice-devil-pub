using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HenrysDiceDevil.Domain.Diagnostics;
using Microsoft.Data.Sqlite;

namespace HenrysDiceDevil.Infrastructure.Caching;

public sealed class FileResultCacheStore : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _dbPath;
    private readonly object _dbSync = new();
    private readonly object _pendingSync = new();
    private readonly IPerfSink _perfSink;
    private readonly bool _enableAsyncWrites;
    private readonly int _maxPendingEntries;
    private readonly int _writerFlushIntervalMs;
    private readonly AutoResetEvent _pendingSignal = new(initialState: false);
    private readonly CancellationTokenSource _writerCts = new();
    private readonly Task? _writerTask;

    private Dictionary<string, PendingEntry> _pendingWrites = new(StringComparer.Ordinal);
    private int _pendingHighWaterMark;
    private long _epoch;
    private bool _acceptWrites = true;
    private bool _disposed;
    private bool _initialized;

    public FileResultCacheStore(
        string rootDirectory = "cache",
        IPerfSink? perfSink = null,
        bool enableAsyncWrites = false,
        int maxPendingEntries = 200_000,
        int writerFlushIntervalMs = 60)
    {
        _rootDirectory = rootDirectory;
        _dbPath = Path.Combine(_rootDirectory, "cache.db");
        _perfSink = perfSink ?? NullPerfSink.Instance;
        _enableAsyncWrites = enableAsyncWrites;
        _maxPendingEntries = Math.Max(10_000, maxPendingEntries);
        _writerFlushIntervalMs = Math.Max(10, writerFlushIntervalMs);

        EnsureInitialized();
        if (_enableAsyncWrites)
        {
            _writerTask = Task.Run(WriterLoop);
        }
    }

    public IReadOnlyDictionary<string, JsonElement> Load(IReadOnlyList<string> keys)
    {
        ThrowIfDisposed();
        if (keys.Count == 0)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        long start = _perfSink.Enabled ? Stopwatch.GetTimestamp() : 0;
        var uniqueKeys = keys.Distinct(StringComparer.Ordinal).ToArray();
        var state = LoadPersisted(uniqueKeys);
        if (_enableAsyncWrites)
        {
            OverlayPending(state, uniqueKeys);
        }

        if (_perfSink.Enabled)
        {
            _perfSink.Increment("cache.load.requested_keys", keys.Count);
            _perfSink.Increment("cache.load.distinct_keys", uniqueKeys.Length);
            _perfSink.ObserveValue("cache.load.hit_count", state.Count);
            _perfSink.ObserveDurationMs("cache.load.total_ms", ElapsedMs(start));
        }

        return state;
    }

    public bool AsyncWritesEnabled => _enableAsyncWrites;

    public int PendingCount
    {
        get
        {
            lock (_pendingSync)
            {
                return _pendingWrites.Count;
            }
        }
    }

    public int PendingHighWaterMark
    {
        get
        {
            lock (_pendingSync)
            {
                return _pendingHighWaterMark;
            }
        }
    }

    public void Save(IReadOnlyList<(string Key, JsonElement Payload, string Kind)> entries)
    {
        ThrowIfDisposed();
        if (entries.Count == 0)
        {
            return;
        }

        if (!_enableAsyncWrites)
        {
            UpsertPersisted(entries);
            return;
        }

        long epoch = Interlocked.Read(ref _epoch);
        int droppedPilot = 0;

        lock (_pendingSync)
        {
            if (!_acceptWrites)
            {
                return;
            }

            foreach ((string key, JsonElement payload, string kindRaw) in entries)
            {
                string kind = kindRaw is "pilot" or "full" ? kindRaw : "full";
                if (kind == "pilot" && _pendingWrites.Count >= _maxPendingEntries)
                {
                    droppedPilot++;
                    continue;
                }

                _pendingWrites[key] = new PendingEntry(key, payload, kind, epoch);
            }

            if (_pendingWrites.Count > _pendingHighWaterMark)
            {
                _pendingHighWaterMark = _pendingWrites.Count;
            }
        }

        if (droppedPilot > 0)
        {
            _perfSink.Increment("cache.save.dropped_pilot_entries", droppedPilot);
        }

        _perfSink.Increment("cache.save.entries", entries.Count - droppedPilot);
        _pendingSignal.Set();
    }

    public void Delete(IReadOnlyList<string> keys)
    {
        ThrowIfDisposed();
        if (keys.Count == 0)
        {
            return;
        }

        if (_enableAsyncWrites)
        {
            // Clear pending writes so deleted entries are not reinserted.
            Interlocked.Increment(ref _epoch);
            lock (_pendingSync)
            {
                _pendingWrites.Clear();
            }
        }

        DeletePersisted(keys);
    }

    public int ClearKind(string kind)
    {
        ThrowIfDisposed();
        if (kind is not ("pilot" or "full"))
        {
            return 0;
        }

        if (_enableAsyncWrites)
        {
            // Clear pending writes so cleared entries are not reinserted.
            Interlocked.Increment(ref _epoch);
            lock (_pendingSync)
            {
                _pendingWrites.Clear();
            }
        }

        return ClearKindPersisted(kind);
    }

    public void ClearAll()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _epoch);

        if (_enableAsyncWrites)
        {
            lock (_pendingSync)
            {
                _pendingWrites.Clear();
            }
        }

        ClearAllPersisted();
    }

    public void Flush(TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        if (!_enableAsyncWrites)
        {
            return;
        }

        TimeSpan budget = timeout ?? TimeSpan.FromSeconds(2);
        long deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            int pending;
            lock (_pendingSync)
            {
                pending = _pendingWrites.Count;
            }

            if (pending == 0)
            {
                return;
            }

            _pendingSignal.Set();
            Thread.Sleep(10);
        }
    }

    public void Shutdown(TimeSpan? drainTimeout = null)
    {
        if (_disposed)
        {
            return;
        }

        if (_enableAsyncWrites)
        {
            lock (_pendingSync)
            {
                _acceptWrites = false;
            }

            Flush(drainTimeout ?? TimeSpan.FromMilliseconds(300));
            _writerCts.Cancel();
            _pendingSignal.Set();
            try
            {
                _writerTask?.Wait(TimeSpan.FromMilliseconds(200));
            }
            catch (AggregateException)
            {
                // Ignore shutdown wait failures.
            }
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Shutdown(TimeSpan.FromMilliseconds(300));
        _writerCts.Dispose();
        _pendingSignal.Dispose();
    }

    private void WriterLoop()
    {
        var token = _writerCts.Token;
        while (!token.IsCancellationRequested)
        {
            _pendingSignal.WaitOne(_writerFlushIntervalMs);
            DrainPendingBatch();
        }

        // Final drain after cancellation.
        DrainPendingBatch();
    }

    private void DrainPendingBatch()
    {
        Dictionary<string, PendingEntry> snapshot;
        lock (_pendingSync)
        {
            if (_pendingWrites.Count == 0)
            {
                return;
            }

            snapshot = _pendingWrites;
            _pendingWrites = new Dictionary<string, PendingEntry>(StringComparer.Ordinal);
        }

        long activeEpoch = Interlocked.Read(ref _epoch);
        var batch = snapshot.Values
            .Where(x => x.Epoch == activeEpoch)
            .Select(static x => (x.Key, x.Payload, x.Kind))
            .ToArray();

        if (batch.Length == 0)
        {
            return;
        }

        UpsertPersisted(batch);
    }

    private void OverlayPending(Dictionary<string, JsonElement> target, IReadOnlyList<string> keys)
    {
        long activeEpoch = Interlocked.Read(ref _epoch);
        lock (_pendingSync)
        {
            foreach (string key in keys)
            {
                if (_pendingWrites.TryGetValue(key, out PendingEntry entry) && entry.Epoch == activeEpoch)
                {
                    target[key] = entry.Payload;
                }
            }
        }
    }

    private Dictionary<string, JsonElement> LoadPersisted(IReadOnlyList<string> keys)
    {
        long start = _perfSink.Enabled ? Stopwatch.GetTimestamp() : 0;
        EnsureInitialized();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        lock (_dbSync)
        {
            using var connection = OpenConnection();
            foreach (string[] chunk in ChunkKeys(keys, size: 900))
            {
                if (chunk.Length == 0)
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                var parameterNames = new string[chunk.Length];
                for (int i = 0; i < chunk.Length; i++)
                {
                    string paramName = $"@k{i}";
                    parameterNames[i] = paramName;
                    _ = command.Parameters.AddWithValue(paramName, chunk[i]);
                }

                command.CommandText = $"SELECT key, payload FROM cache_entries WHERE key IN ({string.Join(",", parameterNames)})";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    string payload = reader.GetString(1);
                    using JsonDocument doc = JsonDocument.Parse(payload);
                    result[key] = doc.RootElement.Clone();
                }
            }
        }

        if (_perfSink.Enabled)
        {
            _perfSink.ObserveDurationMs("cache.read_state.ms", ElapsedMs(start));
            _perfSink.ObserveValue("cache.read_state.entries", result.Count);
        }

        return result;
    }

    private void UpsertPersisted(IReadOnlyList<(string Key, JsonElement Payload, string Kind)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        long start = _perfSink.Enabled ? Stopwatch.GetTimestamp() : 0;
        EnsureInitialized();
        lock (_dbSync)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO cache_entries(key, kind, payload, updated_utc)
                VALUES ($key, $kind, $payload, $updatedUtc)
                ON CONFLICT(key) DO UPDATE SET
                    kind = excluded.kind,
                    payload = excluded.payload,
                    updated_utc = excluded.updated_utc
                """;
            var keyParam = command.CreateParameter();
            keyParam.ParameterName = "$key";
            command.Parameters.Add(keyParam);
            var kindParam = command.CreateParameter();
            kindParam.ParameterName = "$kind";
            command.Parameters.Add(kindParam);
            var payloadParam = command.CreateParameter();
            payloadParam.ParameterName = "$payload";
            command.Parameters.Add(payloadParam);
            var updatedParam = command.CreateParameter();
            updatedParam.ParameterName = "$updatedUtc";
            command.Parameters.Add(updatedParam);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach ((string key, JsonElement payload, string kindRaw) in entries)
            {
                string kind = kindRaw is "pilot" or "full" ? kindRaw : "full";
                keyParam.Value = key;
                kindParam.Value = kind;
                payloadParam.Value = payload.GetRawText();
                updatedParam.Value = now;
                _ = command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        if (_perfSink.Enabled)
        {
            _perfSink.Increment("cache.write_state.entries", entries.Count);
            _perfSink.ObserveDurationMs("cache.write_state.ms", ElapsedMs(start));
            _perfSink.ObserveDurationMs("cache.save.total_ms", ElapsedMs(start));
        }
    }

    private void DeletePersisted(IReadOnlyList<string> keys)
    {
        long start = _perfSink.Enabled ? Stopwatch.GetTimestamp() : 0;
        EnsureInitialized();
        int removed = 0;
        lock (_dbSync)
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            foreach (string[] chunk in ChunkKeys(keys.Distinct(StringComparer.Ordinal).ToArray(), size: 900))
            {
                if (chunk.Length == 0)
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                var parameterNames = new string[chunk.Length];
                for (int i = 0; i < chunk.Length; i++)
                {
                    string paramName = $"@k{i}";
                    parameterNames[i] = paramName;
                    _ = command.Parameters.AddWithValue(paramName, chunk[i]);
                }

                command.CommandText = $"DELETE FROM cache_entries WHERE key IN ({string.Join(",", parameterNames)})";
                removed += command.ExecuteNonQuery();
            }

            tx.Commit();
        }

        if (_perfSink.Enabled)
        {
            _perfSink.Increment("cache.delete.keys", keys.Count);
            _perfSink.ObserveValue("cache.delete.removed", removed);
            _perfSink.ObserveDurationMs("cache.delete.total_ms", ElapsedMs(start));
        }
    }

    private int ClearKindPersisted(string kind)
    {
        long start = _perfSink.Enabled ? Stopwatch.GetTimestamp() : 0;
        EnsureInitialized();
        int removed;
        lock (_dbSync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cache_entries WHERE kind = $kind";
            _ = command.Parameters.AddWithValue("$kind", kind);
            removed = command.ExecuteNonQuery();
        }

        if (_perfSink.Enabled)
        {
            _perfSink.Increment("cache.clear_kind.deleted", removed);
            _perfSink.ObserveDurationMs("cache.clear_kind.total_ms", ElapsedMs(start));
        }

        return removed;
    }

    private void ClearAllPersisted()
    {
        long start = _perfSink.Enabled ? Stopwatch.GetTimestamp() : 0;
        EnsureInitialized();
        lock (_dbSync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cache_entries";
            _ = command.ExecuteNonQuery();
        }

        if (_perfSink.Enabled)
        {
            _perfSink.ObserveDurationMs("cache.clear_all.total_ms", ElapsedMs(start));
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_dbSync)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_rootDirectory);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA cache_size=-65536;
                PRAGMA busy_timeout=2000;
                CREATE TABLE IF NOT EXISTS cache_entries(
                    key TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    updated_utc INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_cache_entries_kind_updated ON cache_entries(kind, updated_utc);
                """;
            _ = command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static IEnumerable<string[]> ChunkKeys(IReadOnlyList<string> keys, int size)
    {
        for (int i = 0; i < keys.Count; i += size)
        {
            int len = Math.Min(size, keys.Count - i);
            var chunk = new string[len];
            for (int j = 0; j < len; j++)
            {
                chunk[j] = keys[i + j];
            }

            yield return chunk;
        }
    }

    private static double ElapsedMs(long startTimestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        return (1000.0 * elapsed) / Stopwatch.Frequency;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileResultCacheStore));
        }
    }

    private readonly record struct PendingEntry(string Key, JsonElement Payload, string Kind, long Epoch);
}
