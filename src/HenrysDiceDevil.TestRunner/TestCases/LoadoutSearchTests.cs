using HenrysDiceDevil.Simulation.Search;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class LoadoutSearchTests : ITestCase
{
    public string Name => nameof(LoadoutSearchTests);

    public void Run()
    {
        var available = new[] { 2, 2, 2 };
        long count = LoadoutSearch.CountCombinations(available, total: 3);
        AssertEx.Equal(7L, count, "Combination count should match bounded stars-and-bars outcome.");

        var enumerated = LoadoutSearch.EnumerateLoadouts(available, total: 3);
        AssertEx.Equal(7, enumerated.Length, "Enumerated loadouts count should match count-combinations result.");
        AssertEx.True(enumerated.All(x => x.Sum() == 3), "All enumerated loadouts must sum to total dice.");

        var qualities = new[] { 100.0, 50.0, 20.0 };
        var sampleA = LoadoutSearch.RandomLoadouts(available, qualities, total: 3, limit: 5, seed: 1234);
        var sampleB = LoadoutSearch.RandomLoadouts(available, qualities, total: 3, limit: 5, seed: 1234);
        AssertEx.Equal(sampleA.Length, sampleB.Length, "Deterministic sampling should return the same number of loadouts.");

        string keyA = string.Join("|", sampleA.Select(static x => string.Join(",", x)));
        string keyB = string.Join("|", sampleB.Select(static x => string.Join(",", x)));
        AssertEx.Equal(keyA, keyB, "Deterministic sampling should return the same ordered loadouts for the same seed.");

        double quality = DiceQuality.FromProbabilities(new[] { 0.0, 0.30, 0.10, 0.10, 0.10, 0.20, 0.20 });
        AssertEx.True(Math.Abs(quality - 50.0) <= 1e-9, "Die quality calculation should match legacy weighting formula.");
    }
}
