using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class EfficiencyPlanValidatorTests : ITestCase
{
    public string Name => nameof(EfficiencyPlanValidatorTests);

    public void Run()
    {
        var validPlan = new[]
        {
            new EfficiencyStage(100_000, 100, 30, 0.10, 100),
            new EfficiencyStage(10_000, 500, 10, 0.10, 100),
            new EfficiencyStage(0, 100_000, 10, 0.01, 100),
        };

        var noErrors = EfficiencyPlanValidator.Validate(validPlan);
        AssertEx.Equal(0, noErrors.Length, "Valid efficiency plan should not produce errors.");

        var invalidPlan = new[]
        {
            new EfficiencyStage(1_000, 500, 10, 0.01, 100),
            new EfficiencyStage(5_000, 500, 10, -1.0, 0),
        };

        var errors = EfficiencyPlanValidator.Validate(invalidPlan);
        AssertEx.True(errors.Length >= 3, "Invalid efficiency plan should report multiple errors.");

        var legacyRow = EfficiencyPlanNormalizer.LegacyRow(pilotTurns: 0, keepPercent: 0.0, epsilon: -1.0, minSurvivors: 0);
        AssertEx.Equal(1, legacyRow.PilotTurns, "Legacy row should clamp pilot turns to >= 1.");
        AssertEx.Equal(0.01, legacyRow.KeepPercent, "Legacy row should clamp keep percent to >= 0.01.");
        AssertEx.Equal(0.0, legacyRow.Epsilon, "Legacy row should clamp epsilon to >= 0.");
        AssertEx.Equal(1, legacyRow.MinSurvivors, "Legacy row should clamp min survivors to >= 1.");

        var raw = new List<Dictionary<string, object?>>
        {
            new() { ["min_total"] = 1000, ["pilot_turns"] = 100, ["keep_percent"] = 20.0, ["epsilon"] = 0.1, ["min_survivors"] = 50 },
            new() { ["min_total"] = "100", ["pilot_turns"] = "200", ["keep_percent"] = "10.5", ["epsilon"] = "0.01", ["min_survivors"] = "5" },
            new() { ["pilot_turns"] = "not-a-number" },
        };
        var (normalized, normalizeErrors) = EfficiencyPlanNormalizer.Normalize(raw);
        AssertEx.Equal(2, normalized.Length, "Only valid numeric rows should be normalized.");
        AssertEx.Equal(1, normalizeErrors.Length, "Invalid numeric rows should return an error.");
    }
}
