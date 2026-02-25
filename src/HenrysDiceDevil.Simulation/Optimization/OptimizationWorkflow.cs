using System.Text.Json;
using System.Diagnostics;
using System.Collections.Concurrent;
using HenrysDiceDevil.Domain.Diagnostics;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Infrastructure.Caching;
using HenrysDiceDevil.Simulation.Contracts;
using HenrysDiceDevil.Simulation.Workers;

namespace HenrysDiceDevil.Simulation.Optimization;

public sealed class OptimizationWorkflow
{
    private readonly LoadoutEvaluator _evaluator;
    private readonly FileResultCacheStore _cache;
    private readonly IPerfSink _perfSink;

    public OptimizationWorkflow(LoadoutEvaluator evaluator, FileResultCacheStore cache, IPerfSink? perfSink = null)
    {
        _evaluator = evaluator;
        _cache = cache;
        _perfSink = perfSink ?? NullPerfSink.Instance;
    }

    public OptimizationRunResult Run(
        IReadOnlyList<IReadOnlyList<int>> loadouts,
        IReadOnlyList<DieType> diceCatalog,
        OptimizationSettings settings,
        IProgress<OptimizationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int progressIntervalMs = 100)
    {
        return Run(loadouts, diceCatalog, settings, workerCount: null, progress, cancellationToken, progressIntervalMs);
    }

    public OptimizationRunResult Run(
        IReadOnlyList<IReadOnlyList<int>> loadouts,
        IReadOnlyList<DieType> diceCatalog,
        OptimizationSettings settings,
        int? workerCount,
        IProgress<OptimizationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int progressIntervalMs = 100)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _perfSink.Increment("workflow.run.invocations");
        _perfSink.ObserveValue("workflow.run.loadouts", loadouts.Count);
        progressIntervalMs = Math.Clamp(progressIntervalMs, 10, 5000);

        if (loadouts.Count == 0)
        {
            return new OptimizationRunResult([], 0, 0)
            {
                Telemetry = OptimizationTelemetry.Empty,
            };
        }

        string diceSignature = CacheKeyBuilder.BuildDiceSignature(
            diceCatalog.Select(d => (d.Name, (IReadOnlyList<double>)d.Probabilities)).ToArray());

        int stageCount = 0;
        int plannedStageCount = settings.EfficiencyEnabled && settings.EfficiencyPlan.Length > 0 ? settings.EfficiencyPlan.Length : 1;
        IReadOnlyList<IReadOnlyList<int>> candidates = loadouts;
        List<SimulationResult> stageResults = [];
        var telemetryStages = new List<StageTelemetry>();

        if (settings.EfficiencyEnabled && settings.EfficiencyPlan.Length > 0 && loadouts.Count > 1)
        {
            for (int stageIdx = 0; stageIdx < settings.EfficiencyPlan.Length; stageIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = settings.EfficiencyPlan[stageIdx];
                if (candidates.Count < row.MinTotal)
                {
                    continue;
                }

                stageCount++;
                bool isFinalStage = stageIdx == settings.EfficiencyPlan.Length - 1;
                int? stageSeed = isFinalStage ? null : settings.EfficiencySeed + stageIdx;
                var stageSettings = settings with { NumTurns = row.PilotTurns };
                string kind = isFinalStage ? "full" : "pilot";

                var stageEval = EvaluateWithCache(
                    candidates,
                    diceCatalog,
                    stageSettings,
                    stageSeed,
                    workerCount,
                    diceSignature,
                    kind,
                    stageCount - 1,
                    plannedStageCount,
                    progress,
                    cancellationToken,
                    progressIntervalMs);
                stageResults = stageEval.Results;
                telemetryStages.Add(stageEval.Telemetry);
                candidates = FilterSurvivors(stageResults, settings.Objective, row.KeepPercent, row.Epsilon, row.MinSurvivors);

                if (candidates.Count <= 1)
                {
                    break;
                }
            }
        }

        if (stageCount == 0)
        {
            var stageEval = EvaluateWithCache(
                loadouts,
                diceCatalog,
                settings,
                seedBase: null,
                workerCount,
                diceSignature,
                cacheKind: "full",
                stageIndex: 0,
                stageCount: plannedStageCount,
                progress,
                cancellationToken,
                progressIntervalMs);
            stageResults = stageEval.Results;
            telemetryStages.Add(stageEval.Telemetry);
            return new OptimizationRunResult(stageResults, 0, stageResults.Count)
            {
                Telemetry = new OptimizationTelemetry(telemetryStages),
            };
        }

