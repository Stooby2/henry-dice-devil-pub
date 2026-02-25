using HenrysDiceDevil.Domain.Settings;

namespace HenrysDiceDevil.Simulation.Runtime;

public static class RiskPolicyCatalog
{
    public static RiskPolicy Resolve(RiskProfile profile) =>
        profile switch
        {
            RiskProfile.Conservative => new RiskPolicy(0.6, 1.4, 300, 0.25),
            RiskProfile.Balanced => new RiskPolicy(0.8, 1.1, 200, 0.35),
            RiskProfile.Aggressive => new RiskPolicy(1.0, 0.9, 120, 0.45),
            _ => new RiskPolicy(0.8, 1.1, 200, 0.35),
        };
}
