using System.Collections.Immutable;

namespace HenrysDiceDevil.Simulation.Scoring;

public sealed class ScoringGroupEngine
{
    private const int Faces = 6;
    private const int MaxDice = 6;
    private static readonly ImmutableArray<ScoreSelection>[] Precomputed = BuildPrecomputedSelections();

    public ImmutableArray<ScoreSelection> ScoreGroupsForCounts(IReadOnlyList<int> counts)
    {
        if (counts.Count != Faces)
        {
            throw new ArgumentException("Face counts must contain 6 entries.", nameof(counts));
        }

        int total = 0;
        for (int i = 0; i < Faces; i++)
        {
            int value = counts[i];
            if (value < 0 || value > MaxDice)
            {
                throw new ArgumentOutOfRangeException(nameof(counts), "Face counts must be in [0, 6].");
            }

            total += value;
        }

        if (total > MaxDice)
        {
            throw new ArgumentOutOfRangeException(nameof(counts), "Total dice in face counts cannot exceed 6.");
        }

        int key = PackCountsKey(counts);
        var result = Precomputed[key];
        return result.IsDefault ? [] : result;
    }

    public ImmutableArray<ScoreSelection> ScoreGroupsForCounts(ReadOnlySpan<int> counts)
    {
        if (counts.Length != Faces)
        {
            throw new ArgumentException("Face counts must contain 6 entries.", nameof(counts));
        }

        int total = 0;
        for (int i = 0; i < Faces; i++)
        {
            int value = counts[i];
            if (value < 0 || value > MaxDice)
            {
                throw new ArgumentOutOfRangeException(nameof(counts), "Face counts must be in [0, 6].");
            }

            total += value;
        }

        if (total > MaxDice)
        {
            throw new ArgumentOutOfRangeException(nameof(counts), "Total dice in face counts cannot exceed 6.");
        }

        int key = PackCountsKey(counts);
        var result = Precomputed[key];
        return result.IsDefault ? [] : result;
    }

    public ImmutableArray<ScoreSelection> ScoreGroupsForPackedKey(int key)
    {
        var result = Precomputed[key];
        return result.IsDefault ? [] : result;
    }

    private static ImmutableArray<ScoreSelection>[] BuildPrecomputedSelections()
    {
        var table = new ImmutableArray<ScoreSelection>[1 << 18];
        var counts = new int[Faces];

        for (int a = 0; a <= MaxDice; a++)
        {
            counts[0] = a;
            for (int b = 0; b <= MaxDice - a; b++)
            {
                counts[1] = b;
                for (int c = 0; c <= MaxDice - a - b; c++)
                {
                    counts[2] = c;
                    for (int d = 0; d <= MaxDice - a - b - c; d++)
                    {
                        counts[3] = d;
                        for (int e = 0; e <= MaxDice - a - b - c - d; e++)
                        {
                            counts[4] = e;
                            for (int f = 0; f <= MaxDice - a - b - c - d - e; f++)
                            {
                                counts[5] = f;
                                int key = PackCountsKey(counts);
                                table[key] = BuildSelections(counts);
                            }
                        }
                    }
                }
            }
        }

        return table;
    }

