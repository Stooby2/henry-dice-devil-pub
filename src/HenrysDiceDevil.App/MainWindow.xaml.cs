using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using HenrysDiceDevil.Domain.Models;
using HenrysDiceDevil.Domain.Settings;
using HenrysDiceDevil.Infrastructure.Caching;
using HenrysDiceDevil.Infrastructure.Data;
using HenrysDiceDevil.Simulation.Contracts;
using HenrysDiceDevil.Simulation.Optimization;
using HenrysDiceDevil.Simulation.Runtime;
using HenrysDiceDevil.Simulation.Search;
using HenrysDiceDevil.Simulation.Workers;

namespace HenrysDiceDevil.App;

public partial class MainWindow : Window
{
    private const string OrdinaryDieName = "Ordinary die";
    private const int RecommendedTargetScore = 3000;
    private const int RecommendedTurns = 50000;
    private const int RecommendedEfficiencySeed = 1;

    private readonly ObservableCollection<OwnedDieRow> _ownedRows = [];
    private readonly ObservableCollection<EfficiencyStageRow> _efficiencyRows = [];
    private readonly ObservableCollection<ResultRow> _resultRows = [];
    private IReadOnlyList<SimulationResult> _lastRunResults = [];
    private int _lastResultLimit = 50;
    private readonly List<DieType> _diceCatalog = [];
    private readonly OptimizationWorkflow _workflow;
    private readonly FileResultCacheStore _cacheStore;
    private readonly string _ownedDiceStatePath;
    private readonly string _uiStatePath;
    private readonly string _uxMetricsPath;
    private readonly string _lastResultsStatePath;
    private readonly ICollectionView _ownedView;
    private readonly Dictionary<string, int> _savedOwnedCounts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _runCancellation;
    private string _diceFilterText = string.Empty;
    private bool _isAdvancedVisible;
    private UxMetricsState _uxMetrics = new();

