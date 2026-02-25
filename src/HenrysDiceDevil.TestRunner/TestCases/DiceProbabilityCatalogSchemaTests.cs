using System.Text.Json;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class DiceProbabilityCatalogSchemaTests : ITestCase
{
    public string Name => nameof(DiceProbabilityCatalogSchemaTests);

    public void Run()
    {
        string root = RepositoryPaths.ResolveRoot();
        string path = Path.Combine(root, "data", "kcd2_dice_probabilities.json");
        AssertEx.True(File.Exists(path), "Dice probability file is missing.");

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        AssertEx.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Dice catalog root must be an object.");
        AssertEx.True(doc.RootElement.EnumerateObject().Any(), "Dice catalog should not be empty.");

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            AssertEx.True(entry.Value.ValueKind == JsonValueKind.Array, $"Dice '{entry.Name}' must be an array.");
            AssertEx.Equal(7, entry.Value.GetArrayLength(), $"Dice '{entry.Name}' must have 7 probability slots.");

            var values = entry.Value.EnumerateArray().Select(static x => x.GetDouble()).ToArray();
            AssertEx.True(Math.Abs(values[0]) <= 1e-12, $"Dice '{entry.Name}' index 0 must remain zero.");

            double sum = 0.0;
            for (int i = 1; i < values.Length; i++)
            {
                double probability = values[i];
                AssertEx.True(probability >= 0.0, $"Dice '{entry.Name}' has negative probability at side {i}.");
                AssertEx.True(probability <= 1.0, $"Dice '{entry.Name}' has invalid probability > 1 at side {i}.");
                sum += probability;
            }

            AssertEx.True(Math.Abs(1.0 - sum) <= 1e-9, $"Dice '{entry.Name}' probabilities must sum to 1.0.");
        }
    }
}
