using HenrysDiceDevil.Tests.TestCases;

namespace HenrysDiceDevil.Tests;

internal static class Program
{
    private static int Main()
    {
        var tests = new ITestCase[]
        {
            new DiceProbabilityCatalogSchemaTests(),
            new LegacySimulationBaselineSchemaTests(),
            new GroundTruthInventoryBenchmarkTests(),
            new EfficiencyPlanValidatorTests(),
            new DieTypeModelTests(),
            new LoadoutSearchTests(),
            new ScoringGroupEngineTests(),
            new PolicyEstimatorTests(),
            new DpMetricsTests(),
            new LoadoutEvaluatorTests(),
            new CacheKeyBuilderTests(),
            new CacheStoreTests(),
            new OptimizationWorkflowTests(),
            new ObjectiveRankingTests(),
            new ArchitectureSurfaceTests(),
        };

        int failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL {test.Name}");
                Console.WriteLine(ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Executed {tests.Length} tests with {failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }
}
