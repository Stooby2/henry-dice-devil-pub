namespace HenrysDiceDevil.Domain.Settings;

public sealed record EfficiencyStage(
    int MinTotal,
    int PilotTurns,
    double KeepPercent,
    double Epsilon,
    int MinSurvivors);
