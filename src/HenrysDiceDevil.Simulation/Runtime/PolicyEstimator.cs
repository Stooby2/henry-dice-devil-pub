using HenrysDiceDevil.Simulation.Scoring;

namespace HenrysDiceDevil.Simulation.Runtime;

public sealed class PolicyEstimator
{
    private readonly ScoringGroupEngine _scoring;

    public PolicyEstimator(ScoringGroupEngine scoring)
    {
        _scoring = scoring;
    }

    public (double BustProbability, double EvPoints) EstimateBustAndEvExact(double[] averageFaceProbabilities, int numDice)
    {
        var patterns = CountPatternCache.Get(numDice);
        double bust = 0.0;
        double ev = 0.0;

        foreach (var (counts, coeff) in patterns)
        {
            double probability = coeff;
            for (int face = 0; face < 6; face++)
            {
                int count = counts[face];
                if (count > 0)
                {
                    probability *= Math.Pow(averageFaceProbabilities[face], count);
                }
            }

            if (probability <= 0.0)
            {
                continue;
            }

            var selections = _scoring.ScoreGroupsForCounts(counts);
            if (selections.Length == 0)
            {
                bust += probability;
                continue;
            }

            int best = selections.Max(static s => s.Points);
            ev += probability * best;
        }

        return (bust, ev);
    }
}
