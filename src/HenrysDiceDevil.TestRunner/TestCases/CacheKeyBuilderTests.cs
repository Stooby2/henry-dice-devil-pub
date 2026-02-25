using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HenrysDiceDevil.Infrastructure.Caching;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class CacheKeyBuilderTests : ITestCase
{
    public string Name => nameof(CacheKeyBuilderTests);

    public void Run()
    {
        const string diceSignature = "abc123";
        int[] counts = [0, 1, 2, 0];
        var settings = new Dictionary<string, object?>
        {
            ["target"] = 3000,
            ["risk_profile"] = "Balanced",
            ["num_turns"] = 10000,
            ["cap"] = 3000,
            ["seed_base"] = 7,
        };

        var expectedPayload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["v"] = CacheConstants.CacheVersion,
            ["schema"] = CacheConstants.CacheSchema,
            ["dice"] = diceSignature,
            ["counts"] = counts,
            ["target"] = 3000,
            ["risk_profile"] = "Balanced",
            ["num_turns"] = 10000,
            ["cap"] = 3000,
            ["seed_base"] = 7,
        };
        string expectedRaw = JsonSerializer.Serialize(expectedPayload);
        string expectedKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedRaw))).ToLowerInvariant();

        string actual = CacheKeyBuilder.Build(diceSignature, counts, settings);
        AssertEx.Equal(expectedKey, actual, "Cache key must match deterministic payload hash format.");
    }
}
