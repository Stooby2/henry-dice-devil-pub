using System.Security.Cryptography;
using System.Text;

namespace HenrysDiceDevil.Simulation.Runtime;

internal static class SeedFactory
{
    public static int BuildSeed(int seedBase, IReadOnlyList<int> counts)
    {
        string payload = $"{seedBase}:{string.Join(",", counts)}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        ulong seed64 = Convert.ToUInt64(Convert.ToHexString(hash.AsSpan(0, 8)), 16);
        return unchecked((int)(seed64 ^ (seed64 >> 32)));
    }
}
