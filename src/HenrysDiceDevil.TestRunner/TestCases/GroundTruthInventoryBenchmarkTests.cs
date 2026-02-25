using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Simulation.Runtime;
using HenrysDiceDevil.Simulation.Search;
using HenrysDiceDevil.Simulation.Workers;
using HenrysDiceDevil.Tests.TestSupport;

namespace HenrysDiceDevil.Tests.TestCases;

internal sealed class GroundTruthInventoryBenchmarkTests : ITestCase
{
    public string Name => nameof(GroundTruthInventoryBenchmarkTests);

    private const int LoadoutSize = 6;
    private const int DefaultTurnCap = 3000;
    private const double MaxAllowedOutOfBoundsPercent = 2.0;
    private const int MinTop10InObservedTop20Overlap = 9;
    private const string RunEnvVar = "KCD2_RUN_GROUND_TRUTH_BENCHMARK";

    public void Run()
    {
        if (!ShouldRun())
        {
            Console.WriteLine($"Skipped {Name}: set {RunEnvVar}=1 to enable this benchmark.");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        string root = RepositoryPaths.ResolveRoot();
        string groundTruthPath = Path.Combine(root, "tests", "data", "ground_truth_inventory_1000_turns.json");
        string probabilitiesPath = Path.Combine(root, "data", "kcd2_dice_probabilities.json");
        string reportPath = Path.Combine(root, "cache", "validation", "ground_truth_inventory_1000_turns_report.json");

        AssertEx.True(File.Exists(groundTruthPath), $"Ground-truth file is missing: {groundTruthPath}");
        AssertEx.True(File.Exists(probabilitiesPath), $"Dice probability file is missing: {probabilitiesPath}");

        GroundTruthFixture fixture = LoadGroundTruthFixture(groundTruthPath);
        IReadOnlyList<DieType> diceCatalog = LoadOrderedDiceCatalog(probabilitiesPath);
        var indexByName = diceCatalog
            .Select((die, index) => (die.Name, index))
            .ToDictionary(static x => x.Name, static x => x.index, StringComparer.Ordinal);
        int[] available = BuildOwnedInventory(fixture.OwnedDice, indexByName, diceCatalog.Count);

        AssertEx.Equal(fixture.Config.OwnedDiceCount, available.Sum(), "Owned inventory total count must match config.");

        ImmutableArray<ImmutableArray<int>> enumerated = LoadoutSearch.EnumerateLoadouts(available, total: LoadoutSize);
        AssertEx.Equal(fixture.LoadoutCount, enumerated.Length, "Enumerated loadout count must match fixture loadout_count.");
        AssertEx.Equal(fixture.Results.Count, enumerated.Length, "Fixture results count must match enumerated loadouts.");

        var enumeratedKeys = new HashSet<string>(enumerated.Select(static counts => ToKey(counts)), StringComparer.Ordinal);
        var evaluator = new LoadoutEvaluator(new TurnSimulationEngine());
        var settings = new OptimizationSettings(
            TargetScore: fixture.Config.Target,
            TurnCap: Math.Max(DefaultTurnCap, fixture.Config.Target),
            NumTurns: fixture.Config.Turns,
            RiskProfile: fixture.Config.RiskProfile,
            Objective: OptimizationObjective.MaxScore,
            ProbTurns: ImmutableArray.Create(10, 15, 20),
            EfficiencyEnabled: false,
            EfficiencySeed: fixture.Validation.TestSeedBase,
            EfficiencyPlan: []);

        var observedRows = new List<ObservedRow>(fixture.Results.Count);
        foreach (GroundTruthResult row in fixture.Results)
        {
            AssertEx.Equal(diceCatalog.Count, row.Counts.Count, $"Loadout {row.Id} counts length must match dice catalog length.");
            AssertEx.Equal(LoadoutSize, row.Counts.Sum(), $"Loadout {row.Id} must have exactly {LoadoutSize} dice.");
            for (int i = 0; i < row.Counts.Count; i++)
            {
                AssertEx.True(row.Counts[i] >= 0, $"Loadout {row.Id} has a negative count at index {i}.");
                AssertEx.True(row.Counts[i] <= available[i], $"Loadout {row.Id} exceeds owned inventory at index {i}.");
            }

            string key = ToKey(row.Counts);
            AssertEx.True(enumeratedKeys.Contains(key), $"Loadout {row.Id} is not part of the enumerated inventory loadouts.");

            var points = new double[fixture.Validation.TestRepeats];
            var turns = new double[fixture.Validation.TestRepeats];
            for (int repeatIndex = 0; repeatIndex < fixture.Validation.TestRepeats; repeatIndex++)
            {
                int seedBase = fixture.Validation.TestSeedBase + repeatIndex;
                var result = evaluator.EvaluateSingle(row.Counts, diceCatalog, settings, seedBase: seedBase);
                points[repeatIndex] = result.MeanPoints;
                turns[repeatIndex] = result.Metrics.EvTurns;
            }

            double meanPoints = points.Average();
            double meanTurns = turns.Average();
            bool pointsInBounds = meanPoints >= row.BoundsEvPoints.Lower && meanPoints <= row.BoundsEvPoints.Upper;
            bool turnsInBounds = meanTurns >= row.BoundsEvTurns.Lower && meanTurns <= row.BoundsEvTurns.Upper;

            observedRows.Add(new ObservedRow(
                Id: row.Id,
                CountsKey: key,
                ExpectedMeanEvPoints: row.MeanEvPoints,
                ExpectedMeanEvTurns: row.MeanEvTurns,
                ObservedMeanEvPoints: meanPoints,
                ObservedMeanEvTurns: meanTurns,
                SignedDeltaEvPoints: meanPoints - row.MeanEvPoints,
                SignedDeltaEvTurns: meanTurns - row.MeanEvTurns,
                BoundsEvPoints: row.BoundsEvPoints,
                BoundsEvTurns: row.BoundsEvTurns,
                InBoundsEvPoints: pointsInBounds,
                InBoundsEvTurns: turnsInBounds));
        }

        int total = observedRows.Count;
        int pointsOut = observedRows.Count(static x => !x.InBoundsEvPoints);
        int turnsOut = observedRows.Count(static x => !x.InBoundsEvTurns);
        int combinedOut = observedRows.Count(static x => !x.InBoundsEvPoints || !x.InBoundsEvTurns);
        double pointsOutPct = Percent(pointsOut, total);
        double turnsOutPct = Percent(turnsOut, total);
        double combinedOutPct = Percent(combinedOut, total);

        var top20Points = observedRows
            .OrderByDescending(static x => Math.Abs(x.SignedDeltaEvPoints))
            .ThenBy(static x => x.Id, StringComparer.Ordinal)
            .Take(20)
            .Select(static x => new
            {
                id = x.Id,
                signed_delta = x.SignedDeltaEvPoints,
                absolute_delta = Math.Abs(x.SignedDeltaEvPoints),
                observed_mean = x.ObservedMeanEvPoints,
                expected_mean = x.ExpectedMeanEvPoints,
                in_bounds = x.InBoundsEvPoints,
            })
            .ToArray();

        var top20Turns = observedRows
            .OrderByDescending(static x => Math.Abs(x.SignedDeltaEvTurns))
            .ThenBy(static x => x.Id, StringComparer.Ordinal)
            .Take(20)
            .Select(static x => new
            {
                id = x.Id,
                signed_delta = x.SignedDeltaEvTurns,
                absolute_delta = Math.Abs(x.SignedDeltaEvTurns),
                observed_mean = x.ObservedMeanEvTurns,
                expected_mean = x.ExpectedMeanEvTurns,
                in_bounds = x.InBoundsEvTurns,
            })
            .ToArray();

        var expectedTop10 = observedRows
            .OrderByDescending(static x => x.ExpectedMeanEvPoints)
            .ThenBy(static x => x.Id, StringComparer.Ordinal)
            .Take(10)
            .ToArray();
        var expectedTop20 = observedRows
            .OrderByDescending(static x => x.ExpectedMeanEvPoints)
            .ThenBy(static x => x.Id, StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        var observedTop10 = observedRows
            .OrderByDescending(static x => x.ObservedMeanEvPoints)
            .ThenBy(static x => x.Id, StringComparer.Ordinal)
            .Take(10)
            .ToArray();
        var observedTop20 = observedRows
            .OrderByDescending(static x => x.ObservedMeanEvPoints)
            .ThenBy(static x => x.Id, StringComparer.Ordinal)
            .Take(20)
            .ToArray();

        var expectedTop10Set = new HashSet<string>(expectedTop10.Select(static x => x.Id), StringComparer.Ordinal);
        var observedTop10Set = new HashSet<string>(observedTop10.Select(static x => x.Id), StringComparer.Ordinal);
        var observedTop20Set = new HashSet<string>(observedTop20.Select(static x => x.Id), StringComparer.Ordinal);
        var overlap = expectedTop10Set.Intersect(observedTop20Set).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var missingFromObserved = expectedTop10Set.Except(observedTop20Set).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var excessInObserved = observedTop20Set.Except(expectedTop10Set).OrderBy(static x => x, StringComparer.Ordinal).ToArray();

        bool primaryPass = combinedOutPct <= MaxAllowedOutOfBoundsPercent;
        bool rankingPass = overlap.Length >= MinTop10InObservedTop20Overlap;

        stopwatch.Stop();

        var report = new
        {
            fixture = new
            {
                path = groundTruthPath,
                loadout_count = fixture.LoadoutCount,
                inventory_seed = fixture.InventorySeed,
                meta = fixture.Meta,
            },
            simulator_configuration = new
            {
                turns = fixture.Config.Turns,
                target = fixture.Config.Target,
                turn_cap = Math.Max(DefaultTurnCap, fixture.Config.Target),
                risk_profile = fixture.Config.RiskProfile.ToString(),
                prob_turns = new[] { 10, 15, 20 },
                test_repeats = fixture.Validation.TestRepeats,
                test_seed_base = fixture.Validation.TestSeedBase,
            },
            summary = new
            {
                total_loadouts_tested = total,
                out_of_bounds_ev_points = new { count = pointsOut, percent = pointsOutPct },
                out_of_bounds_ev_turns = new { count = turnsOut, percent = turnsOutPct },
                combined_fail = new { count = combinedOut, percent = combinedOutPct },
                ranking = new
                {
                    expected_top10 = expectedTop10.Select(static x => x.Id).ToArray(),
                    observed_top10 = observedTop10.Select(static x => x.Id).ToArray(),
                    expected_top20 = expectedTop20.Select(static x => new { id = x.Id, mean_ev_points = x.ExpectedMeanEvPoints }).ToArray(),
                    observed_top20 = observedTop20.Select(static x => new { id = x.Id, mean_ev_points = x.ObservedMeanEvPoints }).ToArray(),
                    overlap = overlap,
                    overlap_count = overlap.Length,
                    overlap_definition = "ground_truth_top10 intersect observed_top20",
                    missing_from_observed_top20 = missingFromObserved,
                    excess_in_observed_top20 = excessInObserved,
                },
                pass_fail = new
                {
                    primary_max_out_of_bounds_percent = MaxAllowedOutOfBoundsPercent,
                    ranking_min_top10_in_observed_top20_overlap = MinTop10InObservedTop20Overlap,
                    primary_pass = primaryPass,
                    ranking_pass = rankingPass,
                    overall_pass = primaryPass && rankingPass,
                },
            },
            largest_deviations = new
            {
                ev_points = top20Points,
                ev_turns = top20Turns,
            },
            runtime = new
            {
                elapsed_seconds = stopwatch.Elapsed.TotalSeconds,
                timestamp_utc = DateTimeOffset.UtcNow,
            },
        };

        string? reportDirectory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(reportDirectory))
        {
            Directory.CreateDirectory(reportDirectory);
        }

        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine(
            $"Ground-truth benchmark summary: loadouts={total}, out(points)={pointsOut} ({pointsOutPct:F2}%), " +
            $"out(turns)={turnsOut} ({turnsOutPct:F2}%), combined={combinedOut} ({combinedOutPct:F2}%), " +
            $"top10-in-top20 overlap={overlap.Length}/10, report={reportPath}");

        AssertEx.True(
            primaryPass,
            $"Primary criterion failed: out-of-bounds loadouts {combinedOutPct:F2}% exceeds {MaxAllowedOutOfBoundsPercent:F2}%. Report: {reportPath}");
        AssertEx.True(
            rankingPass,
            $"Ranking criterion failed: top-10-in-top-20 overlap {overlap.Length}/10 is below {MinTop10InObservedTop20Overlap}/10. Report: {reportPath}");
    }

    private static GroundTruthFixture LoadGroundTruthFixture(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;
        AssertEx.True(root.ValueKind == JsonValueKind.Object, "Ground-truth fixture root must be an object.");

        JsonElement configNode = Required(root, "config");
        JsonElement validationNode = Required(root, "validation");
        JsonElement ownedDiceNode = Required(root, "owned_dice");
        JsonElement resultsNode = Required(root, "results");
        JsonElement metaNode = Required(root, "meta");

        var config = new FixtureConfig(
            OwnedDiceCount: Required(configNode, "owned_dice_count").GetInt32(),
            Turns: Required(configNode, "turns").GetInt32(),
            Target: Required(configNode, "target").GetInt32(),
            RiskProfile: ParseRiskProfile(Required(configNode, "risk_profile").GetString()));

        var validation = new FixtureValidation(
            TestRepeats: Required(validationNode, "test_repeats").GetInt32(),
            TestSeedBase: Required(validationNode, "test_seed_base").GetInt32());

        var ownedDice = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty property in ownedDiceNode.EnumerateObject())
        {
            ownedDice[property.Name] = property.Value.GetInt32();
        }

        var results = new List<GroundTruthResult>();
        foreach (JsonElement resultNode in resultsNode.EnumerateArray())
        {
            string id = Required(resultNode, "id").GetString()
                ?? throw new InvalidDataException("Result id cannot be null.");
            var counts = Required(resultNode, "counts").EnumerateArray().Select(static x => x.GetInt32()).ToArray();
            var boundsEvPoints = ParseBounds(Required(resultNode, "bounds_ev_points"));
            var boundsEvTurns = ParseBounds(Required(resultNode, "bounds_ev_turns"));
            double meanEvPoints = Required(resultNode, "mean_ev_points").GetDouble();
            double meanEvTurns = Required(resultNode, "mean_ev_turns").GetDouble();

            results.Add(new GroundTruthResult(
                Id: id,
                Counts: counts,
                MeanEvPoints: meanEvPoints,
                MeanEvTurns: meanEvTurns,
                BoundsEvPoints: boundsEvPoints,
                BoundsEvTurns: boundsEvTurns));
        }

        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in metaNode.EnumerateObject())
        {
            meta[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt64(out long iv) => iv,
                JsonValueKind.Number => property.Value.GetDouble(),
                _ => property.Value.ToString(),
            };
        }

