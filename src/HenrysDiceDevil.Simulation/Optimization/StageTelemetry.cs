namespace HenrysDiceDevil.Simulation.Optimization;

public sealed record StageTelemetry(
    int StageIndex,
    string Kind,
    int CandidateCount,
    int EvaluatedCount,
    int CacheHits,
    int CacheMisses,
    double ElapsedMs,
    double EvaluationMs,
    double CacheLoadMs,
    double CacheSaveMs,
    int PeakPending)
{
    public double LoadoutsPerSecond => ElapsedMs <= 0.0 ? 0.0 : EvaluatedCount / (ElapsedMs / 1000.0);

    public double CacheHitRate
    {
        get
        {
            int total = CacheHits + CacheMisses;
            return total == 0 ? 0.0 : (double)CacheHits / total;
        }
    }

    public double WorkerUtilization => ElapsedMs <= 0.0 ? 0.0 : Math.Clamp(EvaluationMs / ElapsedMs, 0.0, 1.0);

    public double QueuePressure => EvaluatedCount == 0 ? 0.0 : (double)PeakPending / EvaluatedCount;
}