    public MainWindow()
    {
        InitializeComponent();
        string runtimeRoot = ResolveRuntimeRoot();
        _ownedDiceStatePath = System.IO.Path.Combine(runtimeRoot, "cache", "owned-dice-state.json");
        _uiStatePath = System.IO.Path.Combine(runtimeRoot, "cache", "ui-state.json");
        _uxMetricsPath = System.IO.Path.Combine(runtimeRoot, "cache", "ux-metrics.json");
        _lastResultsStatePath = System.IO.Path.Combine(runtimeRoot, "cache", "last-results-state.json");

        _cacheStore = new FileResultCacheStore(
            rootDirectory: System.IO.Path.Combine(runtimeRoot, "cache"),
            enableAsyncWrites: true,
            maxPendingEntries: 250_000,
            writerFlushIntervalMs: 50);
        _workflow = new OptimizationWorkflow(
            new LoadoutEvaluator(new TurnSimulationEngine()),
            _cacheStore);

        _ownedView = CollectionViewSource.GetDefaultView(_ownedRows);
        _ownedView.Filter = OwnedRowFilter;
        OwnedDataGrid.ItemsSource = _ownedView;
        EfficiencyDataGrid.ItemsSource = _efficiencyRows;
        ResultsDataGrid.ItemsSource = _resultRows;
        LoadDiceCatalog();
        LoadOwnedDiceState();
        CaptureSavedOwnedCounts();
        LoadDefaultEfficiencyPlan();
        ApplyRecommendedDefaults(updateStatus: false);
        ResultsObjectiveComboBox.SelectedIndex = ObjectiveComboBox.SelectedIndex;
        LoadUiState();
        LoadUxMetrics();
        LoadLastResultsState();
        MainTabControl.SelectedItem = DiceTab;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        TryEnableDarkTitleBar();
    }

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int trueValue = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref trueValue, Marshal.SizeOf<int>());
            _ = DwmSetWindowAttribute(hwnd, 19, ref trueValue, Marshal.SizeOf<int>());
        }
        catch
        {
            // Ignore on unsupported Windows versions.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void LoadDiceCatalog()
    {
        string runtimeRoot = ResolveRuntimeRoot();
        string path = System.IO.Path.Combine(runtimeRoot, "data", "kcd2_dice_probabilities.json");
        var catalog = DiceProbabilityCatalog.LoadFromFile(path);

        foreach (var entry in catalog.Entries.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            var probs = entry.Value.ToArray();
            double quality = DiceQuality.FromProbabilities(probs);
            var die = new DieType(entry.Key, probs, quality);
            _diceCatalog.Add(die);

            _ownedRows.Add(
                new OwnedDieRow
                {
                    Name = entry.Key,
                    OwnedCount = entry.Key == OrdinaryDieName ? 6 : 0,
                    P1 = $"{probs[1] * 100.0:F1}%",
                    P2 = $"{probs[2] * 100.0:F1}%",
                    P3 = $"{probs[3] * 100.0:F1}%",
                    P4 = $"{probs[4] * 100.0:F1}%",
                    P5 = $"{probs[5] * 100.0:F1}%",
                    P6 = $"{probs[6] * 100.0:F1}%",
                });
        }
    }

    private async void OptimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();

            SetRunControls(isRunning: true);
            StatusTextBlock.Text = "Preparing optimization...";
            RunProgressBar.IsIndeterminate = true;
            RunProgressBar.Value = 0;
            CommitGridEdits();
            SaveOwnedDiceState();
            SaveUiState();
            var optimizeWatch = Stopwatch.StartNew();

            int target = (int)Math.Round(TargetScoreSlider.Value);
            int turns = ParseInt(TurnsTextBox.Text, RecommendedTurns, min: 100);
            int effSeed = ParseInt(EfficiencySeedTextBox.Text, 1, min: 1);

            var risk = ParseEnumFromCombo<RiskProfile>(RiskProfileComboBox, RiskProfile.Balanced);
            var objective = ParseEnumFromCombo<OptimizationObjective>(ObjectiveComboBox, OptimizationObjective.MaxScore);
            bool efficiency = EfficiencyCheckBox.IsChecked == true;
            var performanceProfile = ParseEnumFromCombo<PerformanceProfile>(PerformanceComboBox, PerformanceProfile.High);
            int workerCount = ResolveWorkerCount(performanceProfile, Environment.ProcessorCount);

            ImmutableArray<EfficiencyStage> efficiencyPlan = BuildEfficiencyPlan();
            var planErrors = EfficiencyPlanValidator.Validate(efficiencyPlan);
            if (planErrors.Length > 0)
            {
                StatusTextBlock.Text = "Efficiency plan validation failed.";
                MessageBox.Show(string.Join(Environment.NewLine, planErrors), "Efficiency Plan Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int effectiveTurns = efficiency
                ? (efficiencyPlan.Length == 0 ? RecommendedTurns : Math.Max(1, efficiencyPlan[^1].PilotTurns))
                : turns;

            var settings = new OptimizationSettings(
                TargetScore: target,
                TurnCap: Math.Max(3000, target),
                NumTurns: effectiveTurns,
                RiskProfile: risk,
                Objective: objective,
                ProbTurns: [10, 15, 20],
                EfficiencyEnabled: efficiency,
                EfficiencySeed: effSeed,
                EfficiencyPlan: efficiencyPlan);

            var available = BuildAvailableCountsForOptimization();
            StatusTextBlock.Text = "Enumerating exhaustive 6-die loadouts...";
            await Task.Yield();

            var token = _runCancellation.Token;
            var (combinations, loadouts) = await Task.Run(
                () =>
                {
                    long combos = LoadoutSearch.CountCombinations(available, total: 6);
                    var generated = LoadoutSearch
                        .EnumerateLoadouts(available, total: 6)
                        .Select(static x => (IReadOnlyList<int>)x.ToArray())
                        .ToArray();
                    return (combos, generated);
                },
                token);

            StatusTextBlock.Text = $"Prepared {loadouts.Length} loadouts. Starting simulation...";
            MainTabControl.SelectedItem = ResultsTab;

            var progress = new Progress<OptimizationProgress>(p =>
            {
                double elapsedSeconds = Math.Max(0.001, p.ElapsedMs / 1000.0);
                double loadoutsPerSecond = p.ProcessedLoadouts / elapsedSeconds;
                int remainingLoadouts = Math.Max(0, p.TotalLoadouts - p.ProcessedLoadouts);
                double percent = p.TotalLoadouts <= 0 ? 0.0 : (p.ProcessedLoadouts * 100.0) / p.TotalLoadouts;
                TimeSpan eta = loadoutsPerSecond <= 0.0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(remainingLoadouts / loadoutsPerSecond);

                RunProgressBar.IsIndeterminate = false;
                RunProgressBar.Value = Math.Clamp(percent, 0.0, 100.0);

                StatusTextBlock.Text =
                    $"Stage {p.StageIndex + 1}/{p.StageCount} ({p.StageKind})  {p.ProcessedLoadouts}/{p.TotalLoadouts}  {loadoutsPerSecond:F0} loadouts/s  ETA {eta:mm\\:ss}  cache hits:{p.CacheHits}";
            });

            var result = await Task.Run(
                () => _workflow.Run(
                    loadouts,
                    _diceCatalog,
                    settings,
                    workerCount: workerCount,
                    progress: progress,
                    cancellationToken: token),
                token);
            _lastRunResults = result.Results.ToArray();
            _lastResultLimit = efficiency
                ? (efficiencyPlan.Length == 0 ? 50 : Math.Max(1, efficiencyPlan[^1].MinSurvivors))
                : 50;
            ResultsObjectiveComboBox.SelectedIndex = ObjectiveComboBox.SelectedIndex;
            RebuildResultsRows(objective);
            optimizeWatch.Stop();
            RecordUxMetricRun(optimizeWatch.Elapsed.TotalMilliseconds, loadouts.Length);

            StatusTextBlock.Text = $"Done. Evaluated all {combinations} combinations. Kept {result.FinalCandidateCount} after {result.StageCount} stage(s). Showing top {_lastResultLimit}.";
            StatusTextBlock.Text += $"  Last run: {optimizeWatch.Elapsed:mm\\:ss} ({(loadouts.Length / Math.Max(0.001, optimizeWatch.Elapsed.TotalSeconds)):F0}/s), Avg: {_uxMetrics.AverageRunMs / 1000.0:F1}s  workers:{workerCount}";
            RunProgressBar.IsIndeterminate = false;
            RunProgressBar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Optimization canceled.";
            RunProgressBar.IsIndeterminate = false;
            RunProgressBar.Value = 0;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error: {ex.Message}";
            RunProgressBar.IsIndeterminate = false;
            RunProgressBar.Value = 0;
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            SetRunControls(isRunning: false);
        }
    }

    private void CancelOptimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is null)
        {
            return;
        }

        CancelOptimizeButton.IsEnabled = false;
        CancelFromResultsButton.IsEnabled = false;
        StatusTextBlock.Text = "Cancel requested...";
        _runCancellation.Cancel();
    }

    private void RecalculateTop50Button_OnClick(object sender, RoutedEventArgs e)
    {
        if (_lastRunResults.Count == 0)
        {
            StatusTextBlock.Text = "No results available yet. Run optimization first.";
            return;
        }

        var objective = ParseEnumFromCombo<OptimizationObjective>(ResultsObjectiveComboBox, OptimizationObjective.MaxScore);
        ObjectiveComboBox.SelectedIndex = ResultsObjectiveComboBox.SelectedIndex;
        RebuildResultsRows(objective);
        StatusTextBlock.Text = $"Recalculated top {_lastResultLimit} for objective '{objective}' from latest run.";
    }

    private void RebuildResultsRows(OptimizationObjective objective)
    {
        var top = _lastRunResults
            .OrderBy(r => ObjectiveRanking.RankKey(r, objective).Primary)
            .ThenBy(r => ObjectiveRanking.RankKey(r, objective).Secondary)
            .Take(Math.Max(1, _lastResultLimit))
            .ToArray();

        _resultRows.Clear();
        int rank = 1;
        foreach (var r in top)
        {
            var grouped = ResultPresentation.GroupedHandPercentages(r);
            _resultRows.Add(
                new ResultRow
                {
                    Rank = rank++,
                    Loadout = CompactLoadoutText(r.Counts),
                    EvTurns = $"{r.Metrics.EvTurns:F2}",
                    EvPoints = $"{r.Metrics.EvPoints:F2}",
                    P50 = $"{r.Metrics.P50Turns:F0}",
                    P90 = $"{r.Metrics.P90Turns:F0}",
                    OneOk = $"{grouped["1_ok"]}%",
                    ThreeOk = $"{grouped["3_ok"]}%",
                    FourOk = $"{grouped["4_ok"]}%",
                    FiveOk = $"{grouped["5_ok"]}%",
                    SixOk = $"{grouped["6_ok"]}%",
                    FiveStraight = $"{grouped["5_s"]}%",
                    SixStraight = $"{grouped["6_s"]}%",
                });
        }

        SaveLastResultsState();
    }

    private void SetRunControls(bool isRunning)
    {
        OptimizeButton.IsEnabled = !isRunning;
        OptimizeFromDiceButton.IsEnabled = !isRunning;
        RunAgainFromResultsButton.IsEnabled = !isRunning;
        CancelOptimizeButton.IsEnabled = isRunning;
        CancelFromResultsButton.IsEnabled = isRunning;
    }

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            if (OptimizeButton.IsEnabled)
            {
                OptimizeButton_OnClick(sender, new RoutedEventArgs());
            }
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            MainTabControl.SelectedItem = DiceTab;
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T)
        {
            RecalculateTop50Button_OnClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            MainTabControl.SelectedItem = DiceTab;
            DiceSearchTextBox.Focus();
            DiceSearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _runCancellation is not null)
        {
            CancelOptimizeButton_OnClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ShowAdvancedToggleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        SetAdvancedVisibility(true);
    }

    private void ShowAdvancedToggleButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        SetAdvancedVisibility(false);
    }

    private void SetAdvancedVisibility(bool visible)
    {
        _isAdvancedVisible = visible;

        OptimizeTab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        EfficiencyTab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && (MainTabControl.SelectedItem == OptimizeTab || MainTabControl.SelectedItem == EfficiencyTab))
        {
            MainTabControl.SelectedItem = DiceTab;
        }
    }

    private ImmutableArray<EfficiencyStage> BuildEfficiencyPlan()
    {
        if (_efficiencyRows.Count == 0)
        {
            LoadDefaultEfficiencyPlan();
        }

        return _efficiencyRows
            .Select(row =>
            {
                int pilotTurns = Math.Max(1, row.PilotTurns);

                return new EfficiencyStage(
                    MinTotal: Math.Max(0, row.MinTotal),
                    PilotTurns: pilotTurns,
                    KeepPercent: row.KeepPercent,
                    Epsilon: Math.Max(0.0, row.Epsilon),
                    MinSurvivors: Math.Max(1, row.MinSurvivors));
            })
            .ToImmutableArray();
    }

    private void LoadDefaultEfficiencyPlan()
    {
        _efficiencyRows.Clear();
        _efficiencyRows.Add(new EfficiencyStageRow { MinTotal = 100000, PilotTurns = 100, KeepPercent = 30.0, Epsilon = 0.10, MinSurvivors = 100 });
        _efficiencyRows.Add(new EfficiencyStageRow { MinTotal = 10000, PilotTurns = 500, KeepPercent = 10.0, Epsilon = 0.05, MinSurvivors = 100 });
        _efficiencyRows.Add(new EfficiencyStageRow { MinTotal = 1000, PilotTurns = 1000, KeepPercent = 10.0, Epsilon = 0.0, MinSurvivors = 100 });
        _efficiencyRows.Add(new EfficiencyStageRow { MinTotal = 0, PilotTurns = 50000, KeepPercent = 100.0, Epsilon = 0.0, MinSurvivors = 100 });
    }

    private void CommitGridEdits()
    {
        OwnedDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        OwnedDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        EfficiencyDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        EfficiencyDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void AddStageButton_OnClick(object sender, RoutedEventArgs e)
    {
        int pilot = _efficiencyRows.Count == 0 ? 1000 : _efficiencyRows[^1].PilotTurns + 1000;
        _efficiencyRows.Add(new EfficiencyStageRow
        {
            MinTotal = 0,
            PilotTurns = pilot,
            KeepPercent = 100.0,
            Epsilon = 0.0,
            MinSurvivors = 1,
        });
    }

    private void RemoveStageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (EfficiencyDataGrid.SelectedItem is EfficiencyStageRow row)
        {
            _efficiencyRows.Remove(row);
        }
    }

    private void ResetStagesButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoadDefaultEfficiencyPlan();
        PresetStatusTextBlock.Text = "Preset: Custom";
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _runCancellation?.Cancel();
        CommitGridEdits();
        SaveOwnedDiceState();
        SaveUiState();
        SaveLastResultsState();
        _cacheStore.Shutdown(TimeSpan.FromMilliseconds(350));
    }

    private void LoadOwnedDiceState()
    {
        if (!File.Exists(_ownedDiceStatePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(_ownedDiceStatePath);
            var saved = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (saved is null)
            {
                return;
            }

            foreach (var row in _ownedRows)
            {
                if (saved.TryGetValue(row.Name, out int owned))
                {
                    row.OwnedCount = Math.Max(0, owned);
                }
            }

            CaptureSavedOwnedCounts();
            OwnedDataGrid.Items.Refresh();
        }
        catch
        {
            // Ignore invalid saved state and continue with defaults.
        }
    }

    private void SaveOwnedDiceState()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_ownedDiceStatePath)!);
        var map = _ownedRows.ToDictionary(static row => row.Name, static row => Math.Max(0, row.OwnedCount), StringComparer.Ordinal);
        string json = JsonSerializer.Serialize(map);
        File.WriteAllText(_ownedDiceStatePath, json);
        CaptureSavedOwnedCounts();
    }

    private void LoadUiState()
    {
        if (!File.Exists(_uiStatePath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(_uiStatePath));
            if (state is null)
            {
                return;
            }

            TargetScoreSlider.Value = Math.Clamp(state.TargetScore, 1500, 5000);
            TurnsTextBox.Text = state.Turns.ToString();
            EfficiencySeedTextBox.Text = state.EfficiencySeed.ToString();
            EfficiencyCheckBox.IsChecked = state.EfficiencyEnabled;
            RiskProfileComboBox.SelectedIndex = Math.Clamp(state.RiskProfileIndex, 0, 2);
            ObjectiveComboBox.SelectedIndex = Math.Clamp(state.ObjectiveIndex, 0, ObjectiveComboBox.Items.Count - 1);
            ResultsObjectiveComboBox.SelectedIndex = Math.Clamp(state.ResultsObjectiveIndex, 0, ResultsObjectiveComboBox.Items.Count - 1);
            PerformanceComboBox.SelectedIndex = Math.Clamp(state.PerformanceIndex, 0, PerformanceComboBox.Items.Count - 1);

            if (state.EfficiencyStages.Length > 0)
            {
                _efficiencyRows.Clear();
                foreach (var row in state.EfficiencyStages)
                {
                    _efficiencyRows.Add(new EfficiencyStageRow
                    {
                        MinTotal = Math.Max(0, row.MinTotal),
                        PilotTurns = Math.Max(1, row.PilotTurns),
                        KeepPercent = Math.Clamp(row.KeepPercent, 0.001, 100.0),
                        Epsilon = Math.Max(0.0, row.Epsilon),
                        MinSurvivors = Math.Max(1, row.MinSurvivors),
                    });
                }
            }

            ShowAdvancedToggleButton.IsChecked = state.AdvancedVisible;
            RunSetupToggleButton.IsChecked = state.RunSetupExpanded;
            PresetStatusTextBlock.Text = "Preset: Custom";
            ApplySavedWindowState(state);
        }
        catch
        {
            // Ignore invalid saved settings and keep defaults.
        }
    }

    private void SaveUiState()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_uiStatePath)!);
        var state = new UiState
        {
            TargetScore = (int)Math.Round(TargetScoreSlider.Value),
            Turns = ParseInt(TurnsTextBox.Text, RecommendedTurns, min: 1),
            EfficiencySeed = ParseInt(EfficiencySeedTextBox.Text, RecommendedEfficiencySeed, min: 1),
            EfficiencyEnabled = EfficiencyCheckBox.IsChecked == true,
            RiskProfileIndex = RiskProfileComboBox.SelectedIndex,
            ObjectiveIndex = ObjectiveComboBox.SelectedIndex,
            ResultsObjectiveIndex = ResultsObjectiveComboBox.SelectedIndex,
            PerformanceIndex = PerformanceComboBox.SelectedIndex,
            AdvancedVisible = _isAdvancedVisible,
            RunSetupExpanded = RunSetupToggleButton.IsChecked != false,
            EfficiencyStages = _efficiencyRows
                .Select(static row => new UiEfficiencyStage
                {
                    MinTotal = row.MinTotal,
                    PilotTurns = row.PilotTurns,
                    KeepPercent = row.KeepPercent,
                    Epsilon = row.Epsilon,
                    MinSurvivors = row.MinSurvivors,
                })
                .ToArray(),
        };
        CaptureWindowState(state);

        File.WriteAllText(_uiStatePath, JsonSerializer.Serialize(state));
    }

    private void ApplySavedWindowState(UiState state)
    {
        if (!IsFinitePositive(state.WindowWidth) || !IsFinitePositive(state.WindowHeight))
        {
            return;
        }

        if (!IsFinite(state.WindowLeft) || !IsFinite(state.WindowTop))
        {
            return;
        }

        double width = Math.Max(MinWidth, state.WindowWidth!.Value);
        double height = Math.Max(MinHeight, state.WindowHeight!.Value);
        double left = state.WindowLeft!.Value;
        double top = state.WindowTop!.Value;

        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        if (width > SystemParameters.VirtualScreenWidth)
        {
            width = SystemParameters.VirtualScreenWidth;
        }

        if (height > SystemParameters.VirtualScreenHeight)
        {
            height = SystemParameters.VirtualScreenHeight;
        }

        left = Math.Clamp(left, virtualLeft, Math.Max(virtualLeft, virtualRight - width));
        top = Math.Clamp(top, virtualTop, Math.Max(virtualTop, virtualBottom - height));

        Width = width;
        Height = height;
        Left = left;
        Top = top;

        if (state.WindowIsMaximized == true)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void CaptureWindowState(UiState state)
    {
        bool isMaximized = WindowState == WindowState.Maximized;
        Rect bounds = isMaximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);

        if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) || !IsFinitePositive(bounds.Width) || !IsFinitePositive(bounds.Height))
        {
            return;
        }

        state.WindowLeft = bounds.Left;
        state.WindowTop = bounds.Top;
        state.WindowWidth = bounds.Width;
        state.WindowHeight = bounds.Height;
        state.WindowIsMaximized = isMaximized;
    }

    private static bool IsFinite(double? value)
    {
        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
    }

    private static bool IsFinitePositive(double? value)
    {
        return IsFinite(value) && value!.Value > 0.0;
    }

    private void LoadUxMetrics()
    {
        if (!File.Exists(_uxMetricsPath))
        {
            _uxMetrics = new UxMetricsState();
            return;
        }

        try
        {
            _uxMetrics = JsonSerializer.Deserialize<UxMetricsState>(File.ReadAllText(_uxMetricsPath)) ?? new UxMetricsState();
        }
        catch
        {
            _uxMetrics = new UxMetricsState();
        }
    }

    private void RecordUxMetricRun(double elapsedMs, int loadouts)
    {
        _uxMetrics.RunCount++;
        _uxMetrics.TotalRunMs += elapsedMs;
        _uxMetrics.LastRunMs = elapsedMs;
        _uxMetrics.LastLoadouts = loadouts;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_uxMetricsPath)!);
        File.WriteAllText(_uxMetricsPath, JsonSerializer.Serialize(_uxMetrics));
    }

    private void LoadLastResultsState()
    {
        if (!File.Exists(_lastResultsStatePath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<LastResultsState>(File.ReadAllText(_lastResultsStatePath));
            if (state is null || state.Results.Length == 0)
            {
                return;
            }

            _lastRunResults = state.Results.Select(ToSimulationResult).ToArray();
            _lastResultLimit = Math.Max(1, state.ResultLimit);

            int selectedObjective = Math.Clamp(
                state.ObjectiveIndex,
                0,
                Math.Max(0, ResultsObjectiveComboBox.Items.Count - 1));
            ResultsObjectiveComboBox.SelectedIndex = selectedObjective;
            ObjectiveComboBox.SelectedIndex = selectedObjective;

            var objective = ParseEnumFromCombo<OptimizationObjective>(ResultsObjectiveComboBox, OptimizationObjective.MaxScore);
            RebuildResultsRows(objective);
            StatusTextBlock.Text = $"Loaded {_resultRows.Count} cached result rows from last run.";
        }
        catch
        {
            // Ignore invalid saved results state.
        }
    }

    private void SaveLastResultsState()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_lastResultsStatePath)!);
            var state = new LastResultsState
            {
                ObjectiveIndex = ResultsObjectiveComboBox.SelectedIndex,
                ResultLimit = Math.Max(1, _lastResultLimit),
                Results = _lastRunResults.Select(ToResultState).ToArray(),
            };

            File.WriteAllText(_lastResultsStatePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // Results restore is optional; skip persistence errors.
        }
    }

    private static ResultState ToResultState(SimulationResult result)
    {
        return new ResultState
        {
            Counts = result.Counts.ToArray(),
            EvTurns = result.Metrics.EvTurns,
            EvPoints = result.Metrics.EvPoints,
            P50Turns = result.Metrics.P50Turns,
            P90Turns = result.Metrics.P90Turns,
            EvPointsSe = result.Metrics.EvPointsSe,
            PWithin = result.Metrics.PWithin.ToDictionary(static x => x.Key, static x => x.Value),
            MeanPoints = result.MeanPoints,
            StandardDeviation = result.StandardDeviation,
            TagCounts = result.TagCounts.ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal),
            TotalGroups = result.TotalGroups,
            ScoringTurns = result.ScoringTurns,
        };
    }

    private static SimulationResult ToSimulationResult(ResultState state)
    {
        var metrics = new TurnMetrics(
            EvTurns: state.EvTurns,
            PWithin: state.PWithin.ToImmutableDictionary(),
            EvPoints: state.EvPoints,
            P50Turns: state.P50Turns,
            P90Turns: state.P90Turns,
            EvPointsSe: state.EvPointsSe);

        return new SimulationResult(
            Counts: state.Counts,
            Metrics: metrics,
            MeanPoints: state.MeanPoints,
            StandardDeviation: state.StandardDeviation,
            TagCounts: state.TagCounts,
            TotalGroups: state.TotalGroups,
            ScoringTurns: state.ScoringTurns);
    }

    private void CaptureSavedOwnedCounts()
    {
        _savedOwnedCounts.Clear();
        foreach (var row in _ownedRows)
        {
            _savedOwnedCounts[row.Name] = Math.Max(0, row.OwnedCount);
        }
    }

    private bool OwnedRowFilter(object item)
    {
        if (item is not OwnedDieRow row)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_diceFilterText))
        {
            return true;
        }

        return row.Name.Contains(_diceFilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void DiceSearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _diceFilterText = DiceSearchTextBox.Text?.Trim() ?? string.Empty;
        _ownedView.Refresh();
    }

    private void IncrementOwnedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OwnedDieRow row })
        {
            row.OwnedCount = Math.Max(0, row.OwnedCount + 1);
            OwnedDataGrid.Items.Refresh();
        }
    }

    private void DecrementOwnedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OwnedDieRow row })
        {
            row.OwnedCount = Math.Max(0, row.OwnedCount - 1);
            OwnedDataGrid.Items.Refresh();
        }
    }

    private void ClearAllDiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        var decision = MessageBox.Show(
            "Clear all owned dice counts?",
            "Clear Owned Dice",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (decision != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var row in _ownedRows)
        {
            row.OwnedCount = 0;
        }

        OwnedDataGrid.Items.Refresh();
        StatusTextBlock.Text = "Cleared all owned dice counts.";
    }

    private void ResetDiceToSavedButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _ownedRows)
        {
            row.OwnedCount = _savedOwnedCounts.TryGetValue(row.Name, out int saved) ? Math.Max(0, saved) : 0;
        }

        OwnedDataGrid.Items.Refresh();
        StatusTextBlock.Text = "Restored owned dice from saved state.";
    }

    private void ResultsObjectiveComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_lastRunResults.Count == 0)
        {
            return;
        }

        var objective = ParseEnumFromCombo<OptimizationObjective>(ResultsObjectiveComboBox, OptimizationObjective.MaxScore);
        ObjectiveComboBox.SelectedIndex = ResultsObjectiveComboBox.SelectedIndex;
        RebuildResultsRows(objective);
        StatusTextBlock.Text = $"Updated ranking objective to '{objective}'.";
    }

    private static int ParseInt(string? text, int fallback, int min)
    {
        if (!int.TryParse(text, out int value))
        {
            value = fallback;
        }

        return Math.Max(min, value);
    }

    private void UseRecommendedDefaultsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyRecommendedDefaults(updateStatus: true);
    }

    private void ResetSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all optimization settings to recommended defaults?",
            "Reset Settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        ApplyRecommendedDefaults(updateStatus: true);
    }

    private void ClearCacheButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Clear all cached simulation results now?\n\nThis does not change owned dice or settings.",
            "Clear Cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _cacheStore.ClearAll();
        StatusTextBlock.Text = "Cache cleared.";
    }

    private void ApplyRecommendedDefaults(bool updateStatus)
    {
        TargetScoreSlider.Value = RecommendedTargetScore;
        TurnsTextBox.Text = RecommendedTurns.ToString();
        EfficiencySeedTextBox.Text = RecommendedEfficiencySeed.ToString();
        EfficiencyCheckBox.IsChecked = true;
        RiskProfileComboBox.SelectedIndex = 1;
        ObjectiveComboBox.SelectedIndex = 0;
        ResultsObjectiveComboBox.SelectedIndex = 0;
        PerformanceComboBox.SelectedIndex = 2;
        ShowAdvancedToggleButton.IsChecked = false;
        RunSetupToggleButton.IsChecked = true;
        LoadDefaultEfficiencyPlan();
        PresetStatusTextBlock.Text = "Preset: Recommended Defaults";

        if (updateStatus)
        {
            StatusTextBlock.Text = "Applied recommended defaults.";
        }
    }

    private static TEnum ParseEnumFromCombo<TEnum>(ComboBox comboBox, TEnum fallback) where TEnum : struct
    {
        if (comboBox.SelectedItem is ComboBoxItem item &&
            item.Content is string content &&
            Enum.TryParse<TEnum>(content, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static int ResolveWorkerCount(PerformanceProfile profile, int logicalCores)
    {
        int cores = Math.Max(1, logicalCores);
        int workers = profile switch
        {
            PerformanceProfile.Low => (int)Math.Floor(cores * 0.25),
            PerformanceProfile.Medium => (int)Math.Floor(cores * 0.50),
            _ => Math.Max(4, (int)Math.Floor(cores * 0.50)),
        };

        workers = Math.Clamp(workers, 1, cores);
        return workers;
    }

    private int[] BuildAvailableCountsForOptimization()
    {
        int count = Math.Min(_ownedRows.Count, _diceCatalog.Count);
        var available = new int[count];
        for (int i = 0; i < count; i++)
        {
            var die = _diceCatalog[i];
            if (string.Equals(die.Name, OrdinaryDieName, StringComparison.Ordinal))
            {
                available[i] = 6;
                continue;
            }

            if (IsUniformFaceDie(die))
            {
                available[i] = 0;
                continue;
            }

            available[i] = Math.Max(0, _ownedRows[i].OwnedCount);
        }

        return available;
    }

    private static bool IsUniformFaceDie(DieType die)
    {
        const double tolerance = 1e-12;
        double first = die.Probabilities[1];
        for (int face = 2; face <= 6; face++)
        {
            if (Math.Abs(die.Probabilities[face] - first) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private string CompactLoadoutText(IReadOnlyList<int> counts)
    {
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(counts.Count, _diceCatalog.Count); i++)
        {
            int count = counts[i];
            if (count <= 0)
            {
                continue;
            }

            string name = _diceCatalog[i].Name;
            parts.Add(count == 1 ? name : $"{name} x{count}");
        }

        if (parts.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", parts);
    }

    private static string ResolveRuntimeRoot()
    {
        return AppContext.BaseDirectory;
    }

    public sealed class OwnedDieRow
    {
        public string Name { get; init; } = string.Empty;
        public int OwnedCount { get; set; }
        public string P1 { get; init; } = string.Empty;
        public string P2 { get; init; } = string.Empty;
        public string P3 { get; init; } = string.Empty;
        public string P4 { get; init; } = string.Empty;
        public string P5 { get; init; } = string.Empty;
        public string P6 { get; init; } = string.Empty;
    }

    public sealed class EfficiencyStageRow
    {
        public int MinTotal { get; set; }
        public int PilotTurns { get; set; }
        public double KeepPercent { get; set; }
        public double Epsilon { get; set; }
        public int MinSurvivors { get; set; }
    }

    public sealed class ResultRow
    {
        public int Rank { get; init; }
        public string Loadout { get; init; } = string.Empty;
        public string EvTurns { get; init; } = string.Empty;
        public string EvPoints { get; init; } = string.Empty;
        public string P50 { get; init; } = string.Empty;
        public string P90 { get; init; } = string.Empty;
        public string OneOk { get; init; } = string.Empty;
        public string ThreeOk { get; init; } = string.Empty;
        public string FourOk { get; init; } = string.Empty;
        public string FiveOk { get; init; } = string.Empty;
        public string SixOk { get; init; } = string.Empty;
        public string FiveStraight { get; init; } = string.Empty;
        public string SixStraight { get; init; } = string.Empty;
    }

    private sealed class UiState
    {
        public int TargetScore { get; set; }
        public int Turns { get; set; }
        public int EfficiencySeed { get; set; }
        public bool EfficiencyEnabled { get; set; }
        public int RiskProfileIndex { get; set; }
        public int ObjectiveIndex { get; set; }
        public int ResultsObjectiveIndex { get; set; }
        public int PerformanceIndex { get; set; }
        public bool AdvancedVisible { get; set; }
        public bool RunSetupExpanded { get; set; } = true;
        public UiEfficiencyStage[] EfficiencyStages { get; set; } = [];
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool? WindowIsMaximized { get; set; }
    }

    private sealed class UiEfficiencyStage
    {
        public int MinTotal { get; set; }
        public int PilotTurns { get; set; }
        public double KeepPercent { get; set; }
        public double Epsilon { get; set; }
        public int MinSurvivors { get; set; }
    }

    private sealed class UxMetricsState
    {
        public int RunCount { get; set; }
        public double TotalRunMs { get; set; }
        public double LastRunMs { get; set; }
        public int LastLoadouts { get; set; }

        public double AverageRunMs => RunCount == 0 ? 0.0 : TotalRunMs / RunCount;
    }

    private sealed class LastResultsState
    {
        public int ObjectiveIndex { get; set; }
        public int ResultLimit { get; set; } = 50;
        public ResultState[] Results { get; set; } = [];
    }

    private sealed class ResultState
    {
        public int[] Counts { get; set; } = [];
        public double EvTurns { get; set; }
        public double EvPoints { get; set; }
        public double P50Turns { get; set; }
        public double P90Turns { get; set; }
        public double EvPointsSe { get; set; }
        public Dictionary<int, double> PWithin { get; set; } = [];
        public double MeanPoints { get; set; }
        public double StandardDeviation { get; set; }
        public Dictionary<string, int> TagCounts { get; set; } = new(StringComparer.Ordinal);
        public int TotalGroups { get; set; }
        public int ScoringTurns { get; set; }
    }

    private enum PerformanceProfile
    {
        Low,
        Medium,
        High,
    }
}
