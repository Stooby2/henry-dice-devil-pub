using System.Collections.Immutable;
using System.Diagnostics;
using HenrysDiceDevil.Domain.Diagnostics;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Simulation.Contracts;
using HenrysDiceDevil.Simulation.Scoring;

namespace HenrysDiceDevil.Simulation.Runtime;

public sealed class TurnSimulationEngine : ISimulationEngine
{
    private readonly ScoringGroupEngine _scoring = new();
    private readonly PolicyEstimator _estimator;
    private readonly IPerfSink _perfSink;

    public TurnSimulationEngine(IPerfSink? perfSink = null)
    {
        _estimator = new PolicyEstimator(_scoring);
        _perfSink = perfSink ?? NullPerfSink.Instance;
    }

    public SimulationResult Run(SimulationRequest request)
    {
        bool perfEnabled = _perfSink.Enabled;
        long runStart = perfEnabled ? Stopwatch.GetTimestamp() : 0;
        _perfSink.Increment("engine.run.invocations");

        long setupStart = perfEnabled ? Stopwatch.GetTimestamp() : 0;
        var loadout = BuildLoadout(request.DiceCatalog, request.Counts);
        if (loadout.Count == 0)
        {
            throw new InvalidOperationException("Loadout is empty.");
        }
        var qualityByIndex = new double[loadout.Count];
        for (int i = 0; i < loadout.Count; i++)
        {
            qualityByIndex[i] = loadout[i].Quality;
        }

        int seed = request.SeedBase.HasValue
            ? SeedFactory.BuildSeed(request.SeedBase.Value, request.Counts)
            : Environment.TickCount;
        var rng = new Random(seed);

        var avg = ComputeAverageProbabilities(loadout);
        var bustByK = new double[7];
        var evByK = new double[7];
        for (int k = 1; k <= 6; k++)
        {
            var (bust, ev) = _estimator.EstimateBustAndEvExact(avg, k);
            bustByK[k] = bust;
            evByK[k] = ev;
        }

        var cdfs = BuildCdfs(loadout);
        var policy = RiskPolicyCatalog.Resolve(request.Settings.RiskProfile);
        int turns = Math.Max(1, request.Settings.NumTurns);
        int cap = Math.Max(request.Settings.TurnCap, request.Settings.TargetScore);
        var histogram = new double[cap + 1];
        if (perfEnabled)
        {
            _perfSink.ObserveDurationMs("engine.run.setup_ms", ElapsedMs(setupStart));
            _perfSink.ObserveValue("engine.run.turns", turns);
            _perfSink.ObserveValue("engine.run.cap", cap);
            _perfSink.ObserveValue("engine.run.loadout_size", loadout.Count);
        }

        var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int totalGroups = 0;
        int scoringTurns = 0;
        var selectionCache = new Dictionary<int, ScoreSelection>();
        var turnProbe = perfEnabled ? new TurnProbeAccumulator() : null;

        long simStart = perfEnabled ? Stopwatch.GetTimestamp() : 0;
        double total = 0.0;
        double totalSq = 0.0;
        for (int i = 0; i < turns; i++)
        {
            int score = SimulateTurn(
                rng,
                loadout,
                cdfs,
                request.Settings.TargetScore,
                policy,
                bustByK,
                evByK,
                qualityByIndex,
                tagCounts,
                ref totalGroups,
                selectionCache,
                turnProbe);

            if (score >= cap)
            {
                histogram[cap] += 1.0;
            }
            else
            {
                histogram[score] += 1.0;
            }
            if (score > 0)
            {
                scoringTurns++;
            }

            total += score;
            totalSq += (score * score);
        }
        if (perfEnabled)
        {
            _perfSink.ObserveDurationMs("engine.run.simulation_loop_ms", ElapsedMs(simStart));
            if (turnProbe is not null)
            {
                _perfSink.ObserveDurationMs("engine.turn.roll_faces_ms", turnProbe.RollFacesMs);
                _perfSink.ObserveDurationMs("engine.turn.score_groups_ms", turnProbe.ScoreGroupsMs);
                _perfSink.ObserveDurationMs("engine.turn.select_best_ms", turnProbe.SelectBestMs);
                _perfSink.ObserveDurationMs("engine.turn.choose_remaining_ms", turnProbe.ChooseRemainingMs);
                _perfSink.Increment("engine.turn.roll_calls", turnProbe.RollCalls);
                _perfSink.Increment("engine.turn.score_calls", turnProbe.ScoreCalls);
                _perfSink.Increment("engine.turn.select_best_calls", turnProbe.SelectBestCalls);
                _perfSink.Increment("engine.turn.choose_remaining_calls", turnProbe.ChooseRemainingCalls);
                _perfSink.ObserveValue("engine.turn.loop_iterations", turnProbe.LoopIterations);
            }
        }

        double mean = total / turns;
        double variance = Math.Max(0.0, (totalSq / turns) - (mean * mean));
        double std = Math.Sqrt(variance);

        for (int i = 0; i < histogram.Length; i++)
        {
            histogram[i] /= turns;
        }

        long dpStart = perfEnabled ? Stopwatch.GetTimestamp() : 0;
        var metrics = DpMetricsComputer.Compute(
            turnDistribution: histogram,
            target: request.Settings.TargetScore,
            probTurns: request.Settings.ProbTurns);
        if (perfEnabled)
        {
            _perfSink.ObserveDurationMs("engine.run.dp_metrics_ms", ElapsedMs(dpStart));
        }
        metrics = metrics with { EvPointsSe = std / Math.Sqrt(turns) };

        var result = new SimulationResult(
            Counts: request.Counts.ToArray(),
            Metrics: metrics,
            MeanPoints: mean,
            StandardDeviation: std,
            TagCounts: tagCounts,
            TotalGroups: totalGroups,
            ScoringTurns: scoringTurns);
        if (perfEnabled)
        {
            _perfSink.ObserveDurationMs("engine.run.total_ms", ElapsedMs(runStart));
            _perfSink.ObserveValue("engine.run.scoring_turns", scoringTurns);
            _perfSink.ObserveValue("engine.run.total_groups", totalGroups);
        }

        return result;
    }

