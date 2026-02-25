using System.Collections.Immutable;

namespace HenrysDiceDevil.Domain.Models;

public sealed record TurnMetrics(
    double EvTurns,
    ImmutableDictionary<int, double> PWithin,
    double EvPoints,
    double P50Turns,
    double P90Turns,
    double EvPointsSe);
