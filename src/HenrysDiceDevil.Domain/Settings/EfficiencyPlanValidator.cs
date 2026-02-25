using System.Collections.Immutable;

namespace HenrysDiceDevil.Domain.Settings;

public static class EfficiencyPlanValidator
{
    public static ImmutableArray<string> Validate(IReadOnlyList<EfficiencyStage> plan)
    {
        if (plan.Count == 0)
        {
            return [];
        }

        var errors = ImmutableArray.CreateBuilder<string>();
        int? lastMinTotal = null;
        int? lastPilotTurns = null;

        for (int i = 0; i < plan.Count; i++)
        {
            var row = plan[i];
            int rowNumber = i + 1;

            if (row.MinTotal < 0)
            {
                errors.Add($"Row {rowNumber}: min total must be >= 0.");
            }

            if (row.PilotTurns < 1)
            {
                errors.Add($"Row {rowNumber}: pilot turns must be >= 1.");
            }

            if (row.KeepPercent <= 0.0 || row.KeepPercent > 100.0)
            {
                errors.Add($"Row {rowNumber}: keep % must be in (0, 100].");
            }

            if (row.Epsilon < 0.0)
            {
                errors.Add($"Row {rowNumber}: epsilon must be >= 0.");
            }

            if (row.MinSurvivors < 1)
            {
                errors.Add($"Row {rowNumber}: min survivors must be >= 1.");
            }

            if (lastMinTotal.HasValue && row.MinTotal > lastMinTotal.Value)
            {
                errors.Add($"Row {rowNumber}: min total must be <= previous row min total.");
            }

            if (lastPilotTurns.HasValue && row.PilotTurns <= lastPilotTurns.Value)
            {
                errors.Add($"Row {rowNumber}: pilot turns must increase between rows.");
            }

            lastMinTotal = row.MinTotal;
            lastPilotTurns = row.PilotTurns;
        }

        return errors.ToImmutable();
    }
}
