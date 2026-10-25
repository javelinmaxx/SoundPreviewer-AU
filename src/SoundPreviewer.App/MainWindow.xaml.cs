using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly LibraryStore _store;
    private readonly WavePlayer _player = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _taskTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _taskWatch = new();
    private Action? _flushTaskProgress;
    private AppSettings _settings;
    private CancellationTokenSource? _taskCancellation;
    private bool _isBusy;
    private bool _stopRequested;
    private bool _closeWhenDone;
    private bool _isClosing;
    private bool _isPlaying;
    private bool _seeking;
    private bool _mediaOpened;
    private bool _playWhenOpen;
    private double _openPosition;
    private double? _segmentEnd;
    private int _selectionGeneration;
    private string _loadedPreviewPath = "";
    private PresetRow? _selected;
    private string _statusMessage = "就绪";
    private double _progressValue;
    private bool _hasTaskFeedback;
    private bool _progressIndeterminate;
    private bool _needsRendererSetup;
    private string _taskTitle = "";
    private string _taskProgressText = "";
    private string _taskMemoryText = "";
    private string _taskSelectionText = "";
    private bool _updatingFilterChoices;
    private PresetDuplicateView _duplicateView = new([]);
    private const string MissingFilter = "\0";
    private readonly PresetCategoryService _categoryService = new();
    private string _categoryRulesStatus = "";
    private string _loadedCategoryRules = "";
    private int _baselineRefreshGeneration;

    public ObservableCollection<PresetRow> Presets { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> RecentLogs { get; } = [];
    public ObservableCollection<SourceFolder> Sources { get; } = [];
    public ObservableCollection<PluginConfiguration> Plugins { get; } = [];
    public ObservableCollection<string> PluginDirectories { get; } = [];
    public ObservableCollection<DiscoveredPluginRow> DiscoveredPlugins { get; } = [];
    public DataGridCollectionView PresetsView { get; }
    public ObservableCollection<InitializationRow> InitBaselines { get; } = [];
    public string CategoryRulesStatus => _categoryRulesStatus;
    public List<Choice<SynthKind>> SynthChoices { get; } = Enum.GetValues<SynthKind>().Where(s => s != SynthKind.Unknown).Select(s => new Choice<SynthKind>(Labels.Synth(s), s)).ToList();
    public List<Choice<SynthKind?>> SourceChoices { get; } = [new("自动识别（混合音色包）", null), .. Enum.GetValues<SynthKind>().Where(s => s != SynthKind.Unknown).Select(s => new Choice<SynthKind?>(Labels.Synth(s), s))];
    public List<Choice<RenderBackend>> BackendChoices { get; } = [new("未选择生成方式", RenderBackend.Unconfigured), new("Vital 原生", RenderBackend.VitalCli), new("外部工作进程", RenderBackend.ExternalWorker), new("DawDreamer", RenderBackend.DawDreamer), new("Sushi（实验）", RenderBackend.Sushi)];
    public List<Choice<int>> ParallelRenderChoices { get; } = [new("自动（按 CPU 与内存）", 0), new("1 条（串行）", 1), new("2 条", 2), new("4 条", 4), new("8 条", 8), new("16 条", 16)];
    public List<Choice<int>> MemoryBudgetChoices { get; } = Enumerable.Range(RenderResourcePolicy.MinimumMemoryBudgetGiB,
        RenderResourcePolicy.MaximumMemoryBudgetGiB - RenderResourcePolicy.MinimumMemoryBudgetGiB + 1)
        .Select(value => new Choice<int>(value == RenderResourcePolicy.DefaultMemoryBudgetGiB ? $"{value} GiB（默认）" : $"{value} GiB", value)).ToList();
    public ObservableCollection<CompatibilityRow> Compatibility { get; } = [];
    public PresetRow? Selected => _selected;
    public bool IsBusy => _isBusy;
    public bool IsIdle => !_isBusy && !_storageRelocated;
    public bool CanStopTask => _isBusy && !_stopRequested && (!_isStorageMaintenance || _isCompressingPreviews);
    public bool HasSelection => _selected is not null;
    public bool CanEditSelection => HasSelection && IsIdle;
    public bool CanAssignSynth => CanEditSelection && _selected?.Record.Status != PresetStatus.Missing;
    public bool CanPlay => !_isClosing && !_isStorageMaintenance && !_storageRelocated && _selected is not null && File.Exists(_selected.Record.PreviewPath);
    public bool CanConfirm => IsIdle && CanPlay && _selected?.Record.Status == PresetStatus.NeedsReview;
    public bool CanRenderSelection => IsIdle && _selected is not null && _duplicateView.ResolveCanonical(_selected.Record) is var record &&
        record.DuplicateOfId.Length == 0 && record.Synth != SynthKind.Unknown && record.Status is not (PresetStatus.Unsupported or PresetStatus.Missing or PresetStatus.Unidentified);
    public bool CanViewCanonical => _selected is not null && Presets.Any(p => p.Record.Id == _selected.Record.DuplicateOfId);
    public bool CanonicalVisibility => CanViewCanonical;
    public bool EmptyVisibility => PresetsView.IsEmpty;
    public bool EmptyLibraryVisibility => Presets.Count == 0;
    public bool EmptyFilterVisibility => Presets.Count > 0;
    public string EmptyMessage => Presets.Count == 0 ? "暂无音色" : "当前筛选没有匹配音色";
    public int GeneratedPreviewCount => Presets.Count(p => p.Record.DuplicateOfId.Length == 0 && p.HasPreview);
    public int ReviewPreviewCount => Presets.Count(p => p.Record.DuplicateOfId.Length == 0 && p.HasPreview && p.Record.Status == PresetStatus.NeedsReview);
    public bool GeneratedResultsVisibility => GeneratedPreviewCount > 0;
    public string LibraryLocation => Path.GetFullPath(App.DataDirectory);
    public string AppVersion => "v" + typeof(App).Assembly.GetName().Version!.ToString(3);
    public string BuildLocation => $"程序：{AppContext.BaseDirectory}{Environment.NewLine}音色库：{LibraryLocation}";
    private static string X86HostPath => Path.Combine(AppContext.BaseDirectory, "Vst2Host-x86", "SoundPreviewer.Vst2Host.exe");
    private static string SushiHostPath => Path.Combine(AppContext.BaseDirectory, "Renderers", "Sushi", "sushi.exe");
    public string NowPlaying => _selected?.Name ?? "未选择预览";
    public int VisiblePresetCount => PresetsView.Count;
    public bool CanRenderFiltered => IsIdle && !PresetsView.IsEmpty;
    public bool CanRetryTasks => IsIdle && Presets.Any(row => row.Record.DuplicateOfId.Length == 0 &&
        (row.Record.Status == PresetStatus.Failed || row.Record.LastRenderOutcome == "失败"));
    public string LibrarySummary => $"筛选 {VisiblePresetCount} / {Presets.Count} · 已生成 {GeneratedPreviewCount}（待确认 {ReviewPreviewCount}） · {Presets.Count(p => p.Record.DuplicateOfId.Length > 0)} 个重复副本";
    public string StatusMessage => _statusMessage;
    public double ProgressValue => _progressValue;
    public bool TaskStatusVisibility => _hasTaskFeedback;
    public bool TaskSettingsVisibility => _needsRendererSetup && IsIdle;
    public bool TaskBandVisible => _storageRelocated || (_hasTaskFeedback && (SettingsTab?.IsSelected != true || IsBusy));
    public bool RelocatedLibraryVisibility => _storageRelocated;
    public string TaskTitle => _taskTitle.Length > 0 ? _taskTitle : "生成进度";
    public string TaskProgressText => _taskProgressText.Length > 0 ? _taskProgressText : "尚未开始生成 · 已有预览可直接试听";
    public string TaskMemoryText => _taskMemoryText;
    public string TaskSelectionText => _taskSelectionText;
    public bool TaskMemoryVisible => _taskMemoryText.Length > 0;
    public string TaskElapsed => $"用时 {(_taskWatch.Elapsed.TotalHours >= 1 ? _taskWatch.Elapsed.ToString(@"h\:mm\:ss") : _taskWatch.Elapsed.ToString(@"m\:ss"))}";
    public bool IsProgressIndeterminate => IsBusy && _progressIndeterminate;
    public new event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        _store = new LibraryStore(Path.Combine(App.DataDirectory, "library.db"));
        _store.Initialize();
        _settings = App.ImportedSettings ?? _store.LoadSettings();
        InitializeMetadataCatalogs();
        ApplyLanguage();
        _settings.Appearance = ThemeManager.Normalize(_settings.Appearance);
        ThemeManager.Apply(_settings.Appearance);
        _directoryConfiguration = new DirectoryConfigurationService(App.SmokeTest && !App.TestInstalledStorage
            ? App.DataDirectory : ApplicationPaths.PortableApplicationDirectory);
        bool createdDirectoryConfiguration = _directoryConfiguration.LoadOrCreate(_settings);
        var previousPreviewDirectory = _settings.PreviewDirectory;
        try { _settings.PreviewDirectory = ValidateOutputPath(previousPreviewDirectory); }
        catch (ArgumentException) { _settings.PreviewDirectory = App.DataDirectory; }
        if (!App.SmokeTest || App.TestInstalledStorage) _settings.PreviewDirectory = App.DataDirectory;
        bool migratedPreviewDirectory = !string.Equals(previousPreviewDirectory, _settings.PreviewDirectory, StringComparison.Ordinal);
        _settings.Template.NoteOrder = PreviewNoteOrder.MiddleHighLow;
        int refreshedHost = PluginSelectionService.RefreshBundledHost(_settings.Plugins, X86HostPath, discovered: _settings.DiscoveredPlugins);
        int refreshedSushi = PluginSelectionService.RefreshBundledSushiHost(_settings.Plugins, SushiHostPath);
        int localPython = PluginSelectionService.RefreshLocalDawDreamer(_settings.Plugins, App.DataDirectory);
        bool seededPluginDirectories = createdDirectoryConfiguration && _settings.PluginDirectories.Count == 0;
        if (seededPluginDirectories) _settings.PluginDirectories = ResolvePluginScanDirectories([], _settings.Plugins);
        if (seededPluginDirectories) _directoryConfiguration.Save(_settings);
        _store.SaveSettings(_settings);
        PresetsView = new DataGridCollectionView(Presets);
        PresetsView.Filter = FilterPreset;
        InitializeComponent();
        InitializePreviewShortcut();
        Title += " (" + AppVersion + ")";
        DataContext = this;
        Pages.SelectionChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TaskBandVisible)));
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => _seeking = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, Seek_Released, RoutingStrategies.Tunnel, handledEventsToo: true);
        VolumeSlider.PropertyChanged += Volume_Changed;
        PopulateSettings();
        PopulateLanguageChoices();
        foreach (string message in App.StorageStartupMessages) Log(message);
        Log("目录配置已读取：" + DirectoryConfigurationLocation);
        if (seededPluginDirectories) Log("已添加系统和用户的默认音源安装目录，可直接点击“扫描音源”。");
        if (migratedPreviewDirectory) Log("预览保存目录：" + _settings.PreviewDirectory);
        if (refreshedHost > 0) Log("已更新内置 32 位 Sylenth1 宿主路径，保留原有插件与音色库。");
        if (refreshedSushi > 0) Log("已定位自行放置的 Sushi 宿主，保留原有音源关联。");
        if (localPython > 0) Log("已填入本库准备好的 Python 宿主，实际音源加载仍需检查。");
        LoadCategoryRulesEditor();
        _player.Volume = _settings.PlaybackVolume;
        _playbackTimer.Tick += (_, _) => UpdatePlayback();
        _playbackTimer.Start();
        _taskTimer.Tick += (_, _) =>
        {
            if (_isClosing) return;
            _flushTaskProgress?.Invoke();
            NotifyTaskProgress();
        };
    }

    private async void Window_Loaded(object? sender, EventArgs e)
    {
        try
        {
            // The desktop may constrain the initial window to a smaller working area.
            Width = ClientSize.Width;
            Height = ClientSize.Height;
            RefreshClientLayout();
            var restored = await Task.Run(() => _store.RecoverInterrupted(preserveMissingPreviewMappings: true));
            var records = await Task.Run(() =>
            {
                DuplicatePreviewService.Synchronize(_store);
                return _store.LoadPresets();
            });
            if (_isClosing) return;
            if (_categoryService.Load().Success)
            {
                var classified = await Task.Run(() => _categoryService.Apply(records));
                if (classified.Changed > 0) await Task.Run(() => _store.UpsertMany(records));
            }
            SetPresets(records);
            App.LibraryOpened();
            await RefreshInitBaselinesAsync();
            if (_isClosing) return;
            if (GeneratedPreviewCount > 0)
            {
                _hasTaskFeedback = true;
                _taskTitle = $"已保存 {GeneratedPreviewCount} 个预览";
                _statusMessage = $"{ReviewPreviewCount} 项待试听确认。";
                _progressValue = 100;
                NotifyAll();
            }
            Log($"打开本地音色库，恢复 {restored} 个未完成任务。");
            if (App.ScanOnStart) await Scan();
            if (App.SmokeTest) await RunSmokeTestAsync();
        }
        catch (Exception ex) { ShowError(ex); if (App.SmokeTest) App.Shutdown(1); }
    }

    private static string SourceCountLabel(string path, PresetSourceCounts counts) =>
        $"{path}（{counts.Total} 个音色，去重 {counts.Unique} 个" +
        (counts.DuplicateCopies > 0 ? $"，重复副本 {counts.DuplicateCopies} 个" : "") +
        (counts.CrossSourceMatches > 0 ? $"，跨来源相同 {counts.CrossSourceMatches} 个" : "") + "）";

    private void SetPresets(IEnumerable<PresetRecord> records)
    {
        var snapshot = records.ToList();
        SynchronizeMetadata(snapshot);
        var selectedId = _selected?.Record.Id;
        Presets.Clear();
        foreach (var record in snapshot) Presets.Add(new PresetRow(record, _categories, _tags, () => MetadataLanguage));
        RefreshFilterChoices();
        PresetGrid.SelectedItem = Presets.FirstOrDefault(p => p.Record.Id == selectedId);
        RefreshCompatibility();
        NotifyAll();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();
    private async Task Scan() => await RunTask(async token =>
    {
        if (Sources.Count == 0) { Pages.SelectedItem = SettingsTab; SettingsPages.SelectedItem = DirectorySettingsTab; throw new InvalidOperationException("请先添加预制目录。"); }
        _statusMessage = "正在扫描预制目录…";
        NotifyAll();
        var scanWatch = Stopwatch.StartNew();
        var result = await new ScanService(_categoryService).ScanAsync(Sources.ToList(), Presets.Select(p => p.Record).ToList(), token);
        var latestFeedback = Presets.ToDictionary(p => p.Record.Id);
        foreach (var record in result.Presets)
            if (latestFeedback.TryGetValue(record.Id, out var latest))
            {
                record.Favorite = latest.Record.Favorite;
                record.Feedback = latest.Record.Feedback;
                record.FeedbackUtc = latest.Record.FeedbackUtc;
            }
        var storedRecords = await Task.Run(() =>
        {
            _store.UpsertMany(result.Presets);
            DuplicatePreviewService.Synchronize(_store, token);
            return _store.LoadPresets();
        }, token);
        SetPresets(storedRecords);
        foreach (var warning in result.Warnings) Log(warning);
        _taskTitle = "目录扫描完成";
        _statusMessage = $"新增 {result.Added}，内容变更 {result.Changed}，MD5 重复副本 {result.Presets.Count(p => p.DuplicateOfId.Length > 0)}，读取警告 {result.Warnings.Count}，耗时 {scanWatch.Elapsed.TotalSeconds:0.0} 秒。";
        Log(_statusMessage);
    });

    private async void Render_Click(object sender, RoutedEventArgs e) => await GenerateCurrentFilterAsync();
    private async void RenderSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRenderSelection || _selected is null) return;
        await Generate([_duplicateView.ResolveCanonical(_selected.Record)], forceRegenerate: true);
    }
    private async void RenderFiltered_Click(object sender, RoutedEventArgs e) => await GenerateCurrentFilterAsync();

    private async Task GenerateCurrentFilterAsync()
    {
        if (!IsIdle || _isClosing) return;
        // Only visible rows participate; duplicate copies must be explicitly shown first.
        var records = _duplicateView.ResolveCanonicalSelection(PresetsView.Cast<PresetRow>().Select(row => row.Record).ToList());
        var force = ForceGeneration.IsChecked == true;
        ForceGeneration.IsChecked = false;
        // The queue validates existing WAV/M4A previews before reusing them, including stale status records.
        await Generate(records, force, currentFilterOnly: true);
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        var failed = Presets.Where(row => row.Record.DuplicateOfId.Length == 0 &&
            (row.Record.Status == PresetStatus.Failed || row.Record.LastRenderOutcome == "失败")).Select(row => row.Record).ToList();
        if (failed.Count > 0) await Generate(failed, forceRegenerate: true);
    }

    private async Task Generate(IReadOnlyList<PresetRecord> records, bool forceRegenerate = false, bool currentFilterOnly = false) => await RunTask(async token =>
    {
        if (records.Count == 0) throw new InvalidOperationException(currentFilterOnly ? "当前筛选中没有可生成的音色，请调整筛选或先扫描目录。" : "尚无可生成的音色，请先扫描目录。");
        _taskTitle = "正在准备生成预览";
        _statusMessage = "正在检查音源与渲染程序…";
        NotifyTaskProgress();
        Log(currentFilterOnly
            ? forceRegenerate ? "仅覆盖重渲染当前筛选结果，逐项保留旧预览直到新结果完成。" : "仅生成当前筛选中未生成的音色，已有有效预览跳过。"
            : forceRegenerate ? "开始全量重生成，逐项保留旧预览直到新结果完成。" : "继续生成，默认跳过已有完整预览与 MD5 重复副本。");
        if (_selected is not null && records.Any(p => p.Id == _selected.Record.Id))
        {
            StopPlayback();
            _player.Unload();
            _mediaOpened = false;
            _loadedPreviewPath = "";
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var preparation = new Progress<string>(message =>
        {
            if (!_isBusy || _stopRequested || _taskTitle != "正在准备生成预览") return;
            _statusMessage = message;
            NotifyTaskProgress();
        });
        await Task.Run(() => RefreshBinaryHashes(settings, token, preparation), token);
        _settings.Plugins = settings.Plugins;
        await Task.Run(() => _store.SaveSettings(_settings), token);
        var executionSettings = CreateExecutionSettings(settings, out var readiness);
        var rows = Presets.ToDictionary(p => p.Record.Id);
        var changedRows = new ConcurrentDictionary<string, byte>();
        QueueProgress? pendingProgress = null;
        var lastViewRefresh = TimeSpan.Zero;
        var lastLog = TimeSpan.Zero;
        var progress = new QueueReporter(p =>
        {
            if (p.Preset is not null) changedRows.TryAdd(p.Preset.Id, 0);
            Interlocked.Exchange(ref pendingProgress, p);
        });
        _flushTaskProgress = () =>
        {
            var p = Interlocked.Exchange(ref pendingProgress, null);
            if (p is null) return;
            _progressValue = p.Total == 0 ? 0 : p.Completed * 100d / p.Total;
            _progressIndeterminate = p.IsIndeterminate;
            _taskProgressText = FormatRenderProgress(p);
            _taskMemoryText = FormatRenderResources(p);
            _taskSelectionText = (currentFilterOnly ? "当前筛选 · " : "") + (p.Selection?.Description ?? "正在统计本次队列…");
            if (!_stopRequested)
            {
                _taskTitle = p.Stage == "完成" ? "预览任务已结束" : $"正在{p.Stage}";
                _statusMessage = string.IsNullOrWhiteSpace(p.PresetName) ? p.Message : $"{p.PresetName} · {p.Message}";
                if (!string.IsNullOrWhiteSpace(p.ResourceMessage)) _statusMessage += " · " + p.ResourceMessage;
                if (!string.IsNullOrWhiteSpace(p.ConcurrencyMessage) && p.ConcurrencyMessage != p.ResourceMessage)
                    _statusMessage += " · " + p.ConcurrencyMessage;
            }
            foreach (var id in changedRows.Keys)
                if (changedRows.TryRemove(id, out _) && rows.TryGetValue(id, out var row)) row.Refresh();
            if (_taskWatch.Elapsed - lastViewRefresh >= TimeSpan.FromSeconds(1))
            {
                RefreshPresetView();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LibrarySummary)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GeneratedResultsVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyVisibility)));
                lastViewRefresh = _taskWatch.Elapsed;
            }
            if (_taskWatch.Elapsed - lastLog >= TimeSpan.FromSeconds(2))
            {
                Log($"{p.Completed}/{p.Total} · {_statusMessage}");
                lastLog = _taskWatch.Elapsed;
            }
        };
        var workerPath = ApplicationPaths.WorkerPath;
        QueueResult result;
        try
        {
            result = await Task.Run(() => new RenderQueue(_store).RunAsync(executionSettings, records, workerPath, progress, token, forceRegenerate, readiness), token);
        }
        finally
        {
            _flushTaskProgress?.Invoke();
            _flushTaskProgress = null;
            // A replaced WAV can also belong to records outside the requested filter.
            // Reload their committed mappings after success, cancellation or rollback.
            try
            {
                var previousRendered = _selected?.Record.RenderedUtc;
                SetPresets(await Task.Run(() =>
                {
                    DuplicatePreviewService.Synchronize(_store);
                    return _store.LoadPresets();
                }));
                var storedSettings = await Task.Run(_store.LoadSettings);
                foreach (var plugin in _settings.Plugins)
                    plugin.InitBaselinePath = storedSettings.Plugins.FirstOrDefault(saved => saved.Synth == plugin.Synth)?.InitBaselinePath ?? "";
                if (_loadedPreviewPath.Length > 0 && _selected?.Record.RenderedUtc != previousRendered)
                {
                    StopPlayback();
                    _player.Unload();
                    _mediaOpened = false;
                    _loadedPreviewPath = "";
                }
            }
            catch (Exception error) { Log("任务记录未能刷新：" + error.Message); }
        }
        _needsRendererSetup = result.Unconfigured > 0 || result.Unavailable > 0 || result.Failed > 0;
        _taskTitle = result.Failed + result.Unconfigured + result.Unavailable + result.RetryLimited > 0
            ? result.Generated + result.Reused > 0 ? "预览任务结束，部分项目未生成" : "本次没有生成预览"
            : result.Generated > 0 ? "预览生成完成" : result.Reused > 0 ? "已有预览已保留" : "本次没有生成预览";
        var firstBlocked = result.UnavailableRenderers.FirstOrDefault() ?? result.UnconfiguredRenderers.FirstOrDefault();
        string reason = firstBlocked is null ? "" : $"{Labels.Synth(firstBlocked.Synth)} · {firstBlocked.Status}：{firstBlocked.Detail} · ";
        _statusMessage = (currentFilterOnly ? "当前筛选：" : "") + reason + result.Summary;
        _taskProgressText = FormatRenderProgress(new QueueProgress(result.Processed, result.Eligible, "", "", null)
        {
            Generated = result.Generated, Reused = result.Reused, Failed = result.Failed,
            Unconfigured = result.Unconfigured, Unavailable = result.Unavailable, RetryLimited = result.RetryLimited
        });
        _taskSelectionText = (currentFilterOnly ? "当前筛选 · " : "") + result.Selection.Description;
        RefreshCompatibility();
        await RefreshInitBaselinesAsync();
        if (!currentFilterOnly && result.Generated + result.Reused > 0) ShowGeneratedPreviews();
        Log(_statusMessage);
        Log($"本次队列用时 {result.ElapsedSeconds:0.00} 秒，最多同时运行 {result.PeakActiveCount} 个任务。");
        _taskMemoryText = $"本次总内存采样峰值 {result.PeakWorkingSetBytes / 1048576d:0} MiB / {result.MemoryBudgetBytes / 1048576d:0} MiB · 并行目标 {result.ResolvedConcurrency} 条";
        Log(_taskMemoryText);
    });

    private async Task RunTask(Func<CancellationToken, Task> action)
    {
        if (!IsIdle || _isClosing) return;
        _isBusy = true;
        _stopRequested = false;
        _progressValue = 0;
        _hasTaskFeedback = true;
        _progressIndeterminate = true;
        _needsRendererSetup = false;
        _taskTitle = "任务进行中";
        _statusMessage = "正在准备任务…";
        _taskProgressText = "准备中";
        _taskMemoryText = "";
        _taskSelectionText = "";
        _taskWatch.Restart();
        _taskTimer.Start();
        _taskCancellation = new CancellationTokenSource();
        NotifyAll();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            _taskCancellation.Token.ThrowIfCancellationRequested();
            await action(_taskCancellation.Token);
            if (_taskTitle == "任务进行中") _taskTitle = "任务完成";
            if (_statusMessage == "正在准备任务…") _statusMessage = "任务已完成。";
            _progressValue = 100;
            _taskProgressText = _taskProgressText == "准备中" ? "" : _taskProgressText;
        }
        catch (OperationCanceledException)
        {
            _flushTaskProgress?.Invoke();
            _taskTitle = "任务已停止";
            _statusMessage = "已停止，已完成的结果已保留，可稍后继续。";
            Log(_statusMessage);
        }
        catch (RenderResourceLimitException ex)
        {
            _flushTaskProgress?.Invoke();
            _taskTitle = "生成已安全停止";
            _statusMessage = ex.Message;
            _taskProgressText = FormatRenderProgress(new QueueProgress(ex.Summary.Processed, ex.Summary.Eligible, "", "", null)
            {
                Generated = ex.Summary.Generated, Reused = ex.Summary.Reused, Failed = ex.Summary.Failed,
                Unconfigured = ex.Summary.Unconfigured, Unavailable = ex.Summary.Unavailable, RetryLimited = ex.Summary.RetryLimited
            });
            _taskSelectionText = ex.Summary.Selection.Description;
            _taskMemoryText = $"总内存采样峰值 {ex.Summary.PeakWorkingSetBytes / 1048576d:0} MiB / {ex.Summary.MemoryBudgetBytes / 1048576d:0} MiB；已完成的预览保留，可降低并行后继续。";
            Log(_statusMessage);
            Log(_taskMemoryText);
        }
        catch (Exception ex)
        {
            _flushTaskProgress?.Invoke();
            _flushTaskProgress = null;
            _taskTimer.Stop();
            _progressIndeterminate = false;
            _taskTitle = "任务未完成";
            ShowError(ex);
        }
        finally
        {
            _flushTaskProgress = null;
            _taskTimer.Stop();
            _taskWatch.Stop();
            _progressIndeterminate = false;
            if (_taskProgressText == "准备中") _taskProgressText = "";
            _isBusy = false;
            _stopRequested = false;
            _taskCancellation.Dispose();
            _taskCancellation = null;
            foreach (var row in Presets) row.Refresh();
            RefreshPresetView();
            RefreshCompatibility();
            try { await RefreshInitBaselinesAsync(); }
            catch (Exception error) { Log("基准状态未刷新：" + error.Message); }
            NotifyAll();
            if (_selected is not null && _selected.Record.PreviewPath != _loadedPreviewPath) await LoadSelectedPreview();
            if (_closeWhenDone) Close();
        }
    }

    private void StopTask_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStopTask) return;
        _stopRequested = true;
        _taskTitle = "正在停止任务";
        _statusMessage = "正在停止当前任务…";
        _taskCancellation?.Cancel();
        NotifyAll();
    }

    private void ViewTasks_Click(object sender, RoutedEventArgs e) => Pages.SelectedItem = TasksTab;
    private void ViewGenerated_Click(object sender, RoutedEventArgs e) => ShowGeneratedPreviews();
    private void ShowGeneratedPreviews()
    {
        ResetFilterControls();
        ShowDuplicates.IsChecked = false;
        SetFilterSelection("status", "generated");
        SelectFilterGroup("status");
        Pages.SelectedIndex = 0;
        PresetGrid.SelectedItem = PresetsView.Cast<PresetRow>().FirstOrDefault();
        NotifyAll();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        ResetFilterControls();
        NotifyAll();
    }

    private void ResetFilterControls()
    {
        _updatingFilterChoices = true;
        try
        {
            SearchBox.Clear();
            foreach (var group in FilterGroups) group.SetSelection([]);
        }
        finally { _updatingFilterChoices = false; }
        RefreshPresetView();
        NotifyFilterResults();
    }

    private void RefreshCompatibility()
    {
        Compatibility.Clear();
        foreach (var readiness in RenderReadinessService.Evaluate(_settings))
        {
            var generated = Presets.Count(p => p.Record.Synth == readiness.Synth && p.Record.DuplicateOfId.Length == 0 && p.HasPreview);
            var review = Presets.Count(p => p.Record.Synth == readiness.Synth && p.Record.DuplicateOfId.Length == 0 && p.HasPreview && p.Record.Status == PresetStatus.NeedsReview);
            var output = generated == 0 ? "尚无已生成预览。" : $"已生成 {generated} 项，其中 {review} 项待试听确认。";
            Compatibility.Add(new(Labels.Synth(readiness.Synth), readiness.Status, readiness.Detail + " " + output));
        }
    }
    private void ViewTaskSettings_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedItem = SettingsTab;
        SettingsPages.SelectedItem = RenderSettingsTab;
        RendererSettings.IsExpanded = true;
        RendererSettings.BringIntoView();
    }

    private sealed class QueueReporter(Action<QueueProgress> report) : IProgress<QueueProgress>
    {
        public void Report(QueueProgress value) => report(value);
    }

    private void NotifyTaskProgress()
    {
        foreach (var name in new[] { nameof(TaskTitle), nameof(StatusMessage), nameof(TaskProgressText), nameof(TaskSelectionText), nameof(TaskMemoryText), nameof(TaskMemoryVisible), nameof(TaskElapsed), nameof(ProgressValue), nameof(IsProgressIndeterminate), nameof(TaskBandVisible) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isClosing) return;
        _selected = PresetGrid.SelectedItem as PresetRow;
        RefreshPresetMetadataEditors();
        await LoadSelectedPreview();
    }

    private async Task LoadSelectedPreview()
    {
        if (_isClosing) return;
        var generation = ++_selectionGeneration;
        _loadedPreviewPath = "";
        StopPlayback();
        _player.Unload();
        _mediaOpened = false;
        Waveform.SetAudio(null);
        NotifyAll();
        if (!CanPlay || _selected is null) return;
        var path = _selected.Record.PreviewPath;
        try
        {
            var audio = await Task.Run(() => PreviewAudioFile.Read(path));
            if (_isClosing || generation != _selectionGeneration) return;
            Waveform.SetAudio(audio);
            SeekSlider.Maximum = audio.DurationSeconds;
            _player.Load(audio);
            _mediaOpened = true;
            if (_playWhenOpen) { _player.Play(_openPosition); _isPlaying = true; }
            _loadedPreviewPath = path;
        }
        catch (Exception ex) { if (!_isClosing && generation == _selectionGeneration) Log("无法读取预览：" + ex.Message); }
    }

    private void Preset_DoubleClick(object sender, TappedEventArgs e) { if (CanPlay) StartPlayback(0, null); }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        if (_isPlaying) { _player.Pause(); _isPlaying = false; }
        else StartPlayback(_player.PositionSeconds, null);
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void PlayNote_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _selected is null || sender is not Button button) return;
        var note = FindPreviewNote(_selected.Record, button.Tag?.ToString() ?? "");
        if (note is null) { Log("此预览缺少对应音区的保存记录。"); return; }
        StartPlayback(note.StartSeconds, note.StartSeconds + note.DurationSeconds);
    }
    private static NotePreview? FindPreviewNote(PresetRecord record, string region)
    {
        if (string.IsNullOrWhiteSpace(record.TemplateJson))
        {
            int legacyIndex = region switch { "Low" => 0, "Middle" => 1, "High" => 2, _ => -1 };
            return legacyIndex >= 0 && legacyIndex < record.Notes.Count ? record.Notes[legacyIndex] : null;
        }
        try
        {
            var template = JsonSerializer.Deserialize<PreviewTemplate>(record.TemplateJson, LibraryJson.Options);
            int? pitch = region switch { "Low" => template?.LowNote, "Middle" => template?.MiddleNote, "High" => template?.HighNote, _ => null };
            int index = template?.NoteOrder == PreviewNoteOrder.MiddleHighLow
                ? region switch { "Middle" => 0, "High" => 1, "Low" => 2, _ => -1 }
                : region switch { "Low" => 0, "Middle" => 1, "High" => 2, _ => -1 };
            return index >= 0 && index < record.Notes.Count && record.Notes[index].MidiNote == pitch ? record.Notes[index] : null;
        }
        catch (JsonException) { return null; }
    }
    private void StartPlayback(double seconds, double? end)
    {
        if (!CanPlay) return;
        _openPosition = seconds;
        _segmentEnd = end;
        _playWhenOpen = true;
        if (_mediaOpened)
        {
            try { _player.Play(seconds); _isPlaying = true; }
            catch (Exception ex) { ShowError(ex); }
        }
    }
    private void StopPlayback()
    {
        if (_isClosing) return;
        _player.Stop();
        _isPlaying = false;
        _playWhenOpen = false;
        _openPosition = 0;
        _segmentEnd = null;
        if (SeekSlider is not null) SeekSlider.Value = 0;
        Waveform?.SetPosition(0);
    }
    private void UpdatePlayback()
    {
        if (_isClosing) return;
        if (_isPlaying && (!_player.IsPlaying || _segmentEnd is { } end && _player.PositionSeconds >= end)) { StopPlayback(); return; }
        if (_mediaOpened && !_seeking) SeekSlider.Value = _player.PositionSeconds;
        Waveform.SetPosition(_player.PositionSeconds);
        var duration = TimeSpan.FromSeconds(_mediaOpened ? _player.DurationSeconds : 0);
        var format = duration.TotalSeconds < 10 ? @"mm\:ss\.f" : @"mm\:ss";
        TimeLabel.Text = TimeSpan.FromSeconds(_player.PositionSeconds).ToString(format) + " / " + duration.ToString(format);
    }
    private void Seek_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_isClosing) return;
        _seeking = false;
        _segmentEnd = null;
        _openPosition = SeekSlider.Value;
        if (_mediaOpened) _player.Seek(SeekSlider.Value);
    }
    private void Volume_Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isClosing || _storageRelocated || e.Property != RangeBase.ValueProperty) return;
        _player.Volume = VolumeSlider.Value;
        if (_settings is not null) _settings.PlaybackVolume = VolumeSlider.Value;
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelection || _selected is null) return;
        _selected.Record.Favorite = !_selected.Record.Favorite;
        SaveFeedback();
    }
    private void Like_Click(object sender, RoutedEventArgs e) => SetFeedback(1);
    private void Dislike_Click(object sender, RoutedEventArgs e) => SetFeedback(-1);
    private void SetFeedback(int value)
    {
        if (!CanEditSelection || _selected is null) return;
        _selected.Record.Feedback = _selected.Record.Feedback == value ? 0 : value;
        _selected.Record.FeedbackUtc = DateTime.UtcNow;
        SaveFeedback();
    }
    private void SaveFeedback()
    {
        _store.Upsert(_selected!.Record);
        _selected.Refresh();
        RefreshPresetView();
        NotifyAll();
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!CanConfirm || _selected is null) return;
        _selected.Record.Status = PresetStatus.Ready;
        _selected.Record.Message = "已由用户试听确认音色对应关系。";
        SaveFeedback();
        RefreshCompatibility();
    }
    private async void AssignSynth_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAssignSynth || _selected is null) return;
        try
        {
            if (AssignmentSynthChoice.SelectedItem is not Choice<SynthKind> choice)
                throw new ArgumentException("请先选择此预制所属的音源。");
            var row = _selected;
            ScanService.AssignSynth(row.Record, choice.Value);
            var records = Presets.Select(p => p.Record).ToList();
            PresetIndex.RebuildDuplicates(records, Sources.ToList());
            _store.UpsertMany(records);
            SetPresets(await Task.Run(() =>
            {
                DuplicatePreviewService.Synchronize(_store);
                return _store.LoadPresets();
            }));
            await LoadSelectedPreview();
            Log($"已确认音源标签：{row.Name} → {Labels.Synth(choice.Value)}。");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ViewCanonical_Click(object sender, RoutedEventArgs e)
    {
        var record = Presets.FirstOrDefault(p => p.Record.Id == _selected?.Record.DuplicateOfId);
        if (record is null) return;
        ResetFilterControls();
        ShowDuplicates.IsChecked = false;
        PresetGrid.SelectedItem = record;
        PresetGrid.ScrollIntoView(record, null);
    }
    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || _isStorageMaintenance || _storageRelocated || _selected is null) return;
        try
        {
            Process.Start(PrepareSourceReveal(_selected.Record.SourcePath, OperatingSystem.IsWindows()));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowColumns_Click(object sender, RoutedEventArgs e)
    {
        if (ColumnsButton.ContextMenu is not { } menu) return;
        menu.Open(ColumnsButton);
    }

    private void ColumnMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Items.Clear();
        foreach (var column in PresetGrid.Columns.OrderBy(column => column.DisplayIndex))
        {
            var item = new MenuItem { Header = column.Header?.ToString(), ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (!item.IsChecked && PresetGrid.Columns.Count(value => value.IsVisible) <= 1)
                { item.IsChecked = true; return; }
                column.IsVisible = item.IsChecked;
            };
            menu.Items.Add(item);
        }
    }

    private void LoadCategoryRulesEditor()
    {
        RulesPathBox.Text = PresetCategoryService.DefaultRulesPath;
        try
        {
            _categoryService.EnsureRulesFile();
            if (new FileInfo(PresetCategoryService.DefaultRulesPath).Length > 256 * 1024)
                throw new InvalidDataException("分类规则超过 256 KiB，请缩小文件后重新加载。");
            RulesEditor.Text = File.ReadAllText(PresetCategoryService.DefaultRulesPath);
            _loadedCategoryRules = RulesEditor.Text;
            var result = _categoryService.Load();
            _categoryRulesStatus = result.Success ? $"已加载 {result.RuleCount} 个分类。" : result.Error + " 已保留原有分类。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { _categoryRulesStatus = "分类规则未加载：" + error.Message; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryRulesStatus)));
    }

    private async void ReloadCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        if (RulesEditor.Text != _loadedCategoryRules && !await Dialogs.ShowAsync(this, "重新载入将放弃尚未保存的分类规则，是否继续？", "分类规则", question: true)) return;
        LoadCategoryRulesEditor();
    }

    private void OpenCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _categoryService.EnsureRulesFile();
            Process.Start(new ProcessStartInfo(PresetCategoryService.DefaultRulesPath) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ApplyCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        if (RulesEditor.Text != _loadedCategoryRules)
        {
            ShowError(new InvalidOperationException("分类规则尚未保存，请先点击“保存并应用”。"));
            return;
        }
        await ApplyCategoriesAsync();
    }

    private async void SaveCategoryRules_Click(object sender, RoutedEventArgs e)
    {
        var text = RulesEditor.Text ?? "";
        var validation = PresetCategoryService.Validate(text);
        if (!validation.Success) { ShowError(new InvalidDataException(validation.Error)); return; }
        await RunTask(async token =>
        {
            var path = PresetCategoryService.DefaultRulesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, text, new System.Text.UTF8Encoding(false), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                _loadedCategoryRules = text;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            await ApplyCategoriesCoreAsync(token);
        });
    }

    private Task ApplyCategoriesAsync() => RunTask(ApplyCategoriesCoreAsync);

    private async Task ApplyCategoriesCoreAsync(CancellationToken token)
    {
        var records = Presets.Select(row => row.Record).ToList();
        var applied = await Task.Run(() =>
        {
            var copies = JsonSerializer.Deserialize<List<PresetRecord>>(JsonSerializer.Serialize(records, LibraryJson.Options), LibraryJson.Options)!;
            var result = _categoryService.ReloadAndApply(copies, token);
            if (result.Success)
            {
                token.ThrowIfCancellationRequested();
                _store.UpsertMany(copies);
            }
            return (Result: result, Records: copies);
        }, token);
        var result = applied.Result;
        if (!result.Success) throw new InvalidDataException(result.Error);
        var categories = applied.Records.ToDictionary(record => record.Id);
        foreach (var record in records)
        {
            record.CategoryTags = categories[record.Id].CategoryTags;
            record.CategoryIds = categories[record.Id].CategoryIds;
            record.AutomaticCategories = categories[record.Id].AutomaticCategories;
        }
        SynchronizeMetadata(records);
        RefreshPresetMetadataEditors();
        foreach (var row in Presets) row.Refresh();
        RefreshFilterChoices();
        _categoryRulesStatus = $"已检查 {result.Processed} 个音色，更新 {result.Changed} 个分类。";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryRulesStatus)));
        Log(_categoryRulesStatus);
        NotifyAll();
    }

    private async Task RefreshInitBaselinesAsync()
    {
        if (_isClosing) return;
        int generation = ++_baselineRefreshGeneration;
        foreach (var plugin in Plugins)
            plugin.InitBaselinePath = _settings.Plugins.FirstOrDefault(saved => saved.Synth == plugin.Synth)?.InitBaselinePath ?? "";
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var rows = new List<InitializationRow>();
        foreach (var plugin in settings.Plugins)
        {
            var state = await InitBaselineService.InspectAsync(settings, plugin);
            var readiness = RenderReadinessService.Evaluate(plugin, settings.DiscoveredPlugins);
            bool supported = InitBaselineService.Supports(plugin);
            if (supported && !readiness.CanAttemptRender)
                state = state with { Status = readiness.Status, Detail = readiness.Detail };
            rows.Add(new(plugin.Synth, Labels.Synth(plugin.Synth), state.Status, state.Detail, state.WavPath,
                supported && readiness.CanAttemptRender,
                (state.CanCompare || state.CanRenderWithVerifiedPresetLoad) && File.Exists(state.WavPath)));
        }
        if (_isClosing || generation != _baselineRefreshGeneration) return;
        InitBaselines.Clear();
        foreach (var row in rows) InitBaselines.Add(row);
    }

    private async void GenerateInitBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: InitializationRow row }) return;
        await RunTask(async token =>
        {
            var plugin = _settings.Plugins.First(p => p.Synth == row.Kind);
            _taskTitle = "正在生成默认启动基准";
            _statusMessage = Labels.Synth(plugin.Synth) + "：采集三份未加载预制的默认启动声音并校准。";
            NotifyTaskProgress();
            await Task.Run(() => RefreshBinaryHashes(_settings, token), token);
            var resourceOwner = _taskCancellation;
            var resourceProgress = new QueueReporter(value => Dispatcher.UIThread.Post(() =>
            {
                if (!_isBusy || _isClosing || !ReferenceEquals(_taskCancellation, resourceOwner) || _taskTitle != "正在生成默认启动基准") return;
                _taskMemoryText = $"总内存 {value.WorkingSetBytes / 1048576d:0} / {value.MemoryBudgetBytes / 1048576d:0} MiB · 正在建立声音参照";
                NotifyTaskProgress();
            }));
            var state = await Task.Run(() => new RenderQueue(_store).GenerateInitializationBaselineAsync(
                _settings, plugin, ApplicationPaths.WorkerPath, resourceProgress, token), token);
            await Task.Run(() => _store.SaveSettings(_settings));
            Log(state.Detail);
            _statusMessage = state.Detail;
            await RefreshInitBaselinesAsync();
            if (!state.CanCompare && !state.CanRenderWithVerifiedPresetLoad) throw new InvalidOperationException(state.Detail);
            _taskTitle = state.CanRenderWithVerifiedPresetLoad ? "已启用逐音色状态核验" : "默认启动基准已生成";
        });
    }

    private async void PlayInitBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: InitializationRow row }) return;
        await RunTask(async token =>
        {
            StopPlayback();
            var plugin = _settings.Plugins.First(p => p.Synth == row.Kind);
            var state = await InitBaselineService.InspectAsync(_settings, plugin, token);
            if (!state.CanCompare && !state.CanRenderWithVerifiedPresetLoad) throw new InvalidOperationException(state.Detail);
            using var player = new WavePlayer();
            var audio = await Task.Run(() => WaveFile.Read(state.WavPath, token), token);
            player.Volume = _settings.PlaybackVolume;
            player.Load(audio);
            player.Play(0);
            var watch = Stopwatch.StartNew();
            while (player.IsPlaying)
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed.TotalSeconds > audio.DurationSeconds + 10) throw new TimeoutException("基准试听未在预期时间内结束。");
                await Task.Delay(50, token);
            }
        });
    }

    private static void RefreshGrid(DataGrid grid)
    {
        var selected = grid.SelectedItem;
        var items = grid.ItemsSource;
        grid.ItemsSource = null;
        grid.ItemsSource = items;
        grid.SelectedItem = selected;
    }

    private void PopulateSettings()
    {
        Sources.Clear(); foreach (var source in _settings.Sources) Sources.Add(source);
        Plugins.Clear(); foreach (var plugin in _settings.Plugins)
            Plugins.Add(JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(plugin, LibraryJson.Options), LibraryJson.Options)!);
        PluginDirectories.Clear();
        foreach (var directory in _settings.PluginDirectories) PluginDirectories.Add(directory);
        RefreshDiscoveredPlugins();
        var t = _settings.Template;
        ParallelRendering.SelectedItem = ParallelRenderChoices.FirstOrDefault(choice => choice.Value == _settings.MaxParallelRenders) ?? ParallelRenderChoices[0];
        MemoryBudgetBox.SelectedItem = MemoryBudgetChoices.FirstOrDefault(choice => choice.Value == _settings.MemoryBudgetGiB)
            ?? MemoryBudgetChoices.Single(choice => choice.Value == RenderResourcePolicy.DefaultMemoryBudgetGiB);
        QuickPreviewMode.IsChecked = t.QuickPreview;
        QuickPreviewSecondsBox.Text = t.QuickPreviewSeconds.ToString(CultureInfo.InvariantCulture);
        LowNoteBox.Text = NoteNames.Format(t.LowNote);
        MiddleNoteBox.Text = NoteNames.Format(t.MiddleNote);
        HighNoteBox.Text = NoteNames.Format(t.HighNote);
        VelocityBox.Text = t.Velocity.ToString(CultureInfo.InvariantCulture);
        HoldBox.Text = t.HoldSeconds.ToString(CultureInfo.InvariantCulture);
        TailBox.Text = t.TailSeconds.ToString(CultureInfo.InvariantCulture);
        BpmBox.Text = t.Bpm.ToString(CultureInfo.InvariantCulture);
        SampleRateBox.SelectedIndex = t.SampleRate == 48000 ? 1 : 0;
        PreviewFormatBox.SelectedIndex = _settings.PreviewFormat == PreviewAudioFormat.Wav ? 1 : 0;
        PreviewDirectoryBox.Text = T("Preview/（当前程序目录下）");
        BudgetBox.Text = _settings.StorageBudgetGb.ToString(CultureInfo.InvariantCulture);
        AttemptsBox.Text = _settings.MaxAttempts.ToString(CultureInfo.InvariantCulture);
        VolumeSlider.Value = _settings.PlaybackVolume;
    }

    private void AddSourceFromLibrary_Click(object sender, RoutedEventArgs e) { Pages.SelectedItem = SettingsTab; SettingsPages.SelectedItem = DirectorySettingsTab; AddSource_Click(sender, e); }
    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("选择预制目录"), AllowMultiple = false });
        var directory = folders.FirstOrDefault()?.TryGetLocalPath();
        if (directory is null || SourceSynthChoice.SelectedItem is not Choice<SynthKind?> synth) return;
        var automatic = synth.Value is null;
        if (Sources.Any(s => s.AutoDetect == automatic && (automatic || s.Synth == synth.Value) && string.Equals(ApplicationPaths.PathIdentity(s.Path), ApplicationPaths.PathIdentity(directory), StringComparison.Ordinal))) return;
        if (!TryChangeDirectories(() => Sources.Add(new SourceFolder { Path = directory, Synth = synth.Value ?? SynthKind.Vital, AutoDetect = automatic }))) return;
        Log($"已添加预制目录（{(automatic ? "自动识别" : Labels.Synth(synth.Value.GetValueOrDefault()))}）：{directory}");
    }
    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceGrid.SelectedItem is not SourceFolder source) return;
        if (!TryChangeDirectories(() => Sources.Remove(source))) return;
        Log("已移除扫描目录，保留已索引音色：" + source.Path);
    }
    private void AutoDetectSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceGrid.SelectedItem is not SourceFolder source) return;
        if (!TryChangeDirectories(() => source.AutoDetect = true)) return;
        RefreshGrid(SourceGrid);
        Log("目录已切换为自动识别，下次扫描更新音源标签：" + source.Path);
    }
    private async void BrowsePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PluginConfiguration plugin }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = plugin.Backend switch { RenderBackend.DawDreamer => "选择 Python 程序（不是音源 DLL）", RenderBackend.Sushi => "选择自备的 Sushi 程序 sushi.exe", _ => "选择渲染程序" }, AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("渲染程序 / 应用程序") { Patterns = ["*"], AppleUniformTypeIdentifiers = ["public.executable", "com.apple.application-bundle"] }, FilePickerFileTypes.All]
        });
        var executable = files.FirstOrDefault()?.TryGetLocalPath();
        if (executable is null) return;
        if (plugin.Backend == RenderBackend.DawDreamer && new[] { ".dll", ".vst", ".vst3", ".component" }.Contains(Path.GetExtension(executable), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("DawDreamer 请选 Python 可执行文件；音源 DLL 或插件包请在“已发现音源”中关联。");
        if (plugin.Backend == RenderBackend.Sushi && !PluginSelectionService.IsSushiHostAvailable(executable))
            throw new ArgumentException("Sushi 请选择带有 SoundPreviewer 预制加载补丁的 Windows x64 sushi.exe，同目录需有 sndfile.dll。此实验宿主须自行准备。");
        plugin.ExecutablePath = Directory.Exists(executable) ? PluginScanService.ResolveBundleExecutable(executable) ?? throw new ArgumentException("应用程序包中没有可执行文件。") : executable;
        if (plugin.Backend == RenderBackend.Unconfigured) plugin.Backend = plugin.Synth == SynthKind.Vital ? RenderBackend.VitalCli : RenderBackend.ExternalWorker;
        if (plugin.Backend == RenderBackend.VitalCli)
            plugin.Version = FileVersionInfo.GetVersionInfo(plugin.ExecutablePath).ProductVersion ?? plugin.Version;
        RefreshGrid(PluginGrid);
    }
    private async void AddPluginDirectory_Click(object sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("选择音源安装目录"), AllowMultiple = true });
        if (folders.Count == 0) return;
        if (!TryChangeDirectories(() =>
        {
            foreach (var folder in folders)
                if (folder.TryGetLocalPath() is { } directory && !PluginDirectories.Any(existing => ApplicationPaths.PathIdentity(existing) == ApplicationPaths.PathIdentity(directory)))
                    PluginDirectories.Add(directory);
        })) return;
        Log("音源安装目录已保存。");
    }
    private void RemovePluginDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (PluginDirectoryGrid.SelectedItem is not string directory) return;
        if (!TryChangeDirectories(() => PluginDirectories.Remove(directory))) return;
        Log("已移除音源扫描目录，保留已关联的音源：" + directory);
    }
    private static List<string> ResolvePluginScanDirectories(IEnumerable<string> configuredDirectories, IEnumerable<PluginConfiguration> plugins)
    {
        var configured = configuredDirectories.ToList();
        if (configured.Count > 0) return configured;
        // Keep absent and inaccessible locations so installs made later are found and scan warnings remain visible.
        return ApplicationPaths.DefaultPluginDirectories.Concat(plugins
                .Select(plugin => plugin.PluginPath.Length > 0 ? plugin.PluginPath : plugin.Backend == RenderBackend.VitalCli ? plugin.ExecutablePath : "")
                .Select(InferPluginScanDirectory).OfType<string>())
            .DistinctBy(ApplicationPaths.PathIdentity).ToList();
    }
    private static string? InferPluginScanDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!OperatingSystem.IsWindows() && (path.Contains('\\') || (path.Length > 1 && path[1] == ':')))) return null;
        try
        {
            var absolute = Path.GetFullPath(path);
            for (var directory = new DirectoryInfo(absolute); directory is not null; directory = directory.Parent)
                if (new[] { ".app", ".vst", ".vst3", ".component" }.Contains(directory.Extension, StringComparer.OrdinalIgnoreCase))
                    return directory.Parent?.FullName;
            return Path.GetDirectoryName(absolute);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }
    private void ViewPluginDirectories_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedItem = SettingsTab;
        SettingsPages.SelectedItem = DirectorySettingsTab;
    }
    private static bool IsWithinUnavailablePluginRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try { return ApplicationPaths.IsWithin(path, root); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return false; }
    }
    private static List<DiscoveredPlugin> MergePluginScanResults(PluginScanResult result, IEnumerable<DiscoveredPlugin> previous)
    {
        var retained = previous.Where(plugin => result.UnavailableRoots.Any(root => IsWithinUnavailablePluginRoot(plugin.FilePath, root)));
        return result.Plugins.Concat(retained).DistinctBy(plugin => plugin.Id).ToList();
    }
    private async void ScanPlugins_Click(object sender, RoutedEventArgs e)
    {
        Pages.SelectedItem = SettingsTab;
        SettingsPages.SelectedItem = DiscoveredSettingsTab;
        await RunTask(async token =>
        {
            if (PluginDirectories.Count == 0)
            {
                foreach (var directory in ResolvePluginScanDirectories([], Plugins)) PluginDirectories.Add(directory);
                _settings.PluginDirectories = PluginDirectories.ToList();
                SaveDirectoryConfiguration(_settings);
            }
            // Existing installations already have saved VST-only roots. Include the
            // AU locations for this scan without rewriting the user's directory list.
            var scanDirectories = OperatingSystem.IsMacOS()
                ? PluginDirectories.Concat(ApplicationPaths.AudioUnitDirectories).Distinct(StringComparer.Ordinal).ToList()
                : PluginDirectories.ToList();
            _taskTitle = "正在扫描已安装音源";
            _statusMessage = "正在扫描系统、用户和自定义安装目录…";
            NotifyAll();
            var result = await new PluginScanService().ScanAsync(scanDirectories, token);
            var merged = MergePluginScanResults(result, _settings.DiscoveredPlugins);
            var associated = PluginSelectionService.ApplyAutomatic(result.Plugins, Plugins, X86HostPath);
            PluginSelectionService.ApplyPreparedDawDreamer(Plugins, App.DataDirectory, merged);
            _settings.PluginDirectories = PluginDirectories.ToList();
            _settings.DiscoveredPlugins = merged;
            _settings.Plugins = ClonePlugins();
            _store.SaveSettings(_settings);
            await InvalidateChangedPreviewsAsync();
            RefreshDiscoveredPlugins();
            RefreshGrid(PluginGrid);
            await RefreshInitBaselinesAsync();
            foreach (var warning in result.Warnings) Log(warning);
            _taskTitle = "音源扫描完成";
            _statusMessage = $"发现 {result.Plugins.Count} 个插件 / 独立程序，自动关联 {associated} 个音源。" +
                (result.UnavailableRoots.Count > 0 ? $"{result.UnavailableRoots.Count} 个安装位置暂不可访问，详情见运行记录。" : "渲染兼容性仍需验证。");
            Log(_statusMessage);
        });
    }
    private async void UseDiscoveredPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredPluginGrid.SelectedItem is not DiscoveredPluginRow row) return;
        await RunTask(async token =>
        {
            if (row.Record.Format == PluginBinaryFormat.AudioUnit)
                throw new ArgumentException("AU 已发现，但桌面预制后端尚不支持 AU。Logic .patch 请运行 Start-LogicPreview-Web.command，打开 http://localhost:5002。");
            var configuration = Plugins.FirstOrDefault(p => p.Synth == row.Record.Synth)
                ?? throw new ArgumentException("此插件尚不属于支持的音源类型。");
            PluginSelectionService.Assign(configuration, row.Record, X86HostPath);
            PluginSelectionService.ApplyPreparedDawDreamer(Plugins, App.DataDirectory, _settings.DiscoveredPlugins);
            _settings.Plugins = ClonePlugins();
            _store.SaveSettings(_settings);
            await InvalidateChangedPreviewsAsync();
            RefreshDiscoveredPlugins();
            RefreshGrid(PluginGrid);
            await RefreshInitBaselinesAsync();
            Log($"已关联 {Labels.Synth(row.Record.Synth)}：{row.Record.FilePath}（{row.ArchitectureLabel} / {row.FormatLabel}）。");
            var readiness = RenderReadinessService.Evaluate(configuration, _settings.DiscoveredPlugins);
            if (!readiness.CanAttemptRender) Log(Labels.Synth(configuration.Synth) + "：" + readiness.Detail);
        });
    }
    private void RefreshDiscoveredPlugins()
    {
        var selectedId = (DiscoveredPluginGrid?.SelectedItem as DiscoveredPluginRow)?.Record.Id;
        DiscoveredPlugins.Clear();
        foreach (var plugin in _settings.DiscoveredPlugins.OrderBy(p => p.Synth == SynthKind.Unknown).ThenBy(p => p.Synth)
            .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase)) DiscoveredPlugins.Add(new DiscoveredPluginRow(plugin, Plugins));
        if (DiscoveredPluginGrid is not null) DiscoveredPluginGrid.SelectedItem = DiscoveredPlugins.FirstOrDefault(p => p.Record.Id == selectedId);
        RefreshCompatibility();
    }
    private List<PluginConfiguration> ClonePlugins() => JsonSerializer.Deserialize<List<PluginConfiguration>>(
        JsonSerializer.Serialize(Plugins, LibraryJson.Options), LibraryJson.Options)!;
    private void VitalDefaults_Click(object sender, RoutedEventArgs e)
    {
        QuickPreviewMode.IsChecked = false;
        var hold = Math.Clamp(Math.Round(ParseDouble(HoldBox.Text, "按键时长")), 1, 30);
        HoldBox.Text = hold.ToString(CultureInfo.InvariantCulture);
        TailBox.Text = (hold * 0.3).ToString("0.0", CultureInfo.InvariantCulture);
        VelocityBox.Text = "89";
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e) => await RunTask(SaveSettingsCoreAsync);
    private async Task SaveSettingsCoreAsync(CancellationToken token)
        {
            if (!PluginGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !PluginGrid.CommitEdit(DataGridEditingUnit.Row, true))
                throw new ArgumentException("请修正渲染器设置中的输入。");
            var template = new PreviewTemplate
            {
                AdditionalProperties = _settings.Template.AdditionalProperties is { } fields ? new(fields) : null,
                LowNote = NoteNames.Parse(LowNoteBox.Text ?? ""), MiddleNote = NoteNames.Parse(MiddleNoteBox.Text ?? ""), HighNote = NoteNames.Parse(HighNoteBox.Text ?? ""),
                Velocity = ParseInt(VelocityBox.Text, "力度"), HoldSeconds = ParseDouble(HoldBox.Text, "按键时长"), TailSeconds = ParseDouble(TailBox.Text, "尾音"),
                Bpm = ParseInt(BpmBox.Text, "BPM"), SampleRate = SampleRateBox.SelectedIndex == 1 ? 48000 : 44100,
                QuickPreview = QuickPreviewMode.IsChecked == true,
                QuickPreviewSeconds = ParseDouble(QuickPreviewSecondsBox.Text, "快速预览时长")
            };
            template.Validate();
            var budget = ParseDouble(BudgetBox.Text, "存储预算");
            var attempts = ParseInt(AttemptsBox.Text, "最大尝试次数");
            if (budget <= 0 || !double.IsFinite(budget) || attempts is < 1 or > 5) throw new ArgumentException("存储预算须大于 0，最大尝试次数须为 1 到 5。");
            PluginSelectionService.RefreshBundledHost(Plugins, X86HostPath, discovered: _settings.DiscoveredPlugins);
            PluginSelectionService.RefreshBundledSushiHost(Plugins, SushiHostPath);
            PluginSelectionService.RefreshLocalDawDreamer(Plugins, App.DataDirectory);
            RenderReadinessService.ValidateSettings(Plugins);
            var candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
            candidate.Template = template;
            candidate.PreviewFormat = PreviewFormatBox.SelectedIndex == 1 ? PreviewAudioFormat.Wav : PreviewAudioFormat.Aac;
            candidate.Plugins = JsonSerializer.Deserialize<List<PluginConfiguration>>(JsonSerializer.Serialize(Plugins, LibraryJson.Options), LibraryJson.Options)!;
            candidate.Sources = Sources.ToList();
            candidate.PluginDirectories = PluginDirectories.ToList();
            await Task.Run(() => RefreshBinaryHashes(candidate, token), token);
            candidate.StorageBudgetGb = budget;
            candidate.MaxAttempts = attempts;
            candidate.MaxParallelRenders = (ParallelRendering.SelectedItem as Choice<int>)?.Value ?? 0;
            candidate.MemoryBudgetGiB = (MemoryBudgetBox.SelectedItem as Choice<int>)?.Value ?? RenderResourcePolicy.DefaultMemoryBudgetGiB;
            RenderResourcePolicy.ResolveMemoryLimitBytes(candidate.MemoryBudgetGiB);
            if (!App.SmokeTest || App.TestInstalledStorage) candidate.PreviewDirectory = App.DataDirectory;
            SaveDirectoryConfiguration(candidate);
            _settings = candidate;
            foreach (var plugin in Plugins)
                plugin.AllowParallelRendering = candidate.Plugins.First(saved => saved.Synth == plugin.Synth).AllowParallelRendering;
            RefreshGrid(PluginGrid);
            await InvalidateChangedPreviewsAsync();
            RefreshCompatibility();
            await RefreshInitBaselinesAsync();
            Log("设置已保存。");
            foreach (var plugin in candidate.Plugins.Where(plugin => plugin.Backend != RenderBackend.Unconfigured))
            {
                var readiness = RenderReadinessService.Evaluate(plugin, candidate.DiscoveredPlugins);
                if (!readiness.CanAttemptRender) Log(Labels.Synth(plugin.Synth) + " 尚需配置：" + readiness.Detail);
            }
        }
    private async Task InvalidateChangedPreviewsAsync()
    {
        var records = Presets.Select(row => row.Record).ToList();
        var updates = await Task.Run(() =>
        {
            var changed = new List<PresetRecord>();
            foreach (var record in records)
            {
                if (record.Synth == SynthKind.Unknown || record.Status == PresetStatus.Unidentified) continue;
                var plugin = _settings.Plugins.FirstOrDefault(p => p.Synth == record.Synth);
                if (plugin is null) continue;
                var previousKey = record.AttemptedCacheKey.Length > 0 ? record.AttemptedCacheKey : record.CacheKey;
                var key = Fingerprints.CacheKey(record, plugin, _settings.Template);
                if (record.Status is PresetStatus.Missing or PresetStatus.Unsupported || previousKey.Length == 0 || previousKey == key) continue;
                var copy = JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;
                if (File.Exists(copy.PreviewPath)) copy.Message = "配置已变更，已保留现有预览。";
                else
                {
                    copy.Status = PresetStatus.Pending;
                    copy.Attempts = 0;
                    copy.AttemptedCacheKey = key;
                    copy.Message = "配置已变更，等待生成。";
                }
                changed.Add(copy);
            }
            if (changed.Count > 0) _store.UpsertMany(changed);
            return changed.ToDictionary(record => record.Id);
        });
        foreach (var row in Presets)
        {
            if (!updates.TryGetValue(row.Record.Id, out var copy)) continue;
            row.Record.Status = copy.Status;
            row.Record.Attempts = copy.Attempts;
            row.Record.AttemptedCacheKey = copy.AttemptedCacheKey;
            row.Record.Message = copy.Message;
            row.Refresh();
        }
        RefreshPresetView();
        NotifyAll();
    }
    private static int ParseInt(string? value, string label) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException(label + "须为整数。");
    private static void RefreshBinaryHashes(AppSettings settings, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        foreach (var plugin in settings.Plugins.Where(p => p.Backend != RenderBackend.Unconfigured))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在校验 {Labels.Synth(plugin.Synth)} 渲染程序…");
            // An offline drive must not erase the last known binary identity during an
            // unrelated settings save. Readiness still prevents executing a missing file.
            if (File.Exists(plugin.ExecutablePath)) plugin.ExecutableHash = Fingerprints.RendererExecutableHash(plugin, cancellationToken);
            progress?.Report($"正在校验 {Labels.Synth(plugin.Synth)} 插件文件…");
            string pluginHash = Fingerprints.PluginBinaryHash(plugin, settings.DiscoveredPlugins, cancellationToken);
            if (pluginHash.Length > 0) plugin.PluginHash = pluginHash;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
    private static double ParseDouble(string? value, string label) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : throw new ArgumentException(label + "须为数字。");

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出音色 MD5 索引与预览映射"), SuggestedFileName = "SoundPreviewer-index.json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(App.DataDirectory)
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            path = ValidateOutputPath(path);
            var json = JsonSerializer.Serialize(new { SchemaVersion = 2, ExportedUtc = DateTime.UtcNow, Presets = Presets.Select(p => p.Record).ToList() }, LibraryJson.Options);
            await File.WriteAllTextAsync(path, json);
            Log("音色 MD5 索引已导出：" + path);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private static string ValidateOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择保存位置。");
        return Path.GetFullPath(path, App.DataDirectory);
    }

    private async void OpenExistingLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || _isClosing) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = T("选择要导入的旧版 library.db 所在目录"), AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(App.DataDirectory)
            });
            if (_isClosing || folders.FirstOrDefault()?.TryGetLocalPath() is not { } directory) return;
            if (ApplicationPaths.SamePath(directory, App.DataDirectory)) { Log("已在使用当前程序的 Preview。"); return; }
            if (!File.Exists(Path.Combine(directory, "library.db"))) throw new ArgumentException("所选目录没有 library.db。");
            if (!await Dialogs.ShowAsync(this, "将把所选旧库和已记录的预览复制、校验到本程序的 Preview。当前 Preview 会完整保留到程序旁的独立备份目录，不合并两份库；旧库和旧音频也会保留。完成后重新打开程序。", "导入旧版音色库", question: true) || _isClosing) return;
            _storageRelocated = true;
            _relocatedDirectory = App.DataDirectory;
            UnloadForStorageMaintenance();
            NotifyAll();
            App.StartWithLibrary(directory, importLibrary: true);
            Close();
        }
        catch (Exception error) { _storageRelocated = false; NotifyAll(); ShowError(error); }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(App.DataDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Log(string message)
    {
        string entry = $"{DateTime.Now:HH:mm:ss}  {message}";
        Logs.Add(entry);
        RecentLogs.Insert(0, entry.ReplaceLineEndings(" "));
        while (RecentLogs.Count > 12) RecentLogs.RemoveAt(RecentLogs.Count - 1);
        while (Logs.Count > 250) Logs.RemoveAt(0);
        if (!App.SmokeTest)
            try { File.AppendAllText(Path.Combine(App.DataDirectory, "application.log"), $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}"); }
            catch (IOException) { }
    }
    private void ShowError(Exception error)
    {
        _statusMessage = error.Message;
        Log(error.Message);
        NotifyAll();
        if (App.SmokeTest) File.WriteAllText(Path.Combine(App.DataDirectory, "ui-error.txt"), error.ToString());
        else _ = Dialogs.ShowAsync(this, error.Message, "操作未完成");
    }
    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));

    private void RefreshClientLayout()
    {
        // A native desktop can clamp the window after children were arranged for its
        // requested size. Discard those cached measurements before using the real client.
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            control.InvalidateMeasure();
            control.InvalidateArrange();
        }
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
    }

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing) return;
        if (_isBusy) { e.Cancel = true; _closeWhenDone = true; _taskCancellation?.Cancel(); return; }
        CompleteWindowClose();
    }
    private void CompleteWindowClose()
    {
        if (_isClosing) return;
        // Selection and queued dispatcher callbacks can still arrive while Avalonia tears down the window.
        _isClosing = true;
        ++_selectionGeneration;
        ++_baselineRefreshGeneration;
        _playbackTimer.Stop();
        _taskTimer.Stop();
        _flushTaskProgress = null;
        _isPlaying = false;
        _mediaOpened = false;
        _playWhenOpen = false;
        _loadedPreviewPath = "";
        _player.Dispose();
        if (!_storageRelocated) _store.SaveSettings(_settings);
    }
}

public sealed record InitializationRow(SynthKind Kind, string Synth, string Status, string Detail, string WavPath, bool CanGenerate, bool CanPlay);
