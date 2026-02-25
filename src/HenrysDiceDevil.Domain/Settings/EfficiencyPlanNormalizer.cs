using System.Collections.Immutable;
using System.Globalization;

namespace HenrysDiceDevil.Domain.Settings;

public static class EfficiencyPlanNormalizer
{
    public static (ImmutableArray<EfficiencyStage> Plan, ImmutableArray<string> Errors) Normalize(
        IReadOnlyList<IDictionary<string, object?>>? rawPlan)
    {
        if (rawPlan is null)
        {
            return ([], []);
        }

        var normalized = ImmutableArray.CreateBuilder<EfficiencyStage>();
        var errors = ImmutableArray.CreateBuilder<string>();

        for (int i = 0; i < rawPlan.Count; i++)
        {
            int row = i + 1;
            var item = rawPlan[i];
            if (item is null)
            {
                errors.Add($"Row {row}: expected an object.");
                continue;
            }

            if (!TryGetInt(item, "min_total", 0, out int minTotal) ||
                !TryGetInt(item, "pilot_turns", 1000, out int pilotTurns) ||
                !TryGetDouble(item, "keep_percent", 10.0, out double keepPercent) ||
                !TryGetDouble(item, "epsilon", 0.01, out double epsilon) ||
                !TryGetInt(item, "min_survivors", 100, out int minSurvivors))
            {
                errors.Add($"Row {row}: contains invalid numeric values.");
                continue;
            }

            normalized.Add(
                new EfficiencyStage(
                    MinTotal: Math.Max(0, minTotal),
                    PilotTurns: Math.Max(1, pilotTurns),
                    KeepPercent: Math.Clamp(keepPercent, 0.01, 100.0),
                    Epsilon: Math.Max(0.0, epsilon),
                    MinSurvivors: Math.Max(1, minSurvivors)));
        }

        return (normalized.ToImmutable(), errors.ToImmutable());
    }

    public static EfficiencyStage LegacyRow(
        int pilotTurns,
        double keepPercent,
        double epsilon,
        int minSurvivors) =>
        new(
            MinTotal: 0,
            PilotTurns: Math.Max(1, pilotTurns),
            KeepPercent: Math.Clamp(keepPercent, 0.01, 100.0),
            Epsilon: Math.Max(0.0, epsilon),
            MinSurvivors: Math.Max(1, minSurvivors));

    private static bool TryGetInt(IDictionary<string, object?> row, string key, int fallback, out int value)
    {
        if (!row.TryGetValue(key, out object? raw) || raw is null)
        {
            value = fallback;
            return true;
        }

        if (raw is int asInt)
        {
            value = asInt;
            return true;
        }

        if (raw is long asLong && asLong >= int.MinValue && asLong <= int.MaxValue)
        {
            value = (int)asLong;
            return true;
        }

        if (raw is string asText && int.TryParse(asText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            value = parsed;
            return true;
        }

        try
        {
            value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private static bool TryGetDouble(IDictionary<string, object?> row, string key, double fallback, out double value)
    {
        if (!row.TryGetValue(key, out object? raw) || raw is null)
        {
            value = fallback;
            return true;
        }

        if (raw is double asDouble)
        {
            value = asDouble;
            return true;
        }

        if (raw is float asFloat)
        {
            value = asFloat;
            return true;
        }

        if (raw is int asInt)
        {
            value = asInt;
            return true;
        }

        if (raw is string asText && double.TryParse(asText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            value = parsed;
            return true;
        }

        try
        {
            value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }
}
