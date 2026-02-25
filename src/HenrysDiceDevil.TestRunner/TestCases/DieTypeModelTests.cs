using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class DieTypeModelTests : ITestCase
{
    public string Name => nameof(DieTypeModelTests);

    public void Run()
    {
        var valid = new[] { 0.0, 1d / 6, 1d / 6, 1d / 6, 1d / 6, 1d / 6, 1d / 6 };
        var die = new DieType("Ordinary die", valid, quality: 0.0);
        AssertEx.Equal("Ordinary die", die.Name, "Die type should preserve name.");
        AssertEx.Equal(7, die.Probabilities.Length, "Die type should preserve 7 probabilities.");

        bool threw = false;
        try
        {
            _ = new DieType("Broken", new[] { 0.0, 0.5, 0.5 }, quality: 0.0);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        AssertEx.True(threw, "Invalid probability length should throw.");
    }
}
