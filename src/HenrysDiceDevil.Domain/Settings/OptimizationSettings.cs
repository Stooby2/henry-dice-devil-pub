using System.Collections.Immutable;

namespace HenrysDiceDevil.Domain.Settings;

public sealed record OptimizationSettings(
    int TargetScore,
    int TurnCap,
    int NumTurns,
    RiskProfile RiskProfile,
    OptimizationObjective Objective,
    ImmutableArray<int> ProbTurns,
    bool EfficiencyEnabled,
    int EfficiencySeed,
    ImmutableArray<EfficiencyStage> EfficiencyPlan);
