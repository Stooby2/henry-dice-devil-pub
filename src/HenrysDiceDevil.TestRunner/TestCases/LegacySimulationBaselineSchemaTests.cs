using System.Text.Json;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class LegacySimulationBaselineSchemaTests : ITestCase
{
    public string Name => nameof(LegacySimulationBaselineSchemaTests);

    public void Run()
    {
        string root = RepositoryPaths.ResolveRoot();
        string path = Path.Combine(root, "legacy", "tests", "data", "simulation_baseline_1000_turns.json");
        AssertEx.True(File.Exists(path), "Legacy simulation baseline fixture is missing.");

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        JsonElement rootNode = doc.RootElement;
        AssertEx.True(rootNode.ValueKind == JsonValueKind.Object, "Simulation baseline root must be an object.");

        JsonElement config = Required(rootNode, "config");
        JsonElement validation = Required(rootNode, "validation");
        JsonElement results = Required(rootNode, "results");

        AssertEx.True(config.ValueKind == JsonValueKind.Object, "config must be an object.");
        AssertEx.True(validation.ValueKind == JsonValueKind.Object, "validation must be an object.");
        AssertEx.True(results.ValueKind == JsonValueKind.Array, "results must be an array.");
        AssertEx.True(results.GetArrayLength() > 0, "results must not be empty.");

        _ = Required(config, "turns").GetInt32();
        _ = Required(config, "target").GetInt32();
        _ = Required(config, "risk_profile").GetString();
        _ = Required(validation, "test_repeats").GetInt32();
        _ = Required(validation, "test_seed_base").GetInt32();

        foreach (JsonElement entry in results.EnumerateArray())
        {
            AssertEx.True(entry.ValueKind == JsonValueKind.Object, "each result entry must be an object.");
            _ = Required(entry, "id").GetString();
            JsonElement dice = Required(entry, "dice");
            AssertEx.True(dice.ValueKind == JsonValueKind.Array, "result.dice must be an array.");
            AssertEx.True(dice.GetArrayLength() == 6, "result.dice must contain exactly 6 die names.");

            _ = Required(entry, "mean_ev_points").GetDouble();
            _ = Required(entry, "delta_ev_points").GetDouble();
        }
    }

    private static JsonElement Required(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException($"Required property '{propertyName}' is missing.");
        }

        return value;
    }
}