    private int SimulateTurn(
        Random rng,
        IReadOnlyList<DieType> loadout,
        IReadOnlyList<double[]> cdfs,
        int target,
        RiskPolicy policy,
        ReadOnlySpan<double> bustByK,
        ReadOnlySpan<double> evByK,
        ReadOnlySpan<double> qualityByIndex,
        Dictionary<string, int> tagCounts,
        ref int totalGroups,
        Dictionary<int, ScoreSelection> selectionCache,
        TurnProbeAccumulator? turnProbe)
    {
        int maxDice = loadout.Count;
        Span<int> remainingIndices = stackalloc int[maxDice];
        Span<int> nextRemainingIndices = stackalloc int[maxDice];
        Span<int> rolledIndices = stackalloc int[maxDice];
        Span<byte> rolledFaces = stackalloc byte[maxDice];
        Span<int> counts = stackalloc int[6];
        Span<int> faceCounts = stackalloc int[7];
        Span<int> facePositions = stackalloc int[7 * maxDice];
        Span<bool> removed = stackalloc bool[maxDice];

        for (int i = 0; i < maxDice; i++)
        {
            remainingIndices[i] = i;
        }

        int remainingCount = maxDice;
        int accumulated = 0;

        while (true)
        {
            turnProbe?.IncrementLoopIterations();
            long opStart = turnProbe is not null ? Stopwatch.GetTimestamp() : 0;
            counts.Clear();
            for (int i = 0; i < remainingCount; i++)
            {
                int dieIndex = remainingIndices[i];
                double u = rng.NextDouble();
                int face = 1;
                while (face <= 6 && u > cdfs[dieIndex][face - 1])
                {
                    face++;
                }

                rolledIndices[i] = dieIndex;
                rolledFaces[i] = (byte)Math.Min(face, 6);
                counts[rolledFaces[i] - 1]++;
            }

            if (turnProbe is not null)
            {
                turnProbe.AddRollFaces(opStart);
            }

            opStart = turnProbe is not null ? Stopwatch.GetTimestamp() : 0;
            int cacheKey = PackCountsKey(counts);
            var selections = _scoring.ScoreGroupsForPackedKey(cacheKey);
            if (turnProbe is not null)
            {
                turnProbe.AddScoreGroups(opStart);
            }
            if (selections.Length == 0)
            {
                return 0;
            }

            if (!selectionCache.TryGetValue(cacheKey, out var selected))
            {
                opStart = turnProbe is not null ? Stopwatch.GetTimestamp() : 0;
                selected = SelectBest(scoringSelections: selections, policy, evByK, bustByK, loadout.Count);
                if (turnProbe is not null)
                {
                    turnProbe.AddSelectBest(opStart);
                }
                selectionCache[cacheKey] = selected;
            }

            opStart = turnProbe is not null ? Stopwatch.GetTimestamp() : 0;
            faceCounts.Clear();
            removed.Clear();
            for (int i = 0; i < remainingCount; i++)
            {
                int face = rolledFaces[i];
                int slot = faceCounts[face]++;
                facePositions[(face * maxDice) + slot] = i;
            }

            for (int face = 1; face <= 6; face++)
            {
                int useN = selected.UsedCounts[face - 1];
                if (useN <= 0)
                {
                    continue;
                }

                int countForFace = faceCounts[face];
                int picks = Math.Min(useN, countForFace);
                int baseOffset = face * maxDice;

                // Sort this face bucket by die quality so we keep best dice and spend worst first.
                for (int i = 0; i < countForFace - 1; i++)
                {
                    int best = i;
                    double bestQuality = qualityByIndex[rolledIndices[facePositions[baseOffset + best]]];
                    for (int j = i + 1; j < countForFace; j++)
                    {
                        double candidate = qualityByIndex[rolledIndices[facePositions[baseOffset + j]]];
                        if (candidate < bestQuality)
                        {
                            best = j;
                            bestQuality = candidate;
                        }
                    }

                    if (best != i)
                    {
                        int a = facePositions[baseOffset + i];
                        facePositions[baseOffset + i] = facePositions[baseOffset + best];
                        facePositions[baseOffset + best] = a;
                    }
                }

                for (int pick = 0; pick < picks; pick++)
                {
                    int pos = facePositions[baseOffset + pick];
                    removed[pos] = true;
                }
            }

            int nextCount = 0;
            for (int i = 0; i < remainingCount; i++)
            {
                if (!removed[i])
                {
                    nextRemainingIndices[nextCount++] = rolledIndices[i];
                }
            }

            if (turnProbe is not null)
            {
                turnProbe.AddChooseRemaining(opStart);
            }
            accumulated += selected.Points;

            foreach (var tag in selected.Tags)
            {
                tagCounts[tag.Key] = tagCounts.GetValueOrDefault(tag.Key) + tag.Value;
                totalGroups += tag.Value;
            }

            if (accumulated >= target)
            {
                return accumulated;
            }

            if (nextCount == 0)
            {
                for (int i = 0; i < maxDice; i++)
                {
                    remainingIndices[i] = i;
                }

                remainingCount = maxDice;
                continue;
            }

            int bankThreshold = policy.BankThreshold;
            double bustLimit = policy.BustLimit;

            if (accumulated >= bankThreshold)
            {
                return accumulated;
            }

            if (nextCount < bustByK.Length && bustByK[nextCount] <= bustLimit)
            {
                for (int i = 0; i < nextCount; i++)
                {
                    remainingIndices[i] = nextRemainingIndices[i];
                }

                remainingCount = nextCount;
                continue;
            }

            return accumulated;
        }
    }

