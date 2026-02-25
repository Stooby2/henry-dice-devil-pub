using System.Collections.Immutable;

namespace HenrysDiceDevil.Domain.Models;

public sealed record DieType
{
    public DieType(string name, IReadOnlyList<double> probabilities, double quality)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Die name is required.", nameof(name));
        }

        if (probabilities.Count != 7)
        {
            throw new ArgumentException("Die probabilities must contain 7 entries (index 0..6).", nameof(probabilities));
        }

        double sum = 0.0;
        for (int i = 1; i < probabilities.Count; i++)
        {
            if (probabilities[i] < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(probabilities), $"Probability at index {i} cannot be negative.");
            }

            sum += probabilities[i];
        }

        if (Math.Abs(probabilities[0]) > 1e-12)
        {
            throw new ArgumentException("Die probability at index 0 must be 0.0.", nameof(probabilities));
        }

        if (Math.Abs(1.0 - sum) > 1e-9)
        {
            throw new ArgumentException("Die probabilities (index 1..6) must sum to 1.0.", nameof(probabilities));
        }

        Name = name;
        Probabilities = probabilities.ToImmutableArray();
        Quality = quality;
    }

    public string Name { get; }

    public ImmutableArray<double> Probabilities { get; }

    public double Quality { get; }
}
