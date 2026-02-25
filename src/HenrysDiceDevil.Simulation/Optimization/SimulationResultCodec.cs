using System.Collections.Immutable;
using System.Text.Json;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Simulation.Contracts;

namespace HenrysDiceDevil.Simulation.Optimization;

public static class SimulationResultCodec
{
    public static JsonElement Serialize(SimulationResult result)
    {
        var payload = new
        {
            counts = result.Counts.ToArray(),
            metrics = new
            {
                ev_turns = result.Metrics.EvTurns,
                p_within = result.Metrics.PWithin,
                ev_points = result.Metrics.EvPoints,
                p50_turns = result.Metrics.P50Turns,
                p90_turns = result.Metrics.P90Turns,
                ev_points_se = result.Metrics.EvPointsSe,
            },
            mean = result.MeanPoints,
            std = result.StandardDeviation,
            tag_counts = result.TagCounts,
            total_groups = result.TotalGroups,
            scoring_turns = result.ScoringTurns,
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    public static SimulationResult Deserialize(JsonElement payload)
    {
        var counts = payload.GetProperty("counts").EnumerateArray().Select(static x => x.GetInt32()).ToArray();
        var metricsNode = payload.GetProperty("metrics");
        var pWithin = ImmutableDictionary.CreateBuilder<int, double>();
        foreach (var kv in metricsNode.GetProperty("p_within").EnumerateObject())
        {
            pWithin[int.Parse(kv.Name)] = kv.Value.GetDouble();
        }

        var metrics = new TurnMetrics(
            EvTurns: metricsNode.GetProperty("ev_turns").GetDouble(),
            PWithin: pWithin.ToImmutable(),
            EvPoints: metricsNode.GetProperty("ev_points").GetDouble(),
            P50Turns: metricsNode.GetProperty("p50_turns").GetDouble(),
            P90Turns: metricsNode.GetProperty("p90_turns").GetDouble(),
            EvPointsSe: metricsNode.GetProperty("ev_points_se").GetDouble());

        var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in payload.GetProperty("tag_counts").EnumerateObject())
        {
            tagCounts[kv.Name] = kv.Value.GetInt32();
        }

        return new SimulationResult(
            Counts: counts,
            Metrics: metrics,
            MeanPoints: payload.GetProperty("mean").GetDouble(),
            StandardDeviation: payload.GetProperty("std").GetDouble(),
            TagCounts: tagCounts,
            TotalGroups: payload.GetProperty("total_groups").GetInt32(),
            ScoringTurns: payload.GetProperty("scoring_turns").GetInt32());
    }
}
