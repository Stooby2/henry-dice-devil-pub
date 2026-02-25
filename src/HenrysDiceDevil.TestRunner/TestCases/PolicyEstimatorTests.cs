using System.Collections.Immutable;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Simulation.Contracts;
using HenrysDiceDevil.Simulation.Runtime;
using HenrysDiceDevil.Simulation.Scoring;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class PolicyEstimatorTests : ITestCase
{
    public string Name => nameof(PolicyEstimatorTests);

    public void Run()
    {
        var scoring = new ScoringGroupEngine();
        var estimator = new PolicyEstimator(scoring);

        var fair = new[] { 1d / 6, 1d / 6, 1d / 6, 1d / 6, 1d / 6, 1d / 6 };
        var (bust1, ev1) = estimator.EstimateBustAndEvExact(fair, 1);
        AssertEx.True(Math.Abs(bust1 - (4.0 / 6.0)) <= 1e-12, "Exact estimator bust probability for fair single die is incorrect.");
        AssertEx.True(Math.Abs(ev1 - 25.0) <= 1e-12, "Exact estimator EV for fair single die is incorrect.");

        var probs = new[] { 0.30, 0.15, 0.05, 0.10, 0.20, 0.20 };
        var exact = estimator.EstimateBustAndEvExact(probs, 3);
        var brute = BruteForce(scoring, probs, 3);
        AssertEx.True(Math.Abs(exact.BustProbability - brute.BustProbability) <= 1e-12, "Exact estimator bust mismatch vs brute force.");
        AssertEx.True(Math.Abs(exact.EvPoints - brute.EvPoints) <= 1e-12, "Exact estimator EV mismatch vs brute force.");

        var dieA = new DieType("A", new[] { 0.0, 0.35, 0.13, 0.13, 0.13, 0.13, 0.13 }, 0);
        var dieB = new DieType("B", new[] { 0.0, 0.20, 0.16, 0.16, 0.16, 0.16, 0.16 }, 0);
        var settings = new OptimizationSettings(
            TargetScore: 3000,
            TurnCap: 3000,
            NumTurns: 200,
            RiskProfile: RiskProfile.Balanced,
            Objective: OptimizationObjective.MaxScore,
            ProbTurns: ImmutableArray.Create(10, 15, 20),
            EfficiencyEnabled: false,
            EfficiencySeed: 1,
            EfficiencyPlan: []);

        var engine = new TurnSimulationEngine();
        var request = new SimulationRequest(
            DiceCatalog: new[] { dieA, dieB },
            Counts: new[] { 3, 3 },
            Settings: settings,
            SeedBase: 123456);
        var run1 = engine.Run(request);
        var run2 = engine.Run(request);
        AssertEx.True(Math.Abs(run1.MeanPoints - run2.MeanPoints) <= 1e-12, "Seeded simulation should be deterministic.");
        AssertEx.True(Math.Abs(run1.StandardDeviation - run2.StandardDeviation) <= 1e-12, "Seeded simulation stddev should be deterministic.");
    }

    private static (double BustProbability, double EvPoints) BruteForce(ScoringGroupEngine scoring, double[] probs, int numDice)
    {
        double bust = 0.0;
        double ev = 0.0;

        int outcomes = (int)Math.Pow(6, numDice);
        for (int code = 0; code < outcomes; code++)
        {
            int x = code;
            var counts = new int[6];
            double weight = 1.0;
            for (int i = 0; i < numDice; i++)
            {
                int face = x % 6;
                x /= 6;
                counts[face]++;
                weight *= probs[face];
            }

            var selections = scoring.ScoreGroupsForCounts(counts);
            if (selections.Length == 0)
            {
                bust += weight;
            }
            else
            {
                ev += weight * selections.Max(static s => s.Points);
            }
        }

        return (bust, ev);
    }
}
