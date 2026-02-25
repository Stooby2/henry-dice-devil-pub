namespace HenrysDiceDevil.Simulation.Contracts;

public interface ISimulationEngine
{
    SimulationResult Run(SimulationRequest request);
}
