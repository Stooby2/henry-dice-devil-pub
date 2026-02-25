using HenrysDiceDevil.Domain.Models;

namespace HenrysDiceDevil.Simulation.Contracts;

public sealed record SimulationResult(
    IReadOnlyList<int> Counts,
    TurnMetrics Metrics,
    double MeanPoints,
    double StandardDeviation,
    IReadOnlyDictionary<string, int> TagCounts,
    int TotalGroups,
    int ScoringTurns);