    private static ScoreSelection SelectBest(
        IReadOnlyList<ScoreSelection> scoringSelections,
        RiskPolicy policy,
        ReadOnlySpan<double> evByK,
        ReadOnlySpan<double> bustByK,
        int totalDice)
    {
        ScoreSelection? best = null;
        double bestValue = double.NegativeInfinity;

        foreach (var selection in scoringSelections)
        {
            int rem = totalDice - selection.UsedDice;
            double ev = rem < evByK.Length ? evByK[rem] : 0.0;
            double bust = rem < bustByK.Length ? bustByK[rem] : 1.0;
            double value = selection.Points
                + (policy.Alpha * ev)
                - (policy.Beta * bust * 500.0);
            if (value > bestValue)
            {
                best = selection;
                bestValue = value;
            }
        }

        return best ?? scoringSelections[0];
    }

    private static IReadOnlyList<double[]> BuildCdfs(IReadOnlyList<DieType> loadout)
    {
        var cdfs = new List<double[]>(loadout.Count);
        foreach (var die in loadout)
        {
            double[] cdf = new double[6];
            double total = 0.0;
            for (int face = 1; face <= 6; face++)
            {
                total += die.Probabilities[face];
                cdf[face - 1] = total;
            }

            if (total <= 0.0)
            {
                throw new InvalidOperationException($"Die {die.Name} has invalid probabilities.");
            }

            for (int i = 0; i < cdf.Length; i++)
            {
                cdf[i] /= total;
            }

            cdfs.Add(cdf);
        }

        return cdfs;
    }

