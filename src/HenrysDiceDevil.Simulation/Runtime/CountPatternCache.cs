namespace HenrysDiceDevil.Simulation.Runtime;

internal static class CountPatternCache
{
    private static readonly Dictionary<int, IReadOnlyList<(int[] Counts, long Coeff)>> PatternsByDice = Build();

    public static IReadOnlyList<(int[] Counts, long Coeff)> Get(int numDice)
    {
        if (!PatternsByDice.TryGetValue(numDice, out var patterns))
        {
            return [];
        }

        return patterns;
    }

    private static Dictionary<int, IReadOnlyList<(int[] Counts, long Coeff)>> Build()
    {
        var byDice = new Dictionary<int, IReadOnlyList<(int[] Counts, long Coeff)>>();
        for (int k = 1; k <= 6; k++)
        {
            var patterns = new List<(int[] Counts, long Coeff)>();
            var counts = new int[6];

            void Recurse(int faceIdx, int remaining)
            {
                if (faceIdx == 5)
                {
                    counts[faceIdx] = remaining;
                    long coeff = Factorial(k);
                    for (int i = 0; i < 6; i++)
                    {
                        coeff /= Factorial(counts[i]);
                    }

                    patterns.Add((counts.ToArray(), coeff));
                    return;
                }

                for (int n = 0; n <= remaining; n++)
                {
                    counts[faceIdx] = n;
                    Recurse(faceIdx + 1, remaining - n);
                }
            }

            Recurse(0, k);
            byDice[k] = patterns;
        }

        return byDice;
    }

    private static long Factorial(int n)
    {
        long result = 1;
        for (int i = 2; i <= n; i++)
        {
            result *= i;
        }

        return result;
    }
}
