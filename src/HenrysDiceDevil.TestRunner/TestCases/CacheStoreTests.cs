using System.Text.Json;
using HenrysDiceDevil.Infrastructure.Caching;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class CacheStoreTests : ITestCase
{
    public string Name => nameof(CacheStoreTests);

    public void Run()
    {
        string root = RepositoryPaths.ResolveRoot();
        string cacheRoot = Path.Combine(root, "cache", "test-cache-store");
        if (Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }

        using var store = new FileResultCacheStore(cacheRoot);
        store.ClearAll();

        var a = JsonSerializer.SerializeToElement(new { v = 1 });
        var b = JsonSerializer.SerializeToElement(new { v = 2 });
        var c = JsonSerializer.SerializeToElement(new { v = 3 });

        store.Save(
            [
                ("k1", a, "pilot"),
                ("k2", b, "full"),
                ("k3", c, "pilot"),
            ]);

        int deletedPilot = store.ClearKind("pilot");
        AssertEx.Equal(2, deletedPilot, "Pilot clear should delete only pilot entries.");
        var loadedAfterPilotClear = store.Load(["k1", "k2", "k3"]);
        AssertEx.True(!loadedAfterPilotClear.ContainsKey("k1"), "Pilot key should be removed.");
        AssertEx.True(loadedAfterPilotClear.ContainsKey("k2"), "Full key should remain after pilot clear.");
        AssertEx.True(!loadedAfterPilotClear.ContainsKey("k3"), "Pilot key should be removed.");

        int deletedFull = store.ClearKind("full");
        AssertEx.Equal(1, deletedFull, "Full clear should delete remaining full entries.");
        var loadedAfterFullClear = store.Load(["k2"]);
        AssertEx.True(!loadedAfterFullClear.ContainsKey("k2"), "Full key should be removed.");

        store.ClearAll();
        if (Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }
}
