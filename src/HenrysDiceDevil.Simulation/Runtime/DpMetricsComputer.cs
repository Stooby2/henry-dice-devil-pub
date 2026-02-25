using System.Collections.Immutable;
using HenrysDiceDevil.Domain.Models;

namespace HenrysDiceDevil.Simulation.Runtime;

public static class DpMetricsComputer
{
    public static TurnMetrics Compute(
        IReadOnlyList<double> turnDistribution,
        int target,
        int maxTurns = 60,
        IReadOnlyList<int>? probTurns = null)
    {
        var turnsToCheck = (probTurns ?? new[] { 10, 15, 20 }).ToArray();
        if (turnDistribution.Count <= 1)
        {
            return new TurnMetrics(
                EvTurns: double.PositiveInfinity,
                PWithin: turnsToCheck.ToImmutableDictionary(static x => x, static _ => 0.0),
                EvPoints: 0.0,
                P50Turns: double.PositiveInfinity,
                P90Turns: double.PositiveInfinity,
                EvPointsSe: 0.0);
        }

        if (target <= 0)
        {
            double evPoints = 0.0;
            for (int i = 0; i < turnDistribution.Count; i++)
            {
                evPoints += i * turnDistribution[i];
            }

            return new TurnMetrics(
                EvTurns: 0.0,
                PWithin: turnsToCheck.ToImmutableDictionary(static x => x, static _ => 1.0),
                EvPoints: evPoints,
                P50Turns: 1.0,
                P90Turns: 1.0,
                EvPointsSe: 0.0);
        }

        var below = new double[target];
        var next = new double[target];
        below[0] = 1.0;
        var reachedBy = new List<double>(capacity: maxTurns);

        var support = new List<(int Score, double Probability)>();
        int maxSupport = Math.Min(target, turnDistribution.Count);
        for (int s = 0; s < maxSupport; s++)
        {
            double p = turnDistribution[s];
            if (p > 0.0)
            {
                support.Add((s, p));
            }
        }

        for (int turn = 1; turn <= maxTurns; turn++)
        {
            Array.Clear(next);
            foreach (var (score, prob) in support)
            {
                for (int x = 0; x < (target - score); x++)
                {
                    next[x + score] += below[x] * prob;
                }
            }

            (below, next) = (next, below);

            double belowMass = 0.0;
            for (int i = 0; i < below.Length; i++)
            {
                belowMass += below[i];
            }

            double reached = 1.0 - belowMass;
            reachedBy.Add(reached);
            if (reached >= 0.995)
            {
                break;
            }
        }

        double evTurns = 0.0;
        foreach (double reached in reachedBy)
        {
            evTurns += 1.0 - reached;
        }

        var pWithin = ImmutableDictionary.CreateBuilder<int, double>();
        foreach (int turns in turnsToCheck)
        {
            if (reachedBy.Count == 0)
            {
                pWithin[turns] = 0.0;
            }
            else if (turns - 1 < reachedBy.Count)
            {
                pWithin[turns] = reachedBy[turns - 1];
            }
            else
            {
                pWithin[turns] = reachedBy[^1];
            }
        }

        double p50 = double.PositiveInfinity;
        double p90 = double.PositiveInfinity;
        for (int i = 0; i < reachedBy.Count; i++)
        {
            if (double.IsPositiveInfinity(p50) && reachedBy[i] >= 0.5)
            {
                p50 = i + 1;
            }

            if (double.IsPositiveInfinity(p90) && reachedBy[i] >= 0.9)
            {
                p90 = i + 1;
            }
        }

        double evPointsFinal = 0.0;
        for (int i = 0; i < turnDistribution.Count; i++)
        {
            evPointsFinal += i * turnDistribution[i];
        }

        return new TurnMetrics(
            EvTurns: evTurns,
            PWithin: pWithin.ToImmutable(),
            EvPoints: evPointsFinal,
            P50Turns: p50,
            P90Turns: p90,
            EvPointsSe: 0.0);
    }
}