    private static double[] ComputeAverageProbabilities(IReadOnlyList<DieType> loadout)
    {
        double[] avg = new double[6];
        foreach (var die in loadout)
        {
            for (int face = 1; face <= 6; face++)
            {
                avg[face - 1] += die.Probabilities[face];
            }
        }

        for (int i = 0; i < 6; i++)
        {
            avg[i] /= loadout.Count;
        }

        double total = 0.0;
        for (int i = 0; i < avg.Length; i++)
        {
            total += avg[i];
        }

        if (total <= 0.0)
        {
            throw new InvalidOperationException("Invalid average probabilities.");
        }

        for (int i = 0; i < 6; i++)
        {
            avg[i] /= total;
        }

        return avg;
    }

    private static IReadOnlyList<DieType> BuildLoadout(IReadOnlyList<DieType> dice, IReadOnlyList<int> counts)
    {
        int total = 0;
        for (int i = 0; i < Math.Min(dice.Count, counts.Count); i++)
        {
            total += counts[i];
        }

        var loadout = new List<DieType>(total);
        for (int i = 0; i < Math.Min(dice.Count, counts.Count); i++)
        {
            for (int n = 0; n < counts[i]; n++)
            {
                loadout.Add(dice[i]);
            }
        }

        return loadout;
    }

    private static int PackCountsKey(IReadOnlyList<int> counts)
    {
        int key = 0;
        for (int i = 0; i < 6; i++)
        {
            key |= (counts[i] & 0x7) << (i * 3);
        }

        return key;
    }

    private static int PackCountsKey(ReadOnlySpan<int> counts)
    {
        int key = 0;
        for (int i = 0; i < 6; i++)
        {
            key |= (counts[i] & 0x7) << (i * 3);
        }

        return key;
    }

    private static double ElapsedMs(long startTimestamp)
    {
        long elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        return (1000.0 * elapsed) / Stopwatch.Frequency;
    }

    private sealed class TurnProbeAccumulator
    {
        private long _rollTicks;
        private long _scoreTicks;
        private long _selectTicks;
        private long _remainingTicks;
        private long _rollCalls;
        private long _scoreCalls;
        private long _selectCalls;
        private long _remainingCalls;
        private long _loopIterations;

        public double RollFacesMs => ToMs(_rollTicks);

        public double ScoreGroupsMs => ToMs(_scoreTicks);

        public double SelectBestMs => ToMs(_selectTicks);

        public double ChooseRemainingMs => ToMs(_remainingTicks);

        public long RollCalls => _rollCalls;

        public long ScoreCalls => _scoreCalls;

        public long SelectBestCalls => _selectCalls;

        public long ChooseRemainingCalls => _remainingCalls;

        public long LoopIterations => _loopIterations;

        public void AddRollFaces(long start)
        {
            _rollTicks += Stopwatch.GetTimestamp() - start;
            _rollCalls++;
        }

        public void AddScoreGroups(long start)
        {
            _scoreTicks += Stopwatch.GetTimestamp() - start;
            _scoreCalls++;
        }

        public void AddSelectBest(long start)
        {
            _selectTicks += Stopwatch.GetTimestamp() - start;
            _selectCalls++;
        }

        public void AddChooseRemaining(long start)
        {
            _remainingTicks += Stopwatch.GetTimestamp() - start;
            _remainingCalls++;
        }

        public void IncrementLoopIterations()
        {
            _loopIterations++;
        }

        private static double ToMs(long ticks)
        {
            return (1000.0 * ticks) / Stopwatch.Frequency;
        }
    }
}
