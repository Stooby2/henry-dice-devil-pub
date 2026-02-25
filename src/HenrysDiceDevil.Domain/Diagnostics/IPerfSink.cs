using System.Diagnostics;

namespace HenrysDiceDevil.Domain.Diagnostics;

public interface IPerfSink
{
    bool Enabled { get; }

    void Increment(string metric, long delta = 1);

    void ObserveDurationMs(string metric, double milliseconds);

    void ObserveValue(string metric, double value);
}

public sealed class NullPerfSink : IPerfSink
{
    public static readonly NullPerfSink Instance = new();

    private NullPerfSink()
    {
    }

    public bool Enabled => false;

    public void Increment(string metric, long delta = 1)
    {
    }

    public void ObserveDurationMs(string metric, double milliseconds)
    {
    }

    public void ObserveValue(string metric, double value)
    {
    }
}

public readonly struct PerfTimer
{
    private readonly IPerfSink? _sink;
    private readonly string? _metric;
    private readonly long _startTimestamp;

    private PerfTimer(IPerfSink sink, string metric)
    {
        _sink = sink;
        _metric = metric;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public static PerfTimer Start(IPerfSink sink, string metric)
    {
        return new PerfTimer(sink, metric);
    }

    public void Stop()
    {
        if (_sink is null || _metric is null || !_sink.Enabled)
        {
            return;
        }

        long elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
        double ms = (1000.0 * elapsed) / Stopwatch.Frequency;
        _sink.ObserveDurationMs(_metric, ms);
    }
}
