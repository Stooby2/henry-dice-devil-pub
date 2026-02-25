using HenrysDiceDevil.Simulation.Scoring;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class ScoringGroupEngineTests : ITestCase
{
    public string Name => nameof(ScoringGroupEngineTests);

    public void Run()
    {
        var engine = new ScoringGroupEngine();

        var singleOnesAndFives = engine.ScoreGroupsForCounts(new[] { 2, 0, 0, 0, 2, 0 });
        var pointsA = singleOnesAndFives.Select(static x => x.Points).ToHashSet();
        AssertEx.True(pointsA.Contains(100), "Expected single one score.");
        AssertEx.True(pointsA.Contains(200), "Expected double one score.");
        AssertEx.True(pointsA.Contains(50), "Expected single five score.");
        AssertEx.True(pointsA.Contains(150), "Expected one+five combination score.");
        AssertEx.True(pointsA.Contains(300), "Expected double one + double five combination score.");

        var threeOnes = engine.ScoreGroupsForCounts(new[] { 3, 0, 0, 0, 0, 0 });
        AssertEx.True(threeOnes.Any(static x => x.Points == 1000), "Expected 3-of-a-kind ones score.");

        var threeTwos = engine.ScoreGroupsForCounts(new[] { 0, 3, 0, 0, 0, 0 });
        AssertEx.True(threeTwos.Any(static x => x.Points == 200), "Expected 3-of-a-kind twos score.");

        var fourOnes = engine.ScoreGroupsForCounts(new[] { 4, 0, 0, 0, 0, 0 });
        AssertEx.True(fourOnes.Any(static x => x.Points == 2000), "Expected 4-of-a-kind ones score.");

        var straight15 = engine.ScoreGroupsForCounts(new[] { 1, 1, 1, 1, 1, 0 });
        AssertEx.True(straight15.Any(static x => x.Points == 500), "Expected straight 1-5 score.");

        var straight26 = engine.ScoreGroupsForCounts(new[] { 0, 1, 1, 1, 1, 1 });
        AssertEx.True(straight26.Any(static x => x.Points == 750), "Expected straight 2-6 score.");

        var straight16 = engine.ScoreGroupsForCounts(new[] { 1, 1, 1, 1, 1, 1 });
        AssertEx.True(straight16.Any(static x => x.Points == 1500), "Expected straight 1-6 score.");
    }
}
