using System.Collections.Immutable;
using System.Text.Json;

namespace HenrysDiceDevil.Infrastructure.Data;

public sealed class DiceProbabilityCatalog
{
    public DiceProbabilityCatalog(ImmutableDictionary<string, ImmutableArray<double>> entries)
    {
        Entries = entries;
    }

    public ImmutableDictionary<string, ImmutableArray<double>> Entries { get; }

    public static DiceProbabilityCatalog LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Dice probability file root must be a JSON object.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<double>>(StringComparer.Ordinal);
        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Dice entry '{property.Name}' must be an array.");
            }

            var probabilityBuilder = ImmutableArray.CreateBuilder<double>();
            foreach (JsonElement number in property.Value.EnumerateArray())
            {
                if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out double value))
                {
                    throw new InvalidDataException($"Dice entry '{property.Name}' contains a non-numeric probability.");
                }

                probabilityBuilder.Add(value);
            }

            builder[property.Name] = probabilityBuilder.ToImmutable();
        }

        return new DiceProbabilityCatalog(builder.ToImmutable());
    }
}
