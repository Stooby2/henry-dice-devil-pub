using HenrysDiceDevil.Simulation.Runtime;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class DpMetricsTests : ITestCase
{
    public string Name => nameof(DpMetricsTests);

    public void Run()
    {
        int target = 200;
        var dist = new double[201];
        dist[0] = 0.5;
        dist[200] = 0.5;

        var metrics = DpMetricsComputer.Compute(dist, target, maxTurns: 5, probTurns: new[] { 1, 2 });
        AssertEx.True(Math.Abs(metrics.PWithin[1] - 0.5) <= 1e-9, "DP reach-by turn 1 should be 0.5.");
        AssertEx.True(Math.Abs(metrics.PWithin[2] - 0.75) <= 1e-9, "DP reach-by turn 2 should be 0.75.");

        var zeroTargetMetrics = DpMetricsComputer.Compute(dist, target: 0, maxTurns: 5, probTurns: new[] { 1, 2 });
        AssertEx.True(zeroTargetMetrics.EvTurns == 0.0, "Target <= 0 should yield zero expected turns.");
        AssertEx.True(zeroTargetMetrics.PWithin[1] == 1.0, "Target <= 0 should yield full probability within any positive turn count.");
    }
}
