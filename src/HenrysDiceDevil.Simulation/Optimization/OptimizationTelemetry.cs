namespace HenrysDiceDevil.Simulation.Optimization;

public sealed record OptimizationTelemetry(IReadOnlyList<StageTelemetry> Stages)
{
    public static OptimizationTelemetry Empty { get; } = new OptimizationTelemetry([]);

    public int TotalEvaluatedLoadouts => Stages.Sum(static s => s.EvaluatedCount);

    public double TotalElapsedMs => Stages.Sum(static s => s.ElapsedMs);

    public double TotalLoadoutsPerSecond => TotalElapsedMs <= 0.0 ? 0.0 : TotalEvaluatedLoadouts / (TotalElapsedMs / 1000.0);

    public int TotalCacheHits => Stages.Sum(static s => s.CacheHits);

    public int TotalCacheMisses => Stages.Sum(static s => s.CacheMisses);

    public double TotalCacheHitRate
    {
        get
        {
            int total = TotalCacheHits + TotalCacheMisses;
            return total == 0 ? 0.0 : (double)TotalCacheHits / total;
        }
    }

    public double AverageWorkerUtilization => Stages.Count == 0 ? 0.0 : Stages.Average(static s => s.WorkerUtilization);

    public double AverageQueuePressure => Stages.Count == 0 ? 0.0 : Stages.Average(static s => s.QueuePressure);
}
