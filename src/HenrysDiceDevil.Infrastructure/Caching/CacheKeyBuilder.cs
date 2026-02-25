using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HenrysDiceDevil.Infrastructure.Caching;

public static class CacheKeyBuilder
{
    public static string BuildDiceSignature(IReadOnlyList<(string Name, IReadOnlyList<double> Probs)> dice)
    {
        var payload = dice
            .Select(static d => new { name = d.Name, probs = d.Probs })
            .OrderBy(static x => x.name, StringComparer.Ordinal)
            .ToArray();
        string raw = JsonSerializer.Serialize(payload);
        return Sha256Hex(raw);
    }

    public static Dictionary<string, object?> BuildContext(
        string diceSignature,
        IDictionary<string, object?> settings)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["v"] = CacheConstants.CacheVersion,
            ["schema"] = CacheConstants.CacheSchema,
            ["dice"] = diceSignature,
            ["target"] = settings["target"],
            ["risk_profile"] = settings["risk_profile"],
            ["num_turns"] = settings["num_turns"],
            ["cap"] = settings["cap"],
        };

        if (settings.TryGetValue("seed_base", out object? seedBase) && seedBase is not null)
        {
            context["seed_base"] = Convert.ToInt32(seedBase);
        }

        return context;
    }

    public static string BuildFromContext(
        IReadOnlyDictionary<string, object?> context,
        IReadOnlyList<int> counts)
    {
        var payload = new Dictionary<string, object?>(context, StringComparer.Ordinal)
        {
            ["counts"] = counts.ToArray(),
        };
        string raw = SerializeWithSortedKeys(payload);
        return Sha256Hex(raw);
    }

    public static string Build(
        string diceSignature,
        IReadOnlyList<int> counts,
        IDictionary<string, object?> settings) =>
        BuildFromContext(BuildContext(diceSignature, settings), counts);

    private static string Sha256Hex(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SerializeWithSortedKeys(IReadOnlyDictionary<string, object?> payload)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in payload)
        {
            sorted[kvp.Key] = kvp.Value;
        }

        return JsonSerializer.Serialize(sorted);
    }
}
