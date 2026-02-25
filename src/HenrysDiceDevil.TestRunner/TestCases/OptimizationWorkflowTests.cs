using System.Collections.Immutable;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Infrastructure.Caching;
using HenrysDiceDevil.Simulation.Optimization;
using HenrysDiceDevil.Simulation.Runtime;
using HenrysDiceDevil.Simulation.Workers;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class OptimizationWorkflowTests : ITestCase
{
    public string Name => nameof(OptimizationWorkflowTests);

    public void Run()
    {
        string root = RepositoryPaths.ResolveRoot();
        string cacheRoot = Path.Combine(root, "cache", "test-workflow");
        if (Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }

        var diceCatalog = new[]
        {
            new DieType("A", new[] { 0.0, 0.30, 0.14, 0.14, 0.14, 0.14, 0.14 }, 60),
            new DieType("B", new[] { 0.0, 0.20, 0.16, 0.16, 0.16, 0.16, 0.16 }, 40),
        };
        var loadouts = new IReadOnlyList<int>[]
        {
            new[] { 6, 0 },
            new[] { 5, 1 },
            new[] { 4, 2 },
            new[] { 3, 3 },
            new[] { 2, 4 },
            new[] { 1, 5 },
        };

        var settings = new OptimizationSettings(
            TargetScore: 3000,
            TurnCap: 3000,
            NumTurns: 250,
            RiskProfile: RiskProfile.Balanced,
            Objective: OptimizationObjective.MaxScore,
            ProbTurns: ImmutableArray.Create(10, 15, 20),
            EfficiencyEnabled: true,
            EfficiencySeed: 101,
            EfficiencyPlan:
            [
                new EfficiencyStage(MinTotal: 3, PilotTurns: 120, KeepPercent: 50, Epsilon: 0.05, MinSurvivors: 2),
                new EfficiencyStage(MinTotal: 0, PilotTurns: 250, KeepPercent: 100, Epsilon: 0.0, MinSurvivors: 1),
            ]);

        using var store = new FileResultCacheStore(cacheRoot);
        var workflow = new OptimizationWorkflow(
            new LoadoutEvaluator(new TurnSimulationEngine()),
            store);

        var progressEvents = new List<OptimizationProgress>();
        var progress = new CollectingProgress(progressEvents);
        var run1 = workflow.Run(loadouts, diceCatalog, settings, progress);
        var run2 = workflow.Run(loadouts, diceCatalog, settings);

        AssertEx.True(run1.StageCount >= 1, "Efficiency workflow should execute at least one stage.");
        AssertEx.True(run1.FinalCandidateCount >= 1, "Efficiency workflow should retain at least one candidate.");
        AssertEx.True(run1.Telemetry.Stages.Count >= 1, "Workflow should emit stage telemetry.");
        AssertEx.True(run1.Telemetry.TotalEvaluatedLoadouts >= run1.FinalCandidateCount, "Telemetry evaluated-count should cover survivors.");
        AssertEx.True(progressEvents.Count > 0, "Workflow should emit progress events when progress callback is supplied.");
        AssertEx.Equal(run1.FinalCandidateCount, run2.FinalCandidateCount, "Workflow should be deterministic on repeated runs.");
        string key1 = string.Join("|", run1.Results.Select(static r => string.Join(",", r.Counts)));
        string key2 = string.Join("|", run2.Results.Select(static r => string.Join(",", r.Counts)));
        AssertEx.Equal(key1, key2, "Workflow survivor loadouts should be deterministic.");

        AssertEx.True(run2.Telemetry.TotalCacheHits > 0, "Second workflow run should observe cache hits.");
        AssertEx.True(run2.Telemetry.TotalCacheHitRate > 0.0, "Second workflow run should report positive cache hit rate.");

        var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();
        AssertEx.Throws<OperationCanceledException>(
            () => workflow.Run(loadouts, diceCatalog, settings, cancellationToken: canceledCts.Token),
            "Workflow should throw when cancellation is requested before run starts.");

        if (Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private sealed class CollectingProgress : IProgress<OptimizationProgress>
    {
        private readonly List<OptimizationProgress> _events;

        public CollectingProgress(List<OptimizationProgress> events)
        {
            _events = events;
        }

        public void Report(OptimizationProgress value)
        {
            _events.Add(value);
        }
    }
}
