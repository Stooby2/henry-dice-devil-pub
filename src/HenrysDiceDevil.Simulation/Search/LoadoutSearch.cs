using System.Collections.Immutable;

namespace HenrysDiceDevil.Simulation.Search;

public static class LoadoutSearch
{
    public static long CountCombinations(IReadOnlyList<int> available, int total = 6)
    {
        var dp = new long[total + 1];
        dp[0] = 1;

        foreach (int count in available)
        {
            var next = new long[total + 1];
            for (int t = 0; t <= total; t++)
            {
                if (dp[t] == 0)
                {
                    continue;
                }

                int maxTake = Math.Min(count, total - t);
                for (int k = 0; k <= maxTake; k++)
                {
                    next[t + k] += dp[t];
                }
            }

            dp = next;
        }

        return dp[total];
    }

    public static ImmutableArray<ImmutableArray<int>> EnumerateLoadouts(
        IReadOnlyList<int> available,
        int total = 6,
        int? limit = null)
    {
        var results = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        var current = new int[available.Count];

        void Recurse(int idx, int remaining)
        {
            if (limit.HasValue && results.Count >= limit.Value)
            {
                return;
            }

            if (idx == available.Count - 1)
            {
                if (remaining <= available[idx])
                {
                    current[idx] = remaining;
                    results.Add(current.ToImmutableArray());
                }

                return;
            }

            int maxTake = Math.Min(available[idx], remaining);
            for (int k = 0; k <= maxTake; k++)
            {
                current[idx] = k;
                Recurse(idx + 1, remaining - k);
                if (limit.HasValue && results.Count >= limit.Value)
                {
                    return;
                }
            }
        }

        if (available.Count > 0)
        {
            Recurse(0, total);
        }

        return results.ToImmutable();
    }

    public static ImmutableArray<ImmutableArray<int>> RandomLoadouts(
        IReadOnlyList<int> available,
        IReadOnlyList<double> qualities,
        int total,
        int limit,
        int seed)
    {
        if (available.Count != qualities.Count)
        {
            throw new ArgumentException("available and qualities must have equal lengths.");
        }

        var rng = new Random(seed);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var results = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        int tries = 0;
        int maxTries = Math.Max(limit * 50, 1);

        while (results.Count < limit && tries < maxTries)
        {
            tries++;
            var counts = new int[available.Count];

            for (int draw = 0; draw < total; draw++)
            {
                var candidates = new List<int>();
                var weights = new List<double>();
                for (int i = 0; i < available.Count; i++)
                {
                    if (counts[i] < available[i])
                    {
                        candidates.Add(i);
                        weights.Add(Math.Max(0.1, qualities[i]));
                    }
                }

                if (candidates.Count == 0)
                {
                    break;
                }

                int selected = WeightedChoice(rng, candidates, weights);
                counts[selected]++;
            }

            if (counts.Sum() != total)
            {
                continue;
            }

            string key = string.Join(",", counts);
            if (!unique.Add(key))
            {
                continue;
            }

            results.Add(counts.ToImmutableArray());
        }

        return results.ToImmutable();
    }

    private static int WeightedChoice(Random rng, IReadOnlyList<int> candidates, IReadOnlyList<double> weights)
    {
        double total = 0.0;
        for (int i = 0; i < weights.Count; i++)
        {
            total += weights[i];
        }

        double pick = rng.NextDouble() * total;
        for (int i = 0; i < candidates.Count; i++)
        {
            pick -= weights[i];
            if (pick <= 0.0)
            {
                return candidates[i];
            }
        }

        return candidates[^1];
    }
}
