namespace HenrysDiceDevil.Tests.TestSupport;

internal static class RepositoryPaths
{
    public static string ResolveRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            string agentPath = Path.Combine(current.FullName, "AGENTS.md");
            string dataPath = Path.Combine(current.FullName, "data");
            if (File.Exists(agentPath) && Directory.Exists(dataPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root from test output directory.");
    }
}
