namespace HenrysDiceDevil.Simulation.Optimization;

public sealed record OptimizationProgress(
    int StageIndex,
    int StageCount,
    string StageKind,
    int ProcessedLoadouts,
    int TotalLoadouts,
    int CacheHits,
    int CacheMisses,
    double ElapsedMs);
