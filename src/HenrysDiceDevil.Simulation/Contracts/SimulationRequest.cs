using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;

namespace HenrysDiceDevil.Simulation.Contracts;

public sealed record SimulationRequest(
    IReadOnlyList<DieType> DiceCatalog,
    IReadOnlyList<int> Counts,
    OptimizationSettings Settings,
    int? SeedBase);