        var survivorKeys = new HashSet<string>(candidates.Select(static c => string.Join(",", c)), StringComparer.Ordinal);
        var final = stageResults.Where(r => survivorKeys.Contains(string.Join(",", r.Counts))).ToList();
        return new OptimizationRunResult(final, stageCount, final.Count)
        {
            Telemetry = new OptimizationTelemetry(telemetryStages),
        };
    }

    private StageEvaluation EvaluateWithCache(
        IReadOnlyList<IReadOnlyList<int>> loadouts,
        IReadOnlyList<DieType> diceCatalog,
        OptimizationSettings settings,
        int? seedBase,
        int? workerCount,
        string diceSignature,
        string cacheKind,
        int stageIndex,
        int stageCount,
        IProgress<OptimizationProgress>? progress,
        CancellationToken cancellationToken,
        int progressIntervalMs)
    {
        var stageWatch = Stopwatch.StartNew();
        var stagePerfTimer = _perfSink.Enabled ? PerfTimer.Start(_perfSink, "workflow.stage.total_ms") : default;
        var cacheLoadWatch = Stopwatch.StartNew();
        var settingsMap = BuildSettingsMap(settings, seedBase);
        var context = CacheKeyBuilder.BuildContext(diceSignature, settingsMap);
        _perfSink.ObserveValue("workflow.stage.context_fields", context.Count);
        var keys = loadouts.Select(counts => CacheKeyBuilder.BuildFromContext(context, counts.ToArray())).ToArray();
        _perfSink.Increment("workflow.stage.key_build.count", keys.Length);
        _perfSink.ObserveValue("workflow.stage.loadouts", loadouts.Count);
        var loaded = _cache.Load(keys);
        cacheLoadWatch.Stop();
        _perfSink.ObserveDurationMs("workflow.stage.cache_load_ms", cacheLoadWatch.Elapsed.TotalMilliseconds);
        _perfSink.ObserveValue("workflow.stage.cache_loaded_entries", loaded.Count);

        var resultsByIndex = new SimulationResult?[loadouts.Count];
        var toWrite = new (string Key, JsonElement Payload, string Kind)[loadouts.Count];
        var evaluationWatch = Stopwatch.StartNew();
        int cacheHits = 0;
        int cacheMisses = 0;
        int processed = 0;
        int maxDegree = Math.Clamp(workerCount ?? 1, 1, Math.Max(1, Environment.ProcessorCount));
        var missingIndices = new List<int>();
        CancellationTokenSource? progressCts = null;
        Task? progressTask = null;
        bool progressStopped = false;

        void StopProgressReporter()
        {
            if (progressStopped)
            {
                return;
            }

            progressStopped = true;
            progressCts?.Cancel();
            if (progressTask is not null)
            {
                try
                {
                    progressTask.Wait();
                }
                catch (AggregateException)
                {
                    // Progress loop is canceled at stage end.
                }
            }

            progressCts?.Dispose();
        }

        if (progress is not null)
        {
            progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken progressToken = progressCts.Token;
            progressTask = Task.Run(async () =>
            {
                while (!progressToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(progressIntervalMs, progressToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    progress.Report(
                        new OptimizationProgress(
                            StageIndex: stageIndex,
                            StageCount: stageCount,
                            StageKind: cacheKind,
                            ProcessedLoadouts: Volatile.Read(ref processed),
                            TotalLoadouts: loadouts.Count,
                            CacheHits: Volatile.Read(ref cacheHits),
                            CacheMisses: Volatile.Read(ref cacheMisses),
                            ElapsedMs: stageWatch.Elapsed.TotalMilliseconds));
                }
            }, CancellationToken.None);
        }

        var results = new List<SimulationResult>(loadouts.Count);
        try
        {
            for (int i = 0; i < loadouts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string key = keys[i];
                if (loaded.TryGetValue(key, out JsonElement payload))
                {
                    resultsByIndex[i] = SimulationResultCodec.Deserialize(payload);
                    cacheHits++;
                    processed++;
                }
                else
                {
                    missingIndices.Add(i);
                    cacheMisses++;
                }
            }

            if (missingIndices.Count > 0)
            {
                int writesCount = missingIndices.Count;
                int rangeSize = Math.Max(16, writesCount / Math.Max(1, maxDegree * 8));
                var parallelOptions = new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maxDegree,
                };

                Parallel.ForEach(Partitioner.Create(0, writesCount, rangeSize), parallelOptions, range =>
                {
                    for (int slot = range.Item1; slot < range.Item2; slot++)
                    {
                        int index = missingIndices[slot];
                        string key = keys[index];
                        var result = _evaluator.EvaluateSingle(loadouts[index], diceCatalog, settings, seedBase);
                        resultsByIndex[index] = result;
                        toWrite[slot] = (key, SimulationResultCodec.Serialize(result), cacheKind);
                        Interlocked.Increment(ref processed);
                    }
                });

                if (writesCount < toWrite.Length)
                {
                    Array.Resize(ref toWrite, writesCount);
                }
            }
            else if (toWrite.Length > 0)
            {
                toWrite = [];
            }

            for (int i = 0; i < resultsByIndex.Length; i++)
            {
                var item = resultsByIndex[i];
                if (item is not null)
                {
                    results.Add(item);
                }
            }
        }
        finally
        {
            StopProgressReporter();
        }

        if (progress is not null)
        {
            progress.Report(
                new OptimizationProgress(
                    StageIndex: stageIndex,
                    StageCount: stageCount,
                    StageKind: cacheKind,
                    ProcessedLoadouts: loadouts.Count,
                    TotalLoadouts: loadouts.Count,
                    CacheHits: cacheHits,
                    CacheMisses: cacheMisses,
                    ElapsedMs: stageWatch.Elapsed.TotalMilliseconds));
        }

        evaluationWatch.Stop();
        _perfSink.ObserveDurationMs("workflow.stage.evaluation_ms", evaluationWatch.Elapsed.TotalMilliseconds);
        _perfSink.Increment("workflow.stage.cache_hits", cacheHits);
        _perfSink.Increment("workflow.stage.cache_misses", cacheMisses);
        _perfSink.ObserveValue("workflow.stage.max_degree", maxDegree);
        _perfSink.ObserveValue("workflow.stage.results", results.Count);
        var cacheSaveWatch = Stopwatch.StartNew();
        _cache.Save(toWrite);
        cacheSaveWatch.Stop();
        _perfSink.ObserveDurationMs("workflow.stage.cache_save_ms", cacheSaveWatch.Elapsed.TotalMilliseconds);
        _perfSink.Increment("workflow.stage.cache_save_entries", toWrite.Length);
        stageWatch.Stop();
        if (_perfSink.Enabled)
        {
            stagePerfTimer.Stop();
            _perfSink.ObserveDurationMs("workflow.stage.stopwatch_elapsed_ms", stageWatch.Elapsed.TotalMilliseconds);
        }

        int peakPending = Math.Max(0, cacheMisses - 1);
        var telemetry = new StageTelemetry(
            StageIndex: stageIndex,
            Kind: cacheKind,
            CandidateCount: loadouts.Count,
            EvaluatedCount: results.Count,
            CacheHits: cacheHits,
            CacheMisses: cacheMisses,
            ElapsedMs: stageWatch.Elapsed.TotalMilliseconds,
            EvaluationMs: evaluationWatch.Elapsed.TotalMilliseconds,
            CacheLoadMs: cacheLoadWatch.Elapsed.TotalMilliseconds,
            CacheSaveMs: cacheSaveWatch.Elapsed.TotalMilliseconds,
            PeakPending: peakPending);

        return new StageEvaluation(results, telemetry);
    }

    private static IReadOnlyList<IReadOnlyList<int>> FilterSurvivors(
        IReadOnlyList<SimulationResult> stageResults,
        OptimizationObjective objective,
        double keepPercent,
        double epsilon,
        int minSurvivors)
    {
        if (stageResults.Count == 0)
        {
            return [];
        }

        double keepFraction = Math.Clamp(keepPercent / 100.0, 0.0001, 1.0);
        int keepCount = Math.Max(minSurvivors, (int)Math.Ceiling(stageResults.Count * keepFraction));
        keepCount = Math.Min(stageResults.Count, keepCount);

        var top = stageResults
            .OrderBy(r => ObjectiveRanking.RankKey(r, objective).Primary)
            .ThenBy(r => ObjectiveRanking.RankKey(r, objective).Secondary)
            .Take(keepCount)
            .ToArray();
        if (top.Length == 0)
        {
            return [];
        }

        if (objective == OptimizationObjective.MaxScore)
        {
            double cutoffTurns = top[^1].Metrics.EvTurns;
            var survivors = stageResults
                .Where(r => r.Metrics.EvTurns <= cutoffTurns + epsilon)
                .Select(r => (IReadOnlyList<int>)r.Counts.ToArray())
                .ToArray();
            return survivors.Length == 0
                ? [top[0].Counts.ToArray()]
                : survivors;
        }
        else
        {
            double cutoffScore = ObjectiveRanking.ObjectiveScore(top[^1], objective);
            var survivors = stageResults
                .Where(r => ObjectiveRanking.ObjectiveScore(r, objective) >= cutoffScore - epsilon)
                .Select(r => (IReadOnlyList<int>)r.Counts.ToArray())
                .ToArray();
            return survivors.Length == 0
                ? [top[0].Counts.ToArray()]
                : survivors;
        }
    }

    private static Dictionary<string, object?> BuildSettingsMap(OptimizationSettings settings, int? seedBase)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["target"] = settings.TargetScore,
            ["risk_profile"] = settings.RiskProfile.ToString(),
            ["num_turns"] = settings.NumTurns,
            ["cap"] = settings.TurnCap,
        };
        if (seedBase.HasValue)
        {
            map["seed_base"] = seedBase.Value;
        }

        return map;
    }

    private sealed record StageEvaluation(List<SimulationResult> Results, StageTelemetry Telemetry);
}
