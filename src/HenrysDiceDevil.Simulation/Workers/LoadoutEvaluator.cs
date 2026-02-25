using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Simulation.Contracts;

namespace HenrysDiceDevil.Simulation.Workers;

public sealed class LoadoutEvaluator
{
    private readonly ISimulationEngine _engine;

    public LoadoutEvaluator(ISimulationEngine engine)
    {
        _engine = engine;
    }

    public SimulationResult EvaluateSingle(
        IReadOnlyList<int> counts,
        IReadOnlyList<DieType> diceCatalog,
        OptimizationSettings settings,
        int? seedBase = null)
    {
        var request = new SimulationRequest(
            DiceCatalog: diceCatalog,
            Counts: counts,
            Settings: settings,
            SeedBase: seedBase);
        return _engine.Run(request);
    }

    public IReadOnlyList<SimulationResult> EvaluateBatch(
        IReadOnlyList<IReadOnlyList<int>> countsBatch,
        IReadOnlyList<DieType> diceCatalog,
        OptimizationSettings settings,
        int? seedBase = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SimulationResult>(countsBatch.Count);
        foreach (var counts in countsBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(EvaluateSingle(counts, diceCatalog, settings, seedBase));
        }

        return results;
    }
}
