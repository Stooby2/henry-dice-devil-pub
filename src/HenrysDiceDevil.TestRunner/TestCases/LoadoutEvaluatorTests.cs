using System.Collections.Immutable;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Simulation.Runtime;
using HenrysDiceDevil.Simulation.Workers;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class LoadoutEvaluatorTests : ITestCase
{
    public string Name => nameof(LoadoutEvaluatorTests);

    public void Run()
    {
        var diceCatalog = new[]
        {
            new DieType("A", new[] { 0.0, 0.30, 0.14, 0.14, 0.14, 0.14, 0.14 }, 0),
            new DieType("B", new[] { 0.0, 0.20, 0.16, 0.16, 0.16, 0.16, 0.16 }, 0),
        };
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

        var evaluator = new LoadoutEvaluator(new TurnSimulationEngine());
        var countsA = new[] { 3, 3 };
        var single = evaluator.EvaluateSingle(countsA, diceCatalog, settings, seedBase: 42);
        var batch = evaluator.EvaluateBatch(new[] { countsA, new[] { 2, 4 } }, diceCatalog, settings, seedBase: 42);
        AssertEx.True(Math.Abs(single.MeanPoints - batch[0].MeanPoints) <= 1e-12, "Single and batch evaluation should match for same loadout/settings/seed.");
        AssertEx.True(Math.Abs(single.StandardDeviation - batch[0].StandardDeviation) <= 1e-12, "Single and batch stddev should match.");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool canceled = false;
        try
        {
            _ = evaluator.EvaluateBatch(new[] { countsA }, diceCatalog, settings, seedBase: 42, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        AssertEx.True(canceled, "Batch evaluation should honor cancellation token.");
    }
}
