using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Simulation.Contracts;

namespace HenrysDiceDevil.Simulation.Optimization;

public static class ObjectiveRanking
{
    public static double ObjectiveScore(SimulationResult result, OptimizationObjective objective)
    {
        if (objective == OptimizationObjective.MaxScore)
        {
            return 0.0;
        }

        int totalGroups = Math.Max(1, result.TotalGroups);
        var tags = result.TagCounts;

        return objective switch
        {
            OptimizationObjective.SingleOne => tags.GetValueOrDefault("single_1", 0) / (double)totalGroups,
            OptimizationObjective.SingleFive => tags.GetValueOrDefault("single_5", 0) / (double)totalGroups,
            OptimizationObjective.Straight6 => tags.GetValueOrDefault("straight_1_6", 0) / (double)totalGroups,
            OptimizationObjective.Straight1To5 => tags.GetValueOrDefault("straight_1_5", 0) / (double)totalGroups,
            OptimizationObjective.Straight2To6 => tags.GetValueOrDefault("straight_2_6", 0) / (double)totalGroups,
            OptimizationObjective.Straight1To6 => tags.GetValueOrDefault("straight_1_6", 0) / (double)totalGroups,
            OptimizationObjective.Straight => (
                tags.GetValueOrDefault("straight_1_5", 0)
                + tags.GetValueOrDefault("straight_2_6", 0)
                + tags.GetValueOrDefault("straight_1_6", 0)) / (double)totalGroups,
            OptimizationObjective.Kind3PlusOnes => SumKind(tags, "kind_1_") / (double)totalGroups,
            OptimizationObjective.Kind3PlusTwos => SumKind(tags, "kind_2_") / (double)totalGroups,
            OptimizationObjective.Kind3PlusThrees => SumKind(tags, "kind_3_") / (double)totalGroups,
            OptimizationObjective.Kind3PlusFours => SumKind(tags, "kind_4_") / (double)totalGroups,
            OptimizationObjective.Kind3PlusFives => SumKind(tags, "kind_5_") / (double)totalGroups,
            OptimizationObjective.Kind3PlusSixes => SumKind(tags, "kind_6_") / (double)totalGroups,
            _ => 0.0,
        };
    }

    public static (double Primary, double Secondary) RankKey(SimulationResult result, OptimizationObjective objective)
    {
        if (objective == OptimizationObjective.MaxScore)
        {
            return (result.Metrics.EvTurns, -result.Metrics.EvPoints);
        }

        return (-ObjectiveScore(result, objective), result.Metrics.EvTurns);
    }

    private static int SumKind(IReadOnlyDictionary<string, int> tags, string prefix)
    {
        int sum = 0;
        foreach (var tag in tags)
        {
            if (tag.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                sum += tag.Value;
            }
        }

        return sum;
    }
}
