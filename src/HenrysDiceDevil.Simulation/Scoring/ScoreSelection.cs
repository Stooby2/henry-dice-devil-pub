using System.Collections.Immutable;

namespace HenrysDiceDevil.Simulation.Scoring;

public sealed record ScoreSelection(
    ImmutableArray<int> UsedCounts,
    int UsedDice,
    int Points,
    ImmutableArray<KeyValuePair<string, int>> Tags);
