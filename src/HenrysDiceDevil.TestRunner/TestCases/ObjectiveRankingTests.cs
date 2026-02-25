using System.Collections.Immutable;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Simulation.Contracts;
using HenrysDiceDevil.Simulation.Optimization;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class ObjectiveRankingTests : ITestCase
{
    public string Name => nameof(ObjectiveRankingTests);

    public void Run()
    {
        var resultA = BuildResult(evTurns: 12.0, evPoints: 400.0, tags: new Dictionary<string, int> { ["single_1"] = 20 }, totalGroups: 100);
        var resultB = BuildResult(evTurns: 11.5, evPoints: 380.0, tags: new Dictionary<string, int> { ["single_1"] = 10 }, totalGroups: 100);

        var maxA = ObjectiveRanking.RankKey(resultA, OptimizationObjective.MaxScore);
        var maxB = ObjectiveRanking.RankKey(resultB, OptimizationObjective.MaxScore);
        AssertEx.True(maxB.Primary < maxA.Primary, "MaxScore should prioritize lower expected turns.");

        double singleA = ObjectiveRanking.ObjectiveScore(resultA, OptimizationObjective.SingleOne);
        double singleB = ObjectiveRanking.ObjectiveScore(resultB, OptimizationObjective.SingleOne);
        AssertEx.True(singleA > singleB, "SingleOne objective score should follow single_1 tag ratio.");

        var grouped = ResultPresentation.GroupedHandPercentages(
            BuildResult(
                evTurns: 10,
                evPoints: 500,
                tags: new Dictionary<string, int>
                {
                    ["single_1"] = 10,
                    ["kind_1_3ok"] = 5,
                    ["kind_2_4ok"] = 3,
                    ["kind_3_5ok"] = 2,
                    ["kind_4_6ok"] = 1,
                    ["straight_1_5"] = 4,
                    ["straight_1_6"] = 2,
                },
                totalGroups: 27));
        AssertEx.Equal(37, grouped["1_ok"], "Grouped 1_ok percentage mismatch.");
        AssertEx.Equal(19, grouped["3_ok"], "Grouped 3_ok percentage mismatch.");
        AssertEx.Equal(11, grouped["4_ok"], "Grouped 4_ok percentage mismatch.");
        AssertEx.Equal(7, grouped["5_ok"], "Grouped 5_ok percentage mismatch.");
        AssertEx.Equal(4, grouped["6_ok"], "Grouped 6_ok percentage mismatch.");
        AssertEx.Equal(15, grouped["5_s"], "Grouped 5_s percentage mismatch.");
        AssertEx.Equal(7, grouped["6_s"], "Grouped 6_s percentage mismatch.");
    }

    private static SimulationResult BuildResult(double evTurns, double evPoints, IReadOnlyDictionary<string, int> tags, int totalGroups)
    {
        var metrics = new TurnMetrics(
            EvTurns: evTurns,
            PWithin: ImmutableDictionary<int, double>.Empty,
            EvPoints: evPoints,
            P50Turns: 0,
            P90Turns: 0,
            EvPointsSe: 0);
        return new SimulationResult(
            Counts: [6, 0],
            Metrics: metrics,
            MeanPoints: evPoints,
            StandardDeviation: 0,
            TagCounts: tags,
            TotalGroups: totalGroups,
            ScoringTurns: 0);
    }
}
