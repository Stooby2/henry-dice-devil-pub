using HenrysDiceDevil.Simulation.Contracts;

namespace HenrysDiceDevil.Simulation.Optimization;

public sealed record OptimizationRunResult(
    IReadOnlyList<SimulationResult> Results,
    int StageCount,
    int FinalCandidateCount)
{
    public OptimizationTelemetry Telemetry { get; init; } = OptimizationTelemetry.Empty;
}