        return new GroundTruthFixture(
            Config: config,
            Validation: validation,
            OwnedDice: ownedDice,
            Results: results,
            LoadoutCount: Required(root, "loadout_count").GetInt32(),
            InventorySeed: Required(root, "inventory_seed").GetInt32(),
            Meta: meta);
    }

    private static IReadOnlyList<DieType> LoadOrderedDiceCatalog(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;
        AssertEx.True(root.ValueKind == JsonValueKind.Object, "Dice probabilities root must be an object.");

        var dice = new List<DieType>();
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Dice entry '{property.Name}' must be an array.");
            }

            double[] probabilities = property.Value.EnumerateArray().Select(static x => x.GetDouble()).ToArray();
            if (probabilities.Length != 7)
            {
                throw new InvalidDataException($"Dice entry '{property.Name}' must contain 7 probabilities.");
            }

            double quality = DiceQuality.FromProbabilities(probabilities);
            dice.Add(new DieType(property.Name, probabilities, quality));
        }

        return dice;
    }

    private static int[] BuildOwnedInventory(
        IReadOnlyDictionary<string, int> ownedDice,
        IReadOnlyDictionary<string, int> indexByName,
        int catalogCount)
    {
        var available = new int[catalogCount];
        foreach ((string name, int count) in ownedDice)
        {
            AssertEx.True(indexByName.TryGetValue(name, out int index), $"Owned die '{name}' is not found in probabilities catalog.");
            AssertEx.True(count >= 0, $"Owned die '{name}' count cannot be negative.");
            available[index] = count;
        }

        return available;
    }

    private static RiskProfile ParseRiskProfile(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidDataException("risk_profile is required.");
        }

        if (!Enum.TryParse<RiskProfile>(raw, ignoreCase: true, out RiskProfile profile))
        {
            throw new InvalidDataException($"Unsupported risk profile '{raw}'.");
        }

        return profile;
    }

    private static Bounds ParseBounds(JsonElement node)
    {
        return new Bounds(
            Lower: Required(node, "lower").GetDouble(),
            Upper: Required(node, "upper").GetDouble());
    }

    private static string ToKey(IReadOnlyList<int> counts)
    {
        return string.Join(",", counts);
    }

    private static double Percent(int count, int total)
    {
        return total == 0 ? 0.0 : (100.0 * count / total);
    }

    private static bool ShouldRun()
    {
        string? raw = Environment.GetEnvironmentVariable(RunEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw == "1"
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Required(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException($"Required property '{propertyName}' is missing.");
        }

        return value;
    }

    private sealed record FixtureConfig(int OwnedDiceCount, int Turns, int Target, RiskProfile RiskProfile);

    private sealed record FixtureValidation(int TestRepeats, int TestSeedBase);

    private sealed record GroundTruthResult(
        string Id,
        IReadOnlyList<int> Counts,
        double MeanEvPoints,
        double MeanEvTurns,
        Bounds BoundsEvPoints,
        Bounds BoundsEvTurns);

    private sealed record GroundTruthFixture(
        FixtureConfig Config,
        FixtureValidation Validation,
        IReadOnlyDictionary<string, int> OwnedDice,
        IReadOnlyList<GroundTruthResult> Results,
        int LoadoutCount,
        int InventorySeed,
        IReadOnlyDictionary<string, object?> Meta);

    private sealed record Bounds(double Lower, double Upper);

    private sealed record ObservedRow(
        string Id,
        string CountsKey,
        double ExpectedMeanEvPoints,
        double ExpectedMeanEvTurns,
        double ObservedMeanEvPoints,
        double ObservedMeanEvTurns,
        double SignedDeltaEvPoints,
        double SignedDeltaEvTurns,
        Bounds BoundsEvPoints,
        Bounds BoundsEvTurns,
        bool InBoundsEvPoints,
        bool InBoundsEvTurns);
}
