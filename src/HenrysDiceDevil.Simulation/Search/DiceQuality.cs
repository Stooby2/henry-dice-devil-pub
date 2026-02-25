namespace HenrysDiceDevil.Simulation.Search;

public static class DiceQuality
{
    public static double FromProbabilities(IReadOnlyList<double> probs)
    {
        if (probs.Count < 7)
        {
            throw new ArgumentException("Probabilities must contain indices 0..6.", nameof(probs));
        }

        double p1 = probs[1];
        double p5 = probs[5];
        double others = probs[2] + probs[3] + probs[4] + probs[6];
        return (100.0 * p1) + (50.0 * p5) + (20.0 * others);
    }
}