    private static ImmutableArray<ScoreSelection> BuildSelections(IReadOnlyList<int> counts)
    {
        int[] c = { 0, counts[0], counts[1], counts[2], counts[3], counts[4], counts[5] };
        var groups = new List<ScoreSelection>();

        foreach (var (face, singleScore) in new[] { (1, 100), (5, 50) })
        {
            if (c[face] > 0)
            {
                for (int n = 1; n <= c[face]; n++)
                {
                    int[] used = new int[6];
                    used[face - 1] = n;
                    string tag = $"single_{face}";
                    groups.Add(
                        new ScoreSelection(
                            UsedCounts: used.ToImmutableArray(),
                            UsedDice: n,
                            Points: n * singleScore,
                            Tags: ImmutableArray.Create(new KeyValuePair<string, int>(tag, n))));
                }
            }
        }

        for (int face = 1; face <= 6; face++)
        {
            if (c[face] >= 3)
            {
                int baseScore = face == 1 ? 1000 : face * 100;
                for (int n = 3; n <= c[face]; n++)
                {
                    int[] used = new int[6];
                    used[face - 1] = n;
                    int mult = n - 2;
                    string tag = $"kind_{face}_{n}ok";
                    groups.Add(
                        new ScoreSelection(
                            UsedCounts: used.ToImmutableArray(),
                            UsedDice: n,
                            Points: baseScore * mult,
                            Tags: ImmutableArray.Create(new KeyValuePair<string, int>(tag, 1))));
                }
            }
        }

        if (Enumerable.Range(1, 5).All(i => c[i] >= 1))
        {
            int[] used = { 1, 1, 1, 1, 1, 0 };
            groups.Add(
                new ScoreSelection(
                    used.ToImmutableArray(),
                    UsedDice: 5,
                    Points: 500,
                    Tags: ImmutableArray.Create(new KeyValuePair<string, int>("straight_1_5", 1))));
        }

        if (Enumerable.Range(2, 5).All(i => c[i] >= 1))
        {
            int[] used = { 0, 1, 1, 1, 1, 1 };
            groups.Add(
                new ScoreSelection(
                    used.ToImmutableArray(),
                    UsedDice: 5,
                    Points: 750,
                    Tags: ImmutableArray.Create(new KeyValuePair<string, int>("straight_2_6", 1))));
        }

        if (Enumerable.Range(1, 6).All(i => c[i] >= 1))
        {
            int[] used = { 1, 1, 1, 1, 1, 1 };
            groups.Add(
                new ScoreSelection(
                    used.ToImmutableArray(),
                    UsedDice: 6,
                    Points: 1500,
                    Tags: ImmutableArray.Create(new KeyValuePair<string, int>("straight_1_6", 1))));
        }

        if (groups.Count == 0)
        {
            return [];
        }

        var selections = new List<ScoreSelection>();
        var usedCounts = new int[6];
        var tags = new List<KeyValuePair<string, int>>();

        void Dfs(int idx, int points)
        {
            if (idx >= groups.Count)
            {
                if (points > 0)
                {
                    selections.Add(
                        new ScoreSelection(
                            UsedCounts: usedCounts.ToImmutableArray(),
                            UsedDice: usedCounts.Sum(),
                            Points: points,
                            Tags: tags.ToImmutableArray()));
                }

                return;
            }

            Dfs(idx + 1, points);

            var group = groups[idx];
            bool canTake = true;
            for (int i = 0; i < 6; i++)
            {
                if ((usedCounts[i] + group.UsedCounts[i]) > counts[i])
                {
                    canTake = false;
                    break;
                }
            }

            if (!canTake)
            {
                return;
            }

            for (int i = 0; i < 6; i++)
            {
                usedCounts[i] += group.UsedCounts[i];
            }

            tags.AddRange(group.Tags);
            Dfs(idx + 1, points + group.Points);
            tags.RemoveRange(tags.Count - group.Tags.Length, group.Tags.Length);

            for (int i = 0; i < 6; i++)
            {
                usedCounts[i] -= group.UsedCounts[i];
            }
        }

        Dfs(0, 0);

        var unique = new Dictionary<string, ScoreSelection>(StringComparer.Ordinal);
        foreach (ScoreSelection selection in selections)
        {
            string key = $"{string.Join(",", selection.UsedCounts)}|{selection.Points}|{string.Join(";", selection.Tags.Select(x => $"{x.Key}:{x.Value}"))}";
            unique[key] = selection;
        }

        return unique.Values.ToImmutableArray();
    }

    private static int PackCountsKey(IReadOnlyList<int> counts)
    {
        int key = 0;
        for (int i = 0; i < Faces; i++)
        {
            key |= (counts[i] & 0x7) << (i * 3);
        }

        return key;
    }

    private static int PackCountsKey(ReadOnlySpan<int> counts)
    {
        int key = 0;
        for (int i = 0; i < Faces; i++)
        {
            key |= (counts[i] & 0x7) << (i * 3);
        }

        return key;
    }
}
