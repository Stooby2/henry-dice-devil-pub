using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class ArchitectureSurfaceTests : ITestCase
{
    public string Name => nameof(ArchitectureSurfaceTests);

    public void Run()
    {
        string root = RepositoryPaths.ResolveRoot();

        string[] requiredProjects =
        {
            Path.Combine(root, "src", "HenrysDiceDevil.App", "HenrysDiceDevil.App.csproj"),
            Path.Combine(root, "src", "HenrysDiceDevil.Domain", "HenrysDiceDevil.Domain.csproj"),
            Path.Combine(root, "src", "HenrysDiceDevil.Simulation", "HenrysDiceDevil.Simulation.csproj"),
            Path.Combine(root, "src", "HenrysDiceDevil.Infrastructure", "HenrysDiceDevil.Infrastructure.csproj"),
            Path.Combine(root, "src", "HenrysDiceDevil.TestRunner", "HenrysDiceDevil.TestRunner.csproj"),
            Path.Combine(root, "benchmarks", "HenrysDiceDevil.Benchmarks", "HenrysDiceDevil.Benchmarks.csproj"),
        };

        foreach (string projectPath in requiredProjects)
        {
            AssertEx.True(File.Exists(projectPath), $"Expected project is missing: {projectPath}");
        }
    }
}
