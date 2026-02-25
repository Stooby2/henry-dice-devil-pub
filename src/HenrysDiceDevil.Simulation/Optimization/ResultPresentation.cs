using HenrysDiceDevil.Simulation.Contracts;

namespace HenrysDiceDevil.Simulation.Optimization;

public static class ResultPresentation
{
    public static IReadOnlyDictionary<string, int> GroupedHandPercentages(SimulationResult result)
    {
        var tags = result.TagCounts;
        int totalGroups = Math.Max(1, result.TotalGroups);

        int oneOk = 0;
        int threeOk = 0;
        int fourOk = 0;
        int fiveOk = 0;
        int sixOk = 0;
        int fiveStraight = 0;
        int sixStraight = 0;

        foreach (var tag in tags)
        {
            if (tag.Key is "single_1" or "single_5")
            {
                oneOk += tag.Value;
            }
            else if (tag.Key.EndsWith("_3ok", StringComparison.Ordinal))
            {
                threeOk += tag.Value;
            }
            else if (tag.Key.EndsWith("_4ok", StringComparison.Ordinal))
            {
                fourOk += tag.Value;
            }
            else if (tag.Key.EndsWith("_5ok", StringComparison.Ordinal))
            {
                fiveOk += tag.Value;
            }
            else if (tag.Key.EndsWith("_6ok", StringComparison.Ordinal))
            {
                sixOk += tag.Value;
            }
            else if (tag.Key is "straight_1_5" or "straight_2_6")
            {
                fiveStraight += tag.Value;
            }
            else if (tag.Key == "straight_1_6")
            {
                sixStraight += tag.Value;
            }
        }

        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1_ok"] = (int)Math.Round(100.0 * oneOk / totalGroups),
            ["3_ok"] = (int)Math.Round(100.0 * threeOk / totalGroups),
            ["4_ok"] = (int)Math.Round(100.0 * fourOk / totalGroups),
            ["5_ok"] = (int)Math.Round(100.0 * fiveOk / totalGroups),
            ["6_ok"] = (int)Math.Round(100.0 * sixOk / totalGroups),
            ["5_s"] = (int)Math.Round(100.0 * fiveStraight / totalGroups),
            ["6_s"] = (int)Math.Round(100.0 * sixStraight / totalGroups),
        };
    }
}
