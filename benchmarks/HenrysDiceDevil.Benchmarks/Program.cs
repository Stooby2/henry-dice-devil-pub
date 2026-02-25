using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HenrysDiceDevil.Domain.Diagnostics;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Infrastructure.Caching;
using HenrysDiceDevil.Infrastructure.Data;
using HenrysDiceDevil.Simulation.Optimization;
using HenrysDiceDevil.Simulation.Runtime;
using HenrysDiceDevil.Simulation.Search;
using HenrysDiceDevil.Simulation.Workers;

namespace HenrysDiceDevil.Benchmarks;

internal static class Program
{
    private static readonly ImmutableArray<EfficiencyStage> DefaultEfficiencyPlan =
    [
        new EfficiencyStage(MinTotal: 1000, PilotTurns: 80, KeepPercent: 30.0, Epsilon: 0.1, MinSurvivors: 100),
        new EfficiencyStage(MinTotal: 0, PilotTurns: 0, KeepPercent: 100.0, Epsilon: 0.0, MinSurvivors: 1),
    ];

    private static int Main(string[] args)
    {
        try
        {
            string root = ResolveRepositoryRoot();
            if (args.Length > 0 && string.Equals(args[0], "compare", StringComparison.OrdinalIgnoreCase))
            {
                var compareOptions = ParseCompareOptions(args.Skip(1).ToArray(), root);
                return RunCompare(compareOptions);
            }

            var options = ParseRunOptions(args, root);
            BenchmarkReport report = RunBenchmarks(options, root);

            if (!string.IsNullOrWhiteSpace(options.JsonPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(options.JsonPath)!);
                File.WriteAllText(
                    options.JsonPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"JSON report written: {options.JsonPath}");
            }

            if (!string.IsNullOrWhiteSpace(options.MarkdownPath))
            {
                WriteMarkdownReport(options.MarkdownPath, report);
                Console.WriteLine($"Markdown report written: {options.MarkdownPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static BenchmarkReport RunBenchmarks(RunOptions options, string repoRoot)
    {
        var diceCatalog = LoadDiceCatalog(repoRoot);
        var allScenarios = BuildScenarios(diceCatalog, repoRoot);
        var selected = allScenarios
            .Where(s => options.Scenarios.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("No scenarios matched --scenarios.");
        }

        var runs = new List<ScenarioRunSummary>(selected.Length * 2);
        var wallClock = Stopwatch.StartNew();
        var workerPlan = options.WorkerSweep.Count > 0 ? options.WorkerSweep : [options.Workers];

        foreach (var scenario in selected)
        {
            foreach (int workers in workerPlan)
            {
                for (int i = 0; i < options.Iterations; i++)
                {
                    if (options.CacheModes.HasFlag(CacheModes.Cold))
                    {
                        Console.WriteLine($"Running {scenario.Name} (cold cache) iteration {i + 1}/{options.Iterations}, workers={workers}...");
                        runs.Add(RunScenario(scenario, diceCatalog, repoRoot, warmCache: false, workers, iteration: i + 1, options));
                    }

                    if (options.CacheModes.HasFlag(CacheModes.Warm))
                    {
                        Console.WriteLine($"Running {scenario.Name} (warm cache) iteration {i + 1}/{options.Iterations}, workers={workers}...");
                        runs.Add(RunScenario(scenario, diceCatalog, repoRoot, warmCache: true, workers, iteration: i + 1, options));
                    }
                }
            }
        }

        wallClock.Stop();
        var scalingSummaries = BuildScalingSummaries(runs);
        return new BenchmarkReport(
            Version: 1,
            TimestampUtc: DateTimeOffset.UtcNow,
            Runtime: new RuntimeMetadata(
                DotnetVersion: Environment.Version.ToString(),
                OsDescription: RuntimeInformation.OSDescription,
                ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorCount: Environment.ProcessorCount),
            Options: options,
            Runs: runs,
            ScalingSummaries: scalingSummaries,
            WallClockSeconds: wallClock.Elapsed.TotalSeconds);
    }

    private static ScenarioRunSummary RunScenario(
        BenchmarkScenario scenario,
        IReadOnlyList<DieType> diceCatalog,
        string repoRoot,
        bool warmCache,
        int workers,
        int iteration,
        RunOptions options)
    {
        string cacheRoot = Path.Combine(repoRoot, "cache", "benchmarks", scenario.Name);
        if (!warmCache && Directory.Exists(cacheRoot))
        {
            Directory.Delete(cacheRoot, recursive: true);
        }

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gc0Before = GC.CollectionCount(0);
        int gc1Before = GC.CollectionCount(1);
        int gc2Before = GC.CollectionCount(2);

        bool enableAsyncWrites = options.CacheAsyncOverride ?? options.AppLike;
        var perfSink = new InMemoryPerfSink(enabled: true);
        using var cacheStore = new FileResultCacheStore(
            cacheRoot,
            perfSink,
            enableAsyncWrites: enableAsyncWrites);
        var workflow = new OptimizationWorkflow(
            new LoadoutEvaluator(new TurnSimulationEngine(perfSink)),
            cacheStore,
            perfSink);
        var progressProbe = options.AppLike ? new ProgressProbe() : null;

        var runStopwatch = Stopwatch.StartNew();
        var result = workflow.Run(
            scenario.Loadouts,
            diceCatalog,
            scenario.Settings,
            workerCount: workers,
            progress: progressProbe,
            progressIntervalMs: options.ProgressIntervalMs);
        runStopwatch.Stop();
        double cacheDrainMs = 0.0;
        if (cacheStore.AsyncWritesEnabled)
        {
            var drainWatch = Stopwatch.StartNew();
            cacheStore.Flush(TimeSpan.FromSeconds(5));
            drainWatch.Stop();
            cacheDrainMs = drainWatch.Elapsed.TotalMilliseconds;
        }

        int pendingAfterFlush = cacheStore.PendingCount;
        int pendingHighWater = cacheStore.PendingHighWaterMark;

        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var telemetry = result.Telemetry;
        var perfMetrics = perfSink.Snapshot();
        double engineMeanMs = perfMetrics.TryGetValue("engine.run.total_ms", out MetricSnapshot? engineTotal)
            ? engineTotal.Mean
            : 0.0;
        double cacheReadMs = perfMetrics.TryGetValue("cache.read_state.ms", out MetricSnapshot? readState)
            ? readState.Sum
            : 0.0;
        double cacheWriteMs = perfMetrics.TryGetValue("cache.write_state.ms", out MetricSnapshot? writeState)
            ? writeState.Sum
            : 0.0;

        return new ScenarioRunSummary(
            ScenarioName: scenario.Name,
            CacheState: warmCache ? "warm" : "cold",
            Iteration: iteration,
            Workers: workers,
            LoadoutCount: scenario.Loadouts.Count,
            StageCount: result.StageCount,
            FinalCandidateCount: result.FinalCandidateCount,
            TotalElapsedMs: runStopwatch.Elapsed.TotalMilliseconds,
            TelemetryElapsedMs: telemetry.TotalElapsedMs,
            LoadoutsPerSecond: telemetry.TotalLoadoutsPerSecond,
            CacheHitRate: telemetry.TotalCacheHitRate,
            WorkerUtilization: telemetry.AverageWorkerUtilization,
            QueuePressure: telemetry.AverageQueuePressure,
            EngineMeanMs: engineMeanMs,
            CacheReadMs: cacheReadMs,
            CacheWriteMs: cacheWriteMs,
            AllocatedBytes: allocatedAfter - allocatedBefore,
            GcCollectionsGen0: GC.CollectionCount(0) - gc0Before,
            GcCollectionsGen1: GC.CollectionCount(1) - gc1Before,
            GcCollectionsGen2: GC.CollectionCount(2) - gc2Before,
            AppLikeMode: options.AppLike,
            CacheAsyncWrites: enableAsyncWrites,
            ProgressReports: progressProbe?.Count ?? 0,
            ProgressReportTotalMs: progressProbe?.TotalMs ?? 0.0,
            ProgressReportMeanMs: progressProbe?.MeanMs ?? 0.0,
            CachePendingHighWaterMark: pendingHighWater,
            CachePendingAfterFlush: pendingAfterFlush,
            CacheDrainMs: cacheDrainMs,
            StageRows: telemetry.Stages
                .Select(static stage => new StageSummary(
                    stage.StageIndex,
                    stage.Kind,
                    stage.CandidateCount,
                    stage.EvaluatedCount,
                    stage.CacheHitRate,
                    stage.ElapsedMs,
                    stage.LoadoutsPerSecond,
                    stage.WorkerUtilization,
                    stage.QueuePressure))
                .ToArray(),
            PerfMetrics: perfMetrics);
    }

    private static int RunCompare(CompareOptions options)
    {
        BenchmarkReport baseline = ReadReport(options.BaselinePath);
        BenchmarkReport current = ReadReport(options.CurrentPath);
        ThresholdConfig thresholds = ReadThresholds(options.ThresholdsPath);

        var failures = new List<string>();
        foreach (var key in baseline.Runs.Select(static x => x.Key).Distinct(StringComparer.Ordinal))
        {
            var baselineRuns = baseline.Runs.Where(x => x.Key == key).ToArray();
            var currentRuns = current.Runs.Where(x => x.Key == key).ToArray();
            if (baselineRuns.Length == 0 || currentRuns.Length == 0)
            {
                failures.Add($"Missing scenario/cache pair in comparison: {key}");
                continue;
            }

            double baselineElapsed = baselineRuns.Average(static x => x.TotalElapsedMs);
            double currentElapsed = currentRuns.Average(static x => x.TotalElapsedMs);
            double baselineThroughput = baselineRuns.Average(static x => x.LoadoutsPerSecond);
            double currentThroughput = currentRuns.Average(static x => x.LoadoutsPerSecond);
            double baselineEngine = baselineRuns.Average(static x => x.EngineMeanMs);
            double currentEngine = currentRuns.Average(static x => x.EngineMeanMs);
            double baselineCacheRead = baselineRuns.Average(static x => x.CacheReadMs);
            double currentCacheRead = currentRuns.Average(static x => x.CacheReadMs);

            double elapsedRegressionPct = PercentDeltaHigherWorse(baselineElapsed, currentElapsed);
            double throughputRegressionPct = PercentDeltaLowerWorse(baselineThroughput, currentThroughput);
            double engineRegressionPct = PercentDeltaHigherWorse(baselineEngine, currentEngine);
            double cacheReadRegressionPct = PercentDeltaHigherWorse(baselineCacheRead, currentCacheRead);

            if (elapsedRegressionPct > thresholds.MaxRuntimeRegressionPercent)
            {
                failures.Add($"{key}: total elapsed regression {elapsedRegressionPct:F2}% > {thresholds.MaxRuntimeRegressionPercent:F2}%");
            }

            if (throughputRegressionPct > thresholds.MaxThroughputRegressionPercent)
            {
                failures.Add($"{key}: loadouts/sec regression {throughputRegressionPct:F2}% > {thresholds.MaxThroughputRegressionPercent:F2}%");
            }

            if (engineRegressionPct > thresholds.MaxEngineMeanRegressionPercent)
            {
                failures.Add($"{key}: engine mean ms regression {engineRegressionPct:F2}% > {thresholds.MaxEngineMeanRegressionPercent:F2}%");
            }

            if (cacheReadRegressionPct > thresholds.MaxCacheReadRegressionPercent)
            {
                failures.Add($"{key}: cache read ms regression {cacheReadRegressionPct:F2}% > {thresholds.MaxCacheReadRegressionPercent:F2}%");
            }

            Console.WriteLine(
                $"{key}: elapsed {baselineElapsed:F1} -> {currentElapsed:F1} ms ({elapsedRegressionPct:+0.00;-0.00;0.00}%), " +
                $"throughput {baselineThroughput:F1} -> {currentThroughput:F1} ({-throughputRegressionPct:+0.00;-0.00;0.00}% better), " +
                $"engine {baselineEngine:F3} -> {currentEngine:F3} ms, cache-read {baselineCacheRead:F3} -> {currentCacheRead:F3} ms");
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("Performance comparison passed.");
            return 0;
        }

        Console.WriteLine("Performance comparison failed:");
        foreach (string failure in failures)
        {
            Console.WriteLine($"- {failure}");
        }

        return 1;
    }

    private static BenchmarkReport ReadReport(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Benchmark report not found: {path}");
        }

        string json = File.ReadAllText(path);
        var report = JsonSerializer.Deserialize<BenchmarkReport>(json);
        return report ?? throw new InvalidDataException($"Could not parse benchmark report: {path}");
    }

    private static ThresholdConfig ReadThresholds(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Threshold config not found: {path}");
        }

        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ThresholdConfig>(json);
        return config ?? throw new InvalidDataException($"Could not parse threshold config: {path}");
    }

    private static void WriteMarkdownReport(string outputPath, BenchmarkReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var sb = new StringBuilder();
        sb.AppendLine("# Performance Benchmark Report");
        sb.AppendLine();
        sb.AppendLine($"Generated (UTC): {report.TimestampUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"Total benchmark wall-clock: {report.WallClockSeconds:F1}s");
        sb.AppendLine();
        string workersText = report.Options.WorkerSweep.Count > 0
            ? string.Join(",", report.Options.WorkerSweep)
            : report.Options.Workers.ToString();
        sb.AppendLine($"Workers: {workersText}, Iterations: {report.Options.Iterations}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Cache | Worker | Iteration | Loadouts | Stages | Final | Total ms | Loadouts/sec | Engine mean ms | Cache read ms | Cache write ms | Alloc MB | Gen0 | Gen1 | Gen2 |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var run in report.Runs.OrderBy(static x => x.ScenarioName, StringComparer.Ordinal).ThenBy(static x => x.CacheState, StringComparer.Ordinal).ThenBy(static x => x.Workers).ThenBy(static x => x.Iteration))
        {
            sb.AppendLine(
                $"| {run.ScenarioName} | {run.CacheState} | {run.Workers} | {run.Iteration} | {run.LoadoutCount} | {run.StageCount} | {run.FinalCandidateCount} | {run.TotalElapsedMs:F1} | {run.LoadoutsPerSecond:F1} | {run.EngineMeanMs:F3} | {run.CacheReadMs:F3} | {run.CacheWriteMs:F3} | {(run.AllocatedBytes / (1024.0 * 1024.0)):F2} | {run.GcCollectionsGen0} | {run.GcCollectionsGen1} | {run.GcCollectionsGen2} |");
        }

        if (report.Runs.Any(static x => x.AppLikeMode))
        {
            sb.AppendLine();
            sb.AppendLine("## App-Like Diagnostics");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Cache | Worker | Async cache | Progress reports | Progress total ms | Progress mean ms | Cache drain ms | Pending HWM | Pending after flush |");
            sb.AppendLine("|---|---|---:|---|---:|---:|---:|---:|---:|---:|");
            foreach (var run in report.Runs.Where(static x => x.AppLikeMode).OrderBy(static x => x.ScenarioName, StringComparer.Ordinal).ThenBy(static x => x.CacheState, StringComparer.Ordinal).ThenBy(static x => x.Workers).ThenBy(static x => x.Iteration))
            {
                sb.AppendLine(
                    $"| {run.ScenarioName} | {run.CacheState} | {run.Workers} | {run.CacheAsyncWrites} | {run.ProgressReports} | {run.ProgressReportTotalMs:F3} | {run.ProgressReportMeanMs:F4} | {run.CacheDrainMs:F2} | {run.CachePendingHighWaterMark} | {run.CachePendingAfterFlush} |");
            }
        }

        if (report.ScalingSummaries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Scaling Summary");
            sb.AppendLine();
            sb.AppendLine("| Scenario | Cache | Best worker | Best loadouts/sec | Recommended worker | Flags |");
            sb.AppendLine("|---|---|---:|---:|---:|---|");
            foreach (var summary in report.ScalingSummaries.OrderBy(static x => x.ScenarioName, StringComparer.Ordinal).ThenBy(static x => x.CacheState, StringComparer.Ordinal))
            {
                string flags = summary.Flags.Count == 0 ? "-" : string.Join(", ", summary.Flags);
                sb.AppendLine($"| {summary.ScenarioName} | {summary.CacheState} | {summary.BestWorker} | {summary.BestLoadoutsPerSecond:F1} | {summary.RecommendedWorker} | {flags} |");
            }

            sb.AppendLine();
            sb.AppendLine("| Scenario | Cache | Worker | Elapsed ms | Loadouts/sec | Speedup vs w1 | Efficiency | Throughput/core | Alloc MB | Gen0 | Gen1 | Gen2 |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var summary in report.ScalingSummaries.OrderBy(static x => x.ScenarioName, StringComparer.Ordinal).ThenBy(static x => x.CacheState, StringComparer.Ordinal))
            {
                foreach (var point in summary.Points.OrderBy(static x => x.Workers))
                {
                    sb.AppendLine(
                        $"| {summary.ScenarioName} | {summary.CacheState} | {point.Workers} | {point.MeanElapsedMs:F1} | {point.MeanLoadoutsPerSecond:F1} | {point.SpeedupVsSingle:F2} | {point.ParallelEfficiency:F2} | {point.ThroughputPerCore:F1} | {point.AllocatedMb:F1} | {point.GcCollectionsGen0} | {point.GcCollectionsGen1} | {point.GcCollectionsGen2} |");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Stage Metrics");
        sb.AppendLine();

        foreach (var run in report.Runs.OrderBy(static x => x.ScenarioName, StringComparer.Ordinal).ThenBy(static x => x.CacheState, StringComparer.Ordinal).ThenBy(static x => x.Workers).ThenBy(static x => x.Iteration))
        {
            sb.AppendLine($"### {run.ScenarioName} ({run.CacheState}) workers {run.Workers} iteration {run.Iteration}");
            sb.AppendLine();
            sb.AppendLine("| Stage | Kind | Candidates | Evaluated | Cache hit | Stage ms | Loadouts/sec | Worker util | Queue pressure |");
            sb.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var stage in run.StageRows)
            {
                sb.AppendLine(
                    $"| {stage.StageIndex} | {stage.Kind} | {stage.CandidateCount} | {stage.EvaluatedCount} | {stage.CacheHitRate:P1} | {stage.ElapsedMs:F1} | {stage.LoadoutsPerSecond:F1} | {stage.WorkerUtilization:P1} | {stage.QueuePressure:F3} |");
            }

            sb.AppendLine();
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    private static IReadOnlyList<DieType> LoadDiceCatalog(string root)
    {
        string path = Path.Combine(root, "data", "kcd2_dice_probabilities.json");
        var catalog = DiceProbabilityCatalog.LoadFromFile(path);
        return catalog.Entries
            .OrderBy(static x => x.Key, StringComparer.Ordinal)
            .Select(static entry =>
            {
                var probabilities = entry.Value.ToArray();
                return new DieType(entry.Key, probabilities, DiceQuality.FromProbabilities(probabilities));
            })
            .ToArray();
    }

    private static IReadOnlyList<BenchmarkScenario> BuildScenarios(IReadOnlyList<DieType> diceCatalog, string repoRoot)
    {
        return
        [
            BuildSyntheticScenario("small", diceCatalog, trackedDice: 8, maxOwnedEach: 2, maxLoadouts: 500, fullTurns: 180, seed: 1337),
            BuildSyntheticScenario("medium", diceCatalog, trackedDice: 14, maxOwnedEach: 3, maxLoadouts: 2000, fullTurns: 220, seed: 7331),
            BuildSyntheticScenario("large", diceCatalog, trackedDice: 20, maxOwnedEach: 4, maxLoadouts: 8000, fullTurns: 260, seed: 4242),
            BuildGroundTruthScenario("ground_truth", diceCatalog, repoRoot),
        ];
    }

    private static BenchmarkScenario BuildSyntheticScenario(
        string name,
        IReadOnlyList<DieType> diceCatalog,
        int trackedDice,
        int maxOwnedEach,
        int maxLoadouts,
        int fullTurns,
        int seed)
    {
        var available = new int[diceCatalog.Count];
        for (int i = 0; i < Math.Min(trackedDice, available.Length); i++)
        {
            available[i] = maxOwnedEach;
        }

        var qualities = diceCatalog.Select(static d => d.Quality).ToArray();
        long combinations = LoadoutSearch.CountCombinations(available, total: 6);
        var loadouts = combinations <= maxLoadouts
            ? LoadoutSearch.EnumerateLoadouts(available, total: 6)
                .Select(static counts => (IReadOnlyList<int>)counts.ToArray())
                .ToArray()
            : LoadoutSearch.RandomLoadouts(available, qualities, total: 6, limit: maxLoadouts, seed: seed)
                .Select(static counts => (IReadOnlyList<int>)counts.ToArray())
                .ToArray();

        var plan = DefaultEfficiencyPlan.SetItem(1, DefaultEfficiencyPlan[1] with { PilotTurns = fullTurns });
        var settings = new OptimizationSettings(
            TargetScore: 3000,
            TurnCap: 3000,
            NumTurns: fullTurns,
            RiskProfile: RiskProfile.Balanced,
            Objective: OptimizationObjective.MaxScore,
            ProbTurns: [10, 15, 20],
            EfficiencyEnabled: true,
            EfficiencySeed: seed,
            EfficiencyPlan: plan);

        return new BenchmarkScenario(name, loadouts, settings);
    }

    private static BenchmarkScenario BuildGroundTruthScenario(
        string name,
        IReadOnlyList<DieType> diceCatalog,
        string repoRoot)
    {
        string path = Path.Combine(repoRoot, "tests", "data", "ground_truth_inventory_1000_turns.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;
        JsonElement config = root.GetProperty("config");
        JsonElement owned = root.GetProperty("owned_dice");

        var indexByName = diceCatalog
            .Select((die, index) => (die.Name, index))
            .ToDictionary(static x => x.Name, static x => x.index, StringComparer.Ordinal);
        var available = new int[diceCatalog.Count];
        foreach (JsonProperty property in owned.EnumerateObject())
        {
            if (indexByName.TryGetValue(property.Name, out int index))
            {
                available[index] = property.Value.GetInt32();
            }
        }

        var loadouts = LoadoutSearch.EnumerateLoadouts(available, total: 6)
            .Select(static counts => (IReadOnlyList<int>)counts.ToArray())
            .ToArray();

        string riskRaw = config.GetProperty("risk_profile").GetString() ?? "Balanced";
        if (!Enum.TryParse<RiskProfile>(riskRaw, ignoreCase: true, out RiskProfile risk))
        {
            risk = RiskProfile.Balanced;
        }

        int turns = config.GetProperty("turns").GetInt32();
        int target = config.GetProperty("target").GetInt32();
        var settings = new OptimizationSettings(
            TargetScore: target,
            TurnCap: Math.Max(3000, target),
            NumTurns: turns,
            RiskProfile: risk,
            Objective: OptimizationObjective.MaxScore,
            ProbTurns: [10, 15, 20],
            EfficiencyEnabled: false,
            EfficiencySeed: 1,
            EfficiencyPlan: []);

        return new BenchmarkScenario(name, loadouts, settings);
    }

    private static RunOptions ParseRunOptions(string[] args, string repoRoot)
    {
        string markdownPath = Path.Combine(repoRoot, "docs", "performance-baseline.md");
        string jsonPath = Path.Combine(repoRoot, "benchmarks", "results", "performance-report.json");
        var scenarios = new HashSet<string>(new[] { "small", "medium", "large", "ground_truth" }, StringComparer.OrdinalIgnoreCase);
        int workers = 1;
        int iterations = 1;
        CacheModes cacheModes = CacheModes.Cold | CacheModes.Warm;
        var workerSweep = new List<int>();
        bool appLike = false;
        int progressIntervalMs = 100;
        bool? cacheAsyncOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--report" && i + 1 < args.Length)
            {
                markdownPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, args[++i]));
            }
            else if (arg == "--json" && i + 1 < args.Length)
            {
                jsonPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, args[++i]));
            }
            else if (arg == "--workers" && i + 1 < args.Length)
            {
                workers = Math.Max(1, int.Parse(args[++i]));
            }
            else if (arg == "--iterations" && i + 1 < args.Length)
            {
                iterations = Math.Max(1, int.Parse(args[++i]));
            }
            else if (arg == "--worker-sweep" && i + 1 < args.Length)
            {
                workerSweep = args[++i]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static x => Math.Max(1, int.Parse(x)))
                    .Distinct()
                    .Order()
                    .ToList();
            }
            else if (arg == "--cache-modes" && i + 1 < args.Length)
            {
                string mode = args[++i];
                cacheModes = mode.ToLowerInvariant() switch
                {
                    "cold" => CacheModes.Cold,
                    "warm" => CacheModes.Warm,
                    "both" => CacheModes.Cold | CacheModes.Warm,
                    _ => throw new InvalidOperationException("Invalid --cache-modes value. Use cold|warm|both."),
                };
            }
            else if (arg == "--app-like")
            {
                appLike = true;
            }
            else if (arg == "--progress-interval-ms" && i + 1 < args.Length)
            {
                progressIntervalMs = Math.Clamp(int.Parse(args[++i]), 10, 5000);
            }
            else if (arg == "--cache-async" && i + 1 < args.Length)
            {
                string raw = args[++i];
                cacheAsyncOverride = ParseBoolArg(raw);
            }
            else if (arg == "--scenarios" && i + 1 < args.Length)
            {
                scenarios = args[++i]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else if (arg == "--no-markdown")
            {
                markdownPath = string.Empty;
            }
            else if (arg == "--no-json")
            {
                jsonPath = string.Empty;
            }
        }

        return new RunOptions(
            markdownPath,
            jsonPath,
            scenarios.ToArray(),
            workers,
            workerSweep.ToArray(),
            iterations,
            cacheModes,
            appLike,
            progressIntervalMs,
            cacheAsyncOverride);
    }

    private static CompareOptions ParseCompareOptions(string[] args, string repoRoot)
    {
        string baseline = string.Empty;
        string current = string.Empty;
        string thresholds = Path.Combine(repoRoot, "benchmarks", "thresholds.json");
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--baseline" && i + 1 < args.Length)
            {
                baseline = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, args[++i]));
            }
            else if (arg == "--current" && i + 1 < args.Length)
            {
                current = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, args[++i]));
            }
            else if (arg == "--thresholds" && i + 1 < args.Length)
            {
                thresholds = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, args[++i]));
            }
        }

        if (string.IsNullOrWhiteSpace(baseline) || string.IsNullOrWhiteSpace(current))
        {
            throw new InvalidOperationException("compare mode requires --baseline <file> and --current <file>.");
        }

        return new CompareOptions(baseline, current, thresholds);
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static IReadOnlyList<ScalingSummary> BuildScalingSummaries(IReadOnlyList<ScenarioRunSummary> runs)
    {
        var summaries = new List<ScalingSummary>();
        var groups = runs
            .GroupBy(static x => (x.ScenarioName, x.CacheState));

        foreach (var group in groups)
        {
            var byWorker = group
                .GroupBy(static x => x.Workers)
                .Select(static x =>
                {
                    double meanElapsedMs = x.Average(static y => y.TotalElapsedMs);
                    double meanLoadoutsPerSecond = x.Average(static y => y.LoadoutsPerSecond);
                    double allocatedMb = x.Average(static y => y.AllocatedBytes / (1024.0 * 1024.0));
                    return new
                    {
                        Workers = x.Key,
                        MeanElapsedMs = meanElapsedMs,
                        MeanLoadoutsPerSecond = meanLoadoutsPerSecond,
                        AllocatedMb = allocatedMb,
                        Gc0 = x.Sum(static y => y.GcCollectionsGen0),
                        Gc1 = x.Sum(static y => y.GcCollectionsGen1),
                        Gc2 = x.Sum(static y => y.GcCollectionsGen2),
                    };
                })
                .OrderBy(static x => x.Workers)
                .ToArray();

            if (byWorker.Length < 2)
            {
                continue;
            }

            var single = byWorker[0];
            double singleThroughput = Math.Max(0.0001, single.MeanLoadoutsPerSecond);
            var points = byWorker
                .Select(static x => x)
                .Select(x =>
                {
                    double speedup = x.MeanLoadoutsPerSecond / singleThroughput;
                    double efficiency = speedup / Math.Max(1, x.Workers);
                    return new ScalingPoint(
                        Workers: x.Workers,
                        MeanElapsedMs: x.MeanElapsedMs,
                        MeanLoadoutsPerSecond: x.MeanLoadoutsPerSecond,
                        SpeedupVsSingle: speedup,
                        ParallelEfficiency: efficiency,
                        ThroughputPerCore: x.MeanLoadoutsPerSecond / Math.Max(1, x.Workers),
                        AllocatedMb: x.AllocatedMb,
                        GcCollectionsGen0: x.Gc0,
                        GcCollectionsGen1: x.Gc1,
                        GcCollectionsGen2: x.Gc2);
                })
                .ToArray();

            var best = points.MaxBy(static x => x.MeanLoadoutsPerSecond)!;
            int recommendedWorker = points
                .Where(x => x.MeanLoadoutsPerSecond >= best.MeanLoadoutsPerSecond * 0.95)
                .Min(static x => x.Workers);

            var flags = new List<string>();
            var maxWorkerPoint = points[^1];
            if (maxWorkerPoint.ParallelEfficiency < 0.35)
            {
                flags.Add("low_parallel_efficiency");
            }

            if (maxWorkerPoint.AllocatedMb > points[0].AllocatedMb * 1.20)
            {
                flags.Add("allocation_growth");
            }

            int baseGcHeavy = points[0].GcCollectionsGen1 + points[0].GcCollectionsGen2;
            int maxGcHeavy = maxWorkerPoint.GcCollectionsGen1 + maxWorkerPoint.GcCollectionsGen2;
            if (baseGcHeavy > 0 && maxGcHeavy >= (baseGcHeavy * 2))
            {
                flags.Add("gc_pressure");
            }

            summaries.Add(
                new ScalingSummary(
                    ScenarioName: group.Key.ScenarioName,
                    CacheState: group.Key.CacheState,
                    BestWorker: best.Workers,
                    BestLoadoutsPerSecond: best.MeanLoadoutsPerSecond,
                    RecommendedWorker: recommendedWorker,
                    Flags: flags,
                    Points: points));
        }

        return summaries;
    }

    private static double PercentDeltaHigherWorse(double baseline, double current)
    {
        if (baseline <= 0.0)
        {
            return current > 0.0 ? 100.0 : 0.0;
        }

        return ((current - baseline) / baseline) * 100.0;
    }

    private static double PercentDeltaLowerWorse(double baseline, double current)
    {
        if (baseline <= 0.0)
        {
            return current < baseline ? 100.0 : 0.0;
        }

        return ((baseline - current) / baseline) * 100.0;
    }

    private static bool ParseBoolArg(string value)
    {
        if (bool.TryParse(value, out bool parsed))
        {
            return parsed;
        }

        return value switch
        {
            "1" => true,
            "0" => false,
            _ => throw new InvalidOperationException("Invalid boolean value. Use true|false or 1|0."),
        };
    }

    private sealed record BenchmarkScenario(
        string Name,
        IReadOnlyList<IReadOnlyList<int>> Loadouts,
        OptimizationSettings Settings);

    private sealed record RunOptions(
        string MarkdownPath,
        string JsonPath,
        IReadOnlyList<string> Scenarios,
        int Workers,
        IReadOnlyList<int> WorkerSweep,
        int Iterations,
        CacheModes CacheModes,
        bool AppLike,
        int ProgressIntervalMs,
        bool? CacheAsyncOverride);

    private sealed record CompareOptions(
        string BaselinePath,
        string CurrentPath,
        string ThresholdsPath);

    private sealed record RuntimeMetadata(
        string DotnetVersion,
        string OsDescription,
        string ProcessArchitecture,
        int ProcessorCount);

    private sealed record BenchmarkReport(
        int Version,
        DateTimeOffset TimestampUtc,
        RuntimeMetadata Runtime,
        RunOptions Options,
        IReadOnlyList<ScenarioRunSummary> Runs,
        IReadOnlyList<ScalingSummary> ScalingSummaries,
        double WallClockSeconds);

    [Flags]
    private enum CacheModes
    {
        Cold = 1,
        Warm = 2,
    }

    private sealed record ScenarioRunSummary(
        string ScenarioName,
        string CacheState,
        int Iteration,
        int Workers,
        int LoadoutCount,
        int StageCount,
        int FinalCandidateCount,
        double TotalElapsedMs,
        double TelemetryElapsedMs,
        double LoadoutsPerSecond,
        double CacheHitRate,
        double WorkerUtilization,
        double QueuePressure,
        double EngineMeanMs,
        double CacheReadMs,
        double CacheWriteMs,
        long AllocatedBytes,
        int GcCollectionsGen0,
        int GcCollectionsGen1,
        int GcCollectionsGen2,
        bool AppLikeMode,
        bool CacheAsyncWrites,
        long ProgressReports,
        double ProgressReportTotalMs,
        double ProgressReportMeanMs,
        int CachePendingHighWaterMark,
        int CachePendingAfterFlush,
        double CacheDrainMs,
        IReadOnlyList<StageSummary> StageRows,
        IReadOnlyDictionary<string, MetricSnapshot> PerfMetrics)
    {
        public string Key => $"{ScenarioName}|{CacheState}";
    }

    private sealed record StageSummary(
        int StageIndex,
        string Kind,
        int CandidateCount,
        int EvaluatedCount,
        double CacheHitRate,
        double ElapsedMs,
        double LoadoutsPerSecond,
        double WorkerUtilization,
        double QueuePressure);

    private sealed record MetricSnapshot(
        long Count,
        double Sum,
        double Min,
        double Max,
        double Mean);

    private sealed record ScalingSummary(
        string ScenarioName,
        string CacheState,
        int BestWorker,
        double BestLoadoutsPerSecond,
        int RecommendedWorker,
        IReadOnlyList<string> Flags,
        IReadOnlyList<ScalingPoint> Points);

    private sealed record ScalingPoint(
        int Workers,
        double MeanElapsedMs,
        double MeanLoadoutsPerSecond,
        double SpeedupVsSingle,
        double ParallelEfficiency,
        double ThroughputPerCore,
        double AllocatedMb,
        int GcCollectionsGen0,
        int GcCollectionsGen1,
        int GcCollectionsGen2);

    private sealed record ThresholdConfig(
        double MaxRuntimeRegressionPercent,
        double MaxThroughputRegressionPercent,
        double MaxEngineMeanRegressionPercent,
        double MaxCacheReadRegressionPercent);

    private sealed class InMemoryPerfSink : IPerfSink
    {
        private readonly ConcurrentDictionary<string, MetricAccumulator> _metrics = new(StringComparer.Ordinal);

        public InMemoryPerfSink(bool enabled)
        {
            Enabled = enabled;
        }

        public bool Enabled { get; }

        public void Increment(string metric, long delta = 1)
        {
            if (!Enabled)
            {
                return;
            }

            GetAccumulator(metric).Add(delta);
        }

        public void ObserveDurationMs(string metric, double milliseconds)
        {
            if (!Enabled)
            {
                return;
            }

            GetAccumulator(metric).Add(milliseconds);
        }

        public void ObserveValue(string metric, double value)
        {
            if (!Enabled)
            {
                return;
            }

            GetAccumulator(metric).Add(value);
        }

        public IReadOnlyDictionary<string, MetricSnapshot> Snapshot()
        {
            var snapshots = new Dictionary<string, MetricSnapshot>(StringComparer.Ordinal);
            foreach ((string key, MetricAccumulator value) in _metrics.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                snapshots[key] = value.Snapshot();
            }

            return snapshots;
        }

        private MetricAccumulator GetAccumulator(string metric)
        {
            return _metrics.GetOrAdd(metric, static _ => new MetricAccumulator());
        }
    }

    private sealed class MetricAccumulator
    {
        private readonly object _sync = new();
        private long _count;
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        public void Add(double value)
        {
            lock (_sync)
            {
                _count++;
                _sum += value;
                if (value < _min)
                {
                    _min = value;
                }

                if (value > _max)
                {
                    _max = value;
                }
            }
        }

        public MetricSnapshot Snapshot()
        {
            lock (_sync)
            {
                double min = _count == 0 ? 0.0 : _min;
                double max = _count == 0 ? 0.0 : _max;
                double mean = _count == 0 ? 0.0 : _sum / _count;
                return new MetricSnapshot(_count, _sum, min, max, mean);
            }
        }
    }

    private sealed class ProgressProbe : IProgress<OptimizationProgress>
    {
        private long _count;
        private long _ticks;

        public long Count => Interlocked.Read(ref _count);

        public double TotalMs => (1000.0 * Interlocked.Read(ref _ticks)) / Stopwatch.Frequency;

        public double MeanMs => Count == 0 ? 0.0 : TotalMs / Count;

        public void Report(OptimizationProgress value)
        {
            long start = Stopwatch.GetTimestamp();

            double elapsedSeconds = Math.Max(0.001, value.ElapsedMs / 1000.0);
            double loadoutsPerSecond = value.ProcessedLoadouts / elapsedSeconds;
            int remainingLoadouts = Math.Max(0, value.TotalLoadouts - value.ProcessedLoadouts);
            double percent = value.TotalLoadouts <= 0 ? 0.0 : (value.ProcessedLoadouts * 100.0) / value.TotalLoadouts;
            _ = loadoutsPerSecond + remainingLoadouts + percent + value.CacheHits + value.CacheMisses + value.StageIndex + value.StageCount;

            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - start);
        }
    }
}
