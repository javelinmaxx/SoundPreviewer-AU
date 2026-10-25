using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private Size? _smokeRequestedViewport;
    private string _smokeLayoutPhase = "startup";

    private async Task RunSmokeTestAsync()
    {
        var guide = PrepareUserGuide();
        if (!guide.UseShellExecute || guide.FileName != Path.Combine(AppContext.BaseDirectory, "UserGuide", OperatingSystem.IsWindows() ? "SoundPreviewer.chm" : "index.md"))
            throw new InvalidOperationException("操作说明入口未指向随程序附带的 CHM 或 Markdown 手册。");
        File.WriteAllText(Path.Combine(App.DataDirectory, "ui-checkpoint.txt"), "appearance");
        // Exercises the same controller, bindings and native playback on both desktop targets.
        bool appearancePersistenceChecked = await CheckAppearanceAsync();
        File.WriteAllText(Path.Combine(App.DataDirectory, "ui-checkpoint.txt"), "filters");
        if (ForceGeneration.IsChecked == true || ShowDuplicates.IsChecked == true)
            throw new InvalidOperationException("全量生成与重复项必须默认关闭。");
        if (SourceSynthChoice.SelectedIndex != 0 || SourceChoices[0].Value is not null ||
            PluginDirectoryGrid.ItemsSource?.Cast<object>().Count() != PluginDirectories.Count ||
            DiscoveredPluginGrid.ItemsSource?.Cast<object>().Count() != DiscoveredPlugins.Count)
            throw new InvalidOperationException("设置列表绑定或目录自动识别默认值错误。");
        CheckPluginScanDefaults();
        CheckLibraryFilterSelections();
        await CheckFilterCountsAsync();
        CheckPreviewStatusGroups();
        CheckDirectoryConfiguration();
        if (!_categoryService.Load().Success || !File.Exists(_categoryService.RulesPath) || string.IsNullOrWhiteSpace(RulesEditor.Text))
            throw new InvalidOperationException("分类规则未能从已有配置或程序内置默认值加载。");
        var progressSample = new QueueProgress(7, 20, "", "", null)
        {
            Generated = 2, Reused = 1, Failed = 1, Unconfigured = 2, Unavailable = 1, RetryLimited = 1,
            ActiveCount = 1, MaxConcurrency = 8, AllowedConcurrency = 3, EffectiveConcurrency = 1,
            MemoryBudgetBytes = RenderResourcePolicy.MemoryLimitBytes, WorkingSetBytes = 536870912
        };
        if (!FormatRenderProgress(progressSample).Contains("成功 3（新生成 2 / 复用 1） · 失败 1", StringComparison.Ordinal) ||
            !FormatRenderProgress(progressSample).Contains("未选择生成方式 2", StringComparison.Ordinal) ||
            !FormatRenderProgress(progressSample).Contains("暂不可用 1", StringComparison.Ordinal) ||
            !FormatRenderProgress(progressSample).Contains("运行 1/1", StringComparison.Ordinal) ||
            !FormatRenderResources(progressSample).Contains("当前并行上限 1 条（设定 8，内存允许 3）", StringComparison.Ordinal) ||
            !FormatRenderResources(progressSample).Contains("实占 512 / 4096 MiB", StringComparison.Ordinal))
            throw new InvalidOperationException("实时成功失败计数或有效并行分母显示不一致。");
        if (App.TestInstalledStorage && (!ApplicationPaths.SamePath(App.DataDirectory, ApplicationPaths.DefaultPreviewDirectory) ||
            !ApplicationPaths.SamePath(_settings.PreviewDirectory, App.DataDirectory) || !PreviewDirectoryBox.IsReadOnly))
            throw new InvalidOperationException("安装版与 Portable 的音色库及生成目录没有固定在程序旁 Preview。");
        bool nativeClipboardChecked = await CheckPresetCopyAsync();
        if (PresetsView.Cast<PresetRow>().Any(row => row.Record.DuplicateOfId.Length > 0))
            throw new InvalidOperationException("重复音色未默认隐藏。");
        ShowDuplicates.IsChecked = true;
        if (PresetsView.Count != Presets.Count) throw new InvalidOperationException("显示重复项未恢复完整索引。");
        ShowDuplicates.IsChecked = false;
        if (!SourceChoices.Any(choice => choice.Value == SynthKind.LogicPatch) ||
            !SynthChoices.Any(choice => choice.Value == SynthKind.LogicPatch) ||
            !BackendChoices.Any(choice => choice.Value == RenderBackend.LogicPatch))
            throw new InvalidOperationException("Logic Patch 未出现在音源或生成方式选项中。");
        foreach (var synth in Enum.GetValues<SynthKind>())
        {
            var filter = FilterGroups.Single(group => group.Key == "synth").Options.Single(choice => choice.Key == synth.ToString());
            if (filter.Label != Labels.Synth(synth) || filter.ToolTip != $"{Labels.Synth(synth)} ({Labels.PresetExtensions(synth)})")
                throw new InvalidOperationException("音源类型与文件后缀标签不一致。");
        }
        var first = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0);
        if (first is not null)
        {
            ClearFilterSelections();
            SetFilterSelection("synth", first.Record.Synth.ToString());
            SetFilterSelection("source", first.Record.SourceId.Length == 0 ? MissingFilter : first.Record.SourceId);
            SetFilterSelection("package", first.Package.Length == 0 ? MissingFilter : first.Package);
            SetFilterSelection("category", first.Record.CategoryTags.FirstOrDefault() ?? MissingFilter);
            SetFilterSelection("favorite", first.Record.Favorite ? "1" : "0");
            SetFilterSelection("status", Labels.PreviewStatusKey(first.Record, first.HasPreview));
            SearchBox.Text = first.Name;
            PresetsView.Refresh();
            if (!PresetsView.Cast<PresetRow>().Contains(first)) throw new InvalidOperationException("组合筛选遗漏匹配音色。");
            SetFilterSelection("favorite", first.Record.Favorite ? "0" : "1");
            if (PresetsView.Cast<PresetRow>().Contains(first)) throw new InvalidOperationException("收藏筛选没有排除音色。");
            ClearFilterSelections();
        }
        var menu = new ContextMenu();
        ColumnMenu_Opened(menu, new RoutedEventArgs());
        if (!PresetGrid.Columns.Where(value => value.IsVisible).OrderBy(value => value.DisplayIndex)
            .Select(value => value.Header as string).SequenceEqual(new[] { "音色名称", "音源", "类别", "状态", "收藏" }))
            throw new InvalidOperationException("音色列表默认可见列及顺序发生变化。");
        if (menu.Items.Count != PresetGrid.Columns.Count) throw new InvalidOperationException("列选择菜单与表格列不一致。");
        var column = PresetGrid.Columns.First(value => value.IsVisible);
        var item = menu.Items.OfType<MenuItem>().First(value => Equals(value.Header, column.Header));
        item.IsChecked = false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (column.IsVisible) throw new InvalidOperationException("列选择没有隐藏主表列。");
        item.IsChecked = true;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        foreach (var order in Enum.GetValues<PreviewNoteOrder>())
        {
            var template = new PreviewTemplate { NoteOrder = order };
            var record = new PresetRecord
            {
                TemplateJson = JsonSerializer.Serialize(template, LibraryJson.Options),
                Notes = template.Pitches.Select((pitch, index) => new NotePreview { MidiNote = pitch, StartSeconds = index * .5, DurationSeconds = .5 }).ToList()
            };
            foreach (var (region, pitch) in new[] { ("Middle", 60), ("High", 84), ("Low", 36) })
                if (FindPreviewNote(record, region)?.MidiNote != pitch) throw new InvalidOperationException("新旧音区顺序的试听按钮映射错误。");
        }
        var playbackChecked = false;
        PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0 && row.HasPreview) ?? first;
        await LoadSelectedPreview();
        if (_mediaOpened)
        {
            _player.Volume = 0;
            _player.Play(0);
            await Task.Delay(250);
            if (_player.PositionSeconds <= 0) throw new InvalidOperationException("原生音频播放位置未前进。");
            _player.Pause();
            var paused = _player.PositionSeconds;
            await Task.Delay(100);
            if (Math.Abs(_player.PositionSeconds - paused) > .05) throw new InvalidOperationException("原生音频暂停检查失败。");
            _player.Seek(Math.Min(1, _player.DurationSeconds / 2));
            _player.Resume();
            await Task.Delay(100);
            _player.Stop();
            _player.Volume = _settings.PlaybackVolume;
            playbackChecked = true;
        }
        bool shortcutPlaybackChecked = await CheckPreviewShortcutAsync();
        var stopChecked = false;
        await RunTask(async token =>
        {
            await SettleLayoutAsync();
            if (!CanStopTask || !StopTaskButton.IsEnabled || !TaskStatusBand.IsVisible || !GenerationProgressBar.IsIndeterminate)
                throw new InvalidOperationException("任务准备阶段未显示进度和停止按钮。");
            var elapsed = TaskElapsed;
            await Task.Delay(1100, token);
            if (TaskElapsed == elapsed) throw new InvalidOperationException("任务用时未更新。");
            CaptureSmokeImage("ui-generation-preparing.png");
            StopTask_Click(StopTaskButton, new RoutedEventArgs());
            if (CanStopTask) throw new InvalidOperationException("停止请求未及时更新。");
            stopChecked = true;
            token.ThrowIfCancellationRequested();
        });
        if (!stopChecked || !IsIdle || CanStopTask || !StatusMessage.StartsWith("已停止", StringComparison.Ordinal))
            throw new InvalidOperationException("停止后任务状态未恢复。");
        bool generatedViewChecked = false, generationFeedbackChecked = false;
        var preview = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0 && row.HasPreview);
        if (preview is not null)
        {
            SearchBox.Text = "TEST ONLY: no search match";
            ShowGeneratedPreviews();
            if (!FilterGroups.Single(group => group.Key == "status").Options.Single(option => option.Key == "generated").IsSelected ||
                SearchBox.Text?.Length > 0 || !PresetsView.Cast<PresetRow>().Contains(preview) || !CanPlay)
                throw new InvalidOperationException("已生成视图没有清除旧筛选或选择预览。");
            generatedViewChecked = true;
            ClearFilterSelections();
        }
        var unconfigured = _settings.Plugins.FirstOrDefault(plugin => plugin.Backend == RenderBackend.Unconfigured);
        if (unconfigured is not null)
        {
            var pending = Enumerable.Range(0, 10000).Select(index => new PresetRecord { Id = $"TEST-ONLY-progress-{index}", Name = $"TEST ONLY progress {index}", Synth = unconfigured.Synth, Status = PresetStatus.Pending }).ToList();
            string storedBefore = JsonSerializer.Serialize(_store.LoadPresets().OrderBy(record => record.Id), LibraryJson.Options);
            await Generate(pending);
            if (!IsIdle || !TaskStatusBand.IsVisible || GenerationProgressBar.IsIndeterminate || ProgressValue != 100 ||
                TaskTitle != "本次没有生成预览" || !StatusMessage.Contains("未选择生成方式", StringComparison.Ordinal) || !TaskSettingsVisibility ||
                !TaskProgressText.Contains("成功 0（新生成 0 / 复用 0） · 失败 0 · 未选择生成方式 10000", StringComparison.Ordinal) ||
                !TaskSelectionText.Contains("本次所选 10000 条 · 去重 10000 个 · 处理队列 10000 个", StringComparison.Ordinal) ||
                TaskSelectionDetails.Text != TaskSelectionText ||
                JsonSerializer.Serialize(_store.LoadPresets().OrderBy(record => record.Id), LibraryJson.Options) != storedBefore)
                throw new InvalidOperationException("无渲染器的大批量任务没有保留明确结果。");
            generationFeedbackChecked = true;
        }
        var editingChecked = await CheckEditingPersistenceAsync();
        var rendererSettingsChecked = await CheckRendererSettingsIsolationAsync();
        bool filteredGenerationChecked = await CheckFilteredGenerationAsync();
        var compressedPreviewChecked = await CheckCompressedPreviewAsync();
        PluginConfiguration? editedBackendModel = null;
        var originalBackend = RenderBackend.Unconfigured;
        var actualViewports = new List<object>();
        foreach (var (width, height, name) in new[] { (1380d, 900d, "desktop"), (1040d, 700d, "compact") })
        {
            _smokeRequestedViewport = new Size(width, height);
            _smokeLayoutPhase = name + "-format";
            Width = width; Height = height;
            Pages.SelectedIndex = 0;
            SetFilterSelection("synth", SynthKind.Serum2.ToString());
            await SettleLayoutAsync();
            CheckSmokeLayout();
            actualViewports.Add(new { Name = name, RequestedWidth = width, RequestedHeight = height, ActualWidth = ClientSize.Width, ActualHeight = ClientSize.Height });
            WriteSmokeLayoutState($"ui-layout-{name}.json");
            CaptureSmokeImage($"ui-format-{name}.png");
            await CheckStableLibraryLayoutAsync(name);
            ClearFilterSelections();
            for (var page = 0; page < 3; page++)
            {
                _smokeLayoutPhase = $"{name}-page-{page}";
                Pages.SelectedIndex = page;
                await SettleLayoutAsync();
                if (page != 0 && PreviewShortcutHint.IsEffectivelyVisible)
                    throw SmokeLayoutFailure("其他页面显示了仅在音色库生效的空格快捷键提示。");
                if (page == 0)
                {
                    PresetGrid.SelectedItem = Presets.FirstOrDefault(row => row.Record.DuplicateOfId.Length == 0 && PresetSourceUnavailableReason(row.Record).Length == 0);
                    PresetDetailsScroll.Offset = default;
                    await SettleLayoutAsync();
                    if (Selected is not null && (!CanLocatePreset || !CanCopyPresetPath || !LocatePresetButton.IsEffectivelyEnabled || !CopyPresetPathButton.IsEffectivelyEnabled))
                        throw SmokeLayoutFailure("音色的定位或复制路径入口未启用。");
                    CopyPresetPathButton.BringIntoView();
                    await SettleLayoutAsync();
                    if (!SmokeBounds(PresetDetailsScroll).Contains(SmokeBounds(CopyPresetPathButton)) ||
                        !SmokeBounds(PresetDetailsScroll).Contains(SmokeBounds(LocatePresetButton)))
                        throw SmokeLayoutFailure("音色定位或复制路径入口不能滚动到详情可视区域。");
                }
                CaptureSmokeImage($"ui-{name}-{page}.png");
            }
            foreach (var (tab, suffix) in new[] { (DirectorySettingsTab, "directories"), (DiscoveredSettingsTab, "discovered"), (RenderSettingsTab, "renderers"), (StorageSettingsTab, "storage"), (ClassificationSettingsTab, "categories") })
            {
                _smokeLayoutPhase = $"{name}-settings-{suffix}";
                Pages.SelectedItem = SettingsTab;
                SettingsPages.SelectedItem = tab;
                await SettleLayoutAsync();
                if (tab == RenderSettingsTab)
                {
                    var backends = PluginGrid.GetVisualDescendants().OfType<ComboBox>()
                        .Where(combo => combo.IsEffectivelyVisible)
                        .DistinctBy(combo => (combo.DataContext as PluginConfiguration)?.Synth).ToList();
                    await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, $"ui-backends-{name}.json"), JsonSerializer.Serialize(backends.Select(combo => new
                    {
                        Synth = (combo.DataContext as PluginConfiguration)?.Synth.ToString(),
                        ModelBackend = (combo.DataContext as PluginConfiguration)?.Backend.ToString(),
                        SelectedItem = combo.SelectedItem?.ToString(), combo.SelectedIndex, combo.ItemCount
                    })));
                    if (backends.Count != Plugins.Count || backends.Any(combo => combo.DataContext is not PluginConfiguration plugin ||
                        combo.SelectedItem is not RenderBackend selected || selected != plugin.Backend || combo.ItemCount != Enum.GetValues<RenderBackend>().Length))
                        throw new InvalidOperationException("渲染器方式列表未显示全部音源的当前设置。");
                    var editable = backends.First(combo => combo.DataContext is PluginConfiguration { Synth: SynthKind.Serum1 });
                    var model = (PluginConfiguration)editable.DataContext!;
                    if (name == "desktop")
                    {
                        originalBackend = model.Backend;
                        editedBackendModel = model;
                        editable.SelectedIndex = (int)RenderBackend.ExternalWorker;
                        await SettleLayoutAsync();
                    }
                    if (model.Backend != RenderBackend.ExternalWorker || editable.SelectedItem is not RenderBackend.ExternalWorker)
                        throw new InvalidOperationException("渲染器方式编辑未写回设置或在页面及窗口切换后丢失。");
                }
                if (tab == DirectorySettingsTab) CheckScanButtonVisible(DirectoryScanPluginsButton);
                if (tab == DiscoveredSettingsTab) CheckScanButtonVisible(DiscoveredScanPluginsButton);
                CaptureSmokeImage($"ui-settings-{suffix}-{name}.png");
            }
            SettingsPages.SelectedItem = StorageSettingsTab;
            var quick = QuickPreviewMode.IsChecked;
            QuickPreviewMode.IsChecked = true;
            await SettleLayoutAsync();
            if (HoldBox.IsEffectivelyEnabled || TailBox.IsEffectivelyEnabled) throw new InvalidOperationException("快速预览未禁用固定时长输入。");
            QuickPreviewMode.IsChecked = false;
            await SettleLayoutAsync();
            if (!HoldBox.IsEffectivelyEnabled || !TailBox.IsEffectivelyEnabled) throw new InvalidOperationException("固定时长输入未恢复。");
            QuickPreviewMode.IsChecked = quick;
        }
        if (editedBackendModel is not null)
        {
            editedBackendModel.Backend = originalBackend;
            RefreshGrid(PluginGrid);
        }
        await CheckLanguagesAsync();
        await CheckClosingCallbacksAsync();
        await File.WriteAllTextAsync(Path.Combine(App.DataDirectory, "ui-smoke.json"), JsonSerializer.Serialize(new
        {
            Loaded = true, UserGuideChecked = true, Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            AppearanceChecked = true, AppearancePersistenceChecked = appearancePersistenceChecked, AppearanceCancelChecked = true, AppearanceViewports = 2,
            FilterOptionCountsChecked = true,
            FilterCountsBenchmarkRows = 14000, FilterCountsBenchmarkMilliseconds = _filterCountsBenchmarkMilliseconds,
            FilterCountsBenchmarkAllocatedBytes = _filterCountsBenchmarkAllocatedBytes,
            Presets = Presets.Count, PlaybackChecked = playbackChecked, FormatsChecked = true, FormatViewports = actualViewports.Count, ActualViewports = actualViewports,
            PreviewShortcutChecked = true, PreviewShortcutPlaybackChecked = shortcutPlaybackChecked, PreviewShortcutHintViewports = 2,
            MixedDirectoryChecked = true, UnidentifiedChecked = Presets.Any(row => row.Record.Status == PresetStatus.Unidentified),
            PluginDirectoriesChecked = true, PluginScanDefaultsChecked = true, PluginScanControlsViewports = 2,
            ClosingCallbacksChecked = true,
            TemplatePersistenceChecked = editingChecked.Template, MetadataPersistenceChecked = editingChecked.Metadata,
            DataLibraryViewports = editingChecked.Metadata ? 2 : 0,
            RendererSettingsChecked = rendererSettingsChecked,
            CompressedPreviewChecked = compressedPreviewChecked.Checked,
            CompressedPreviewPlaybackChecked = compressedPreviewChecked.PlaybackChecked,
            CurrentFilterGenerationChecked = filteredGenerationChecked,
            RendererUnavailableReasonsChecked = filteredGenerationChecked,
            PresetFileTransferChecked = true, ClipboardPathChecked = true, NativeClipboardChecked = nativeClipboardChecked,
            GenerationResultCopyChecked = true, SelectableGenerationDescriptionChecked = true,
            SourceRevealCommandChecked = true, SourceCountsChecked = true,
            CrossSourceDuplicatesChecked = true, MultiSelectFiltersChecked = true, FilterRefreshPreservedChecked = true,
            StrictDuplicateHidingChecked = filteredGenerationChecked,
            FivePreviewStatusesChecked = true, DirectoryConfigurationChecked = true,
            SerumLoadFailureMessagesChecked = true,
            CategoryRulesAvailableChecked = true, CategoryRulesPath = _categoryService.RulesPath,
            LanguagesChecked = true, EnglishViewports = 2, MetadataLanguageIndependentChecked = true,
            MetadataAliasesSearchChecked = true, LargeMetadataListChecked = true,
            StartupWarnings = App.StorageStartupMessages,
            StoragePreparationWindowShown = App.StoragePreparationWindowShown,
            RenderProgressCountersChecked = true, EffectiveConcurrencyDisplayChecked = true,
            QueueSelectionCountsChecked = true, ProgressLayoutStableChecked = true,
            LibrarySpaceChecked = true, SingleRowFiltersChecked = true, FixedFilterOptionsChecked = true, PlaybackProgressDockChecked = true,
            DirectoryConfigurationPath = DirectoryConfigurationLocation, DirectoryPluginDirectories = _settings.PluginDirectories,
            InstalledStorageChecked = App.TestInstalledStorage, LibraryDirectory = App.DataDirectory, PreviewDirectory = _settings.PreviewDirectory,
            ExistingPreviewsLocal = Presets.Where(row => row.Record.PreviewPath.Length > 0).All(row => File.Exists(row.Record.PreviewPath) && ApplicationPaths.IsWithin(row.Record.PreviewPath, App.DataDirectory)),
            StoredPreviewRecords = Presets.Count(row => row.Record.PreviewPath.Length > 0),
            DiscoveredPlugins = DiscoveredPlugins.Count, StopChecked = stopChecked,
            GenerationFeedbackChecked = generationFeedbackChecked, GenerationFeedbackItems = 10000, GeneratedViewChecked = generatedViewChecked,
            DuplicateFilterChecked = true, DuplicateRecords = Presets.Count(row => row.Record.DuplicateOfId.Length > 0),
            ForceDefaultOff = ForceGeneration.IsChecked != true, Width = Bounds.Width, Height = Bounds.Height
        }));
        App.Shutdown();
    }

    private void CheckLibraryFilterSelections()
    {
        var savedSelections = FilterGroups.ToDictionary(group => group.Key,
            group => group.Options.Where(option => option.IsSelected && option.Key.Length > 0).Select(option => option.Key).ToArray());
        string originalSearch = SearchBox.Text ?? "";
        bool? originalDuplicates = ShowDuplicates.IsChecked;
        string sourceId = "TEST-ONLY-source-counts-" + Guid.NewGuid().ToString("N");
        string secondSourceId = sourceId + "-other";
        string path = Path.Combine(App.DataDirectory, "TEST ONLY count source", "preset.vital");
        var canonical = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), Name = "TEST ONLY canonical",
            SourceId = sourceId, SourcePath = path, Synth = SynthKind.Vital, Favorite = true,
            Package = sourceId, Tags = ["TEST ONLY warm"], CategoryTags = ["TEST ONLY bass"] });
        var duplicate = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), Name = "TEST ONLY local duplicate",
            SourceId = sourceId, SourcePath = path, Synth = SynthKind.Vital, DuplicateOfId = canonical.Record.Id,
            Package = sourceId, Tags = ["TEST ONLY warm"], CategoryTags = ["TEST ONLY bass"] });
        var added = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), Name = "TEST ONLY added",
            SourceId = sourceId, SourcePath = path, Synth = SynthKind.Serum1, Favorite = true,
            Package = secondSourceId, Tags = ["TEST ONLY bright"], CategoryTags = ["TEST ONLY lead"] });
        var crossPath = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), SourceId = secondSourceId,
            Name = "TEST ONLY cross source", Synth = SynthKind.Vital, Package = secondSourceId,
            Tags = ["TEST ONLY bright", "TEST ONLY copy tag"], CategoryTags = ["TEST ONLY lead"],
            SourcePath = Path.Combine(App.DataDirectory, "TEST ONLY other source", "copy.vital"), DuplicateOfId = canonical.Record.Id });
        try
        {
            ClearFilterSelections();
            SearchBox.Clear();
            ShowDuplicates.IsChecked = false;
            Presets.Add(canonical);
            Presets.Add(duplicate);
            RefreshFilterCollections();
            var choice = SourceOption(sourceId);
            if (choice.Label != Path.GetDirectoryName(path) + "（2 个音色，去重 1 个，重复副本 1 个）" ||
                choice.Count != 1 || choice.CountLabel != "1" || choice.ToolTip != choice.Label)
                throw new InvalidOperationException("来源选项未显示对应路径的音色总量和重复副本数量。");
            SetFilterSelection("source", sourceId);
            if (PresetsView.Count != 1) throw new InvalidOperationException("来源计数改变了默认重复项筛选。");
            ShowDuplicates.IsChecked = true;
            if (PresetsView.Count != 2 || SourceOption(sourceId).Label != choice.Label)
                throw new InvalidOperationException("显示重复项应恢复副本，来源扫描总量不应变化。");
            ShowDuplicates.IsChecked = false;
            SearchBox.Text = "TEST ONLY no source count match";
            PresetsView.Refresh();
            if (PresetsView.Count != 0 || SourceOption(sourceId).Label != choice.Label)
                throw new InvalidOperationException("搜索筛选不应改变来源路径的扫描总量。");
            SearchBox.Clear();
            Presets.Add(added);
            RefreshFilterCollections();
            if (!SourceOption(sourceId).IsSelected ||
                SourceOption(sourceId).Label != Path.GetDirectoryName(path) + "（3 个音色，去重 2 个，重复副本 1 个）")
                throw new InvalidOperationException("来源计数刷新后未保留所选来源 ID 或新增音色数量。");
            Presets.Add(crossPath);
            RefreshFilterCollections();
            if (!SourceOption(sourceId).Label.Contains("跨来源相同 2 个", StringComparison.Ordinal) ||
                !SourceOption(secondSourceId).Label.Contains("1 个音色，去重 1 个，跨来源相同 1 个", StringComparison.Ordinal))
                throw new InvalidOperationException("跨路径重复统计遗漏了主项或其他来源副本。");
            SetFilterSelection("source", secondSourceId);
            AssertCopiesHiddenUntilEnabled(crossPath);
            if (!SourceOption("").Label.StartsWith($"全部来源（{Presets.Count} 个音色，去重 ", StringComparison.Ordinal))
                throw new InvalidOperationException("全部来源的音色总量不正确。");

            SetFilterSelection("source", sourceId, secondSourceId);
            canonical.Record.Status = PresetStatus.Failed;
            canonical.Refresh();
            SetFilterSelection("synth", SynthKind.Vital.ToString());
            SetFilterSelection("status", "pending");
            AssertCopiesHiddenUntilEnabled(duplicate, crossPath);
            SetFilterSelection("status");
            SearchBox.Text = crossPath.Name;
            PresetsView.Refresh();
            AssertCopiesHiddenUntilEnabled(crossPath);
            SearchBox.Clear();
            SetFilterSelection("tag", "TEST ONLY copy tag");
            AssertCopiesHiddenUntilEnabled(crossPath);
            SetFilterSelection("tag");
            SetFilterSelection("favorite", "0");
            AssertCopiesHiddenUntilEnabled(duplicate, crossPath);
            ClearFilterSelections();

            SetFilterSelection("source", sourceId, secondSourceId);
            AssertVisible(canonical, added);
            ShowDuplicates.IsChecked = true;
            AssertVisible(canonical, duplicate, added, crossPath);
            ShowDuplicates.IsChecked = false;
            SetFilterSelection("synth", SynthKind.Vital.ToString(), SynthKind.Serum1.ToString());
            SetFilterSelection("package", sourceId, secondSourceId);
            SetFilterSelection("category", "TEST ONLY bass", "TEST ONLY lead");
            SetFilterSelection("tag", "TEST ONLY warm", "TEST ONLY bright");
            SetFilterSelection("favorite", "1");
            AssertVisible(canonical, added);
            SetFilterSelection("category", "TEST ONLY lead");
            AssertVisible(added);
            SetFilterSelection("favorite", "0");
            AssertCopiesHiddenUntilEnabled(crossPath);
            var beforeRefresh = FilterGroups.ToDictionary(group => group.Key,
                group => group.Options.Where(option => option.IsSelected && option.Key.Length > 0).Select(option => option.Key).Order().ToArray());
            RefreshFilterCollections();
            foreach (var group in FilterGroups)
                if (!beforeRefresh[group.Key].SequenceEqual(group.Options.Where(option => option.IsSelected && option.Key.Length > 0).Select(option => option.Key).Order()))
                    throw new InvalidOperationException("刷新选项丢失了某组的多个选择。");
            AssertVisible();
            SetFilterSelection("source", "");
            if (FilterGroups.Single(group => group.Key == "source").Options.Any(option => option.Key.Length > 0 && option.IsSelected) ||
                !SourceOption("").IsSelected || !FilterGroups.Single(group => group.Key == "category").Options.Single(option => option.Key == "TEST ONLY lead").IsSelected)
                throw new InvalidOperationException("全部选项没有清空当前组，或错误清除了其他组。");
            ClearFilterSelections();
            if (FilterGroups.Any(group => group.Options.Any(option => option.Key.Length > 0 && option.IsSelected) || !group.Options.Single(option => option.Key.Length == 0).IsSelected))
                throw new InvalidOperationException("清除筛选没有恢复各组的全部选项。");
        }
        finally
        {
            PresetGrid.SelectedItem = null;
            ClearFilterSelections();
            SearchBox.Clear();
            ShowDuplicates.IsChecked = false;
            // Reset currency before removing selected fixtures from a filtered Avalonia view.
            PresetsView.Filter = null;
            PresetsView.MoveCurrentToPosition(-1);
            try
            {
                Presets.Remove(canonical);
                Presets.Remove(duplicate);
                Presets.Remove(added);
                Presets.Remove(crossPath);
                RefreshFilterCollections();
            }
            finally { PresetsView.Filter = FilterPreset; }
            foreach (var (key, values) in savedSelections) SetFilterSelection(key, values);
            SearchBox.Text = originalSearch;
            ShowDuplicates.IsChecked = originalDuplicates;
            PresetsView.Refresh();
        }

        FilterOption SourceOption(string key) => FilterGroups.Single(group => group.Key == "source").Options.Single(option => option.Key == key);
        void AssertVisible(params PresetRow[] expected)
        {
            if (!PresetsView.Cast<PresetRow>().ToHashSet().SetEquals(expected))
                throw new InvalidOperationException("同组多选、跨组交集或全局重复项隐藏不正确。");
        }
        void AssertCopiesHiddenUntilEnabled(params PresetRow[] copies)
        {
            ShowDuplicates.IsChecked = false;
            AssertVisible();
            if (_duplicateView.ResolveCanonicalSelection(PresetsView.Cast<PresetRow>().Select(row => row.Record)).Count != 0)
                throw new InvalidOperationException("关闭重复项后，空筛选仍将隐藏副本重定向至其他来源主项。");
            ShowDuplicates.IsChecked = true;
            AssertVisible(copies);
            if (_duplicateView.ResolveCanonicalSelection(PresetsView.Cast<PresetRow>().Select(row => row.Record)).Single() != canonical.Record)
                throw new InvalidOperationException("开启重复项后，同音色的跨路径副本没有归并至同一个全局主项。");
            PresetGrid.SelectedItem = copies[0];
            if (!CanRenderSelection) throw new InvalidOperationException("显式显示的副本无法通过主项重新生成。");
            ShowDuplicates.IsChecked = false;
            AssertVisible();
            if (PresetGrid.SelectedItem is not null || Selected is not null || CanRenderSelection)
                throw new InvalidOperationException("隐藏已选副本后，表格选择、详情或生成所选能力仍保留了隐藏音色。");
        }
    }

    private void CheckPreviewStatusGroups()
    {
        if (!Enum.GetValues<PresetStatus>().Select(value => (int)value).SequenceEqual(Enumerable.Range(0, 8)) ||
            Enum.GetValues<PresetStatus>().Select(Labels.Status).Distinct().Count() != 5)
            throw new InvalidOperationException("状态显示归并改变了数据库枚举，或静态标签未收敛为五类。");
        string directory = Path.Combine(App.DataDirectory, "TEST-ONLY-status-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string preview = Path.Combine(directory, "existing-preview.wav");
        File.WriteAllText(preview, "TEST ONLY existence sentinel; never played.");
        var rows = new List<PresetRow>();
        try
        {
            ClearFilterSelections();
            SearchBox.Text = directory;
            foreach (var status in Enum.GetValues<PresetStatus>())
                foreach (bool hasPreview in new[] { false, true })
                {
                    var row = new PresetRow(new PresetRecord { Id = Guid.NewGuid().ToString("N"), Name = status.ToString(), Status = status,
                        SourcePath = Path.Combine(directory, status + ".vital"), PreviewPath = hasPreview ? preview : "",
                        Message = "TEST ONLY detailed reason", LastRenderMessage = "TEST ONLY latest render detail" });
                    string expectedKey = status == PresetStatus.Rendering ? "rendering" : hasPreview ? "generated" : status switch
                    {
                        PresetStatus.Pending or PresetStatus.Ready or PresetStatus.NeedsReview => "pending",
                        PresetStatus.Failed => "failed", _ => "unavailable"
                    };
                    string expectedLabel = expectedKey switch { "rendering" => "生成中", "generated" => "已生成", "pending" => "待生成", "failed" => "生成失败", _ => "无法预览" };
                    if (Labels.PreviewStatusKey(row.Record, row.HasPreview) != expectedKey || row.StatusLabel != expectedLabel ||
                        !row.Message.Contains("TEST ONLY detailed reason", StringComparison.Ordinal) || row.LastRenderMessageLabel != "TEST ONLY latest render detail")
                        throw new InvalidOperationException("状态没有归并为五类，或丢失了原始原因与确认信息。");
                    rows.Add(row);
                    Presets.Add(row);
                }
            RefreshFilterCollections();
            foreach (string key in new[] { "pending", "rendering", "generated", "failed", "unavailable" })
            {
                SetFilterSelection("status", key);
                if (!PresetsView.Cast<PresetRow>().ToHashSet().SetEquals(rows.Where(row => Labels.PreviewStatusKey(row.Record, row.HasPreview) == key)))
                    throw new InvalidOperationException("状态筛选与列表显示的五类状态不一致。");
            }
            SetFilterSelection("status", "generated", "failed");
            if (!PresetsView.Cast<PresetRow>().ToHashSet().SetEquals(rows.Where(row => Labels.PreviewStatusKey(row.Record, row.HasPreview) is "generated" or "failed")))
                throw new InvalidOperationException("已生成和失败状态不能同时选择。");
            var generated = rows.First(row => row.Record.Status == PresetStatus.NeedsReview && row.HasPreview);
            generated.Record.DuplicateOfId = "TEST ONLY canonical";
            if (generated.StatusLabel != "已生成" || generated.DuplicateLabel.Length == 0)
                throw new InvalidOperationException("重复副本再次成为独立状态，或丢失了重复详情。");
            File.Delete(preview);
            generated.Refresh();
            if (generated.HasPreview || generated.StatusLabel != "待生成" || !generated.Message.StartsWith("原预览文件不存在，可重新生成。", StringComparison.Ordinal))
                throw new InvalidOperationException("缺失缓存仍被显示为已生成。");
            foreach (var (suffix, oldReason, newReason) in new[]
            {
                (".vitalbank", "尚未验证原生音色包导入及依赖资源", "重新扫描音色"),
                (".fxb", "银行加载与槽位尚未验证", "独立 .fxp")
            })
            {
                var record = new PresetRecord { Status = PresetStatus.Unsupported, SourcePath = Path.Combine(directory, "legacy" + suffix), Message = oldReason };
                var row = new PresetRow(record);
                if (!row.Message.Contains(newReason, StringComparison.Ordinal) || record.Message != oldReason)
                    throw new InvalidOperationException("旧音色包原因没有提供可操作提示，或修改了已存原因。");
                record.Message = "TEST ONLY custom failure";
                if (row.Message != record.Message) throw new InvalidOperationException("友好提示覆盖了自定义失败原因。");
            }
            foreach (var status in new[] { PresetStatus.Unsupported, PresetStatus.Missing, PresetStatus.Unidentified })
                if (new PresetRow(new PresetRecord { Status = status }).Message.Length == 0)
                    throw new InvalidOperationException("无法预览记录缺少可操作的原因提示。");
            foreach (var synth in new[] { SynthKind.Serum1, SynthKind.Serum2 })
            {
                string oldReason = InitBaselineService.RejectionPrefix + "TEST ONLY comparison evidence";
                var record = new PresetRecord { Synth = synth, Status = PresetStatus.Failed, Message = oldReason, LastRenderMessage = oldReason };
                var row = new PresetRow(record);
                if (!row.Message.StartsWith(InitBaselineService.SuspectedLoadFailurePrefix, StringComparison.Ordinal) ||
                    row.LastRenderMessageLabel != row.Message || !row.Message.Contains("TEST ONLY comparison evidence", StringComparison.Ordinal) ||
                    record.Message != oldReason || record.LastRenderMessage != oldReason || !PresetResultText(row).Contains(row.LastRenderMessageLabel, StringComparison.Ordinal))
                    throw new InvalidOperationException("Serum 历史初始化匹配原因未显示或复制为疑似加载失败，或改写了已存记录。");
            }
        }
        finally
        {
            ClearFilterSelections();
            SearchBox.Clear();
            PresetGrid.SelectedItem = null;
            PresetsView.Filter = null;
            PresetsView.MoveCurrentToPosition(-1);
            try
            {
                foreach (var row in rows) Presets.Remove(row);
                RefreshFilterCollections();
            }
            finally { PresetsView.Filter = FilterPreset; }
            PresetsView.Refresh();
            File.Delete(preview);
            Directory.Delete(directory);
        }
    }

    private void CheckDirectoryConfiguration()
    {
        string expectedRoot = App.TestInstalledStorage ? ApplicationPaths.PortableApplicationDirectory : App.DataDirectory;
        if (!ApplicationPaths.SamePath(DirectoryConfigurationLocation, Path.Combine(expectedRoot, "Config", "directories.json")) ||
            !File.Exists(DirectoryConfigurationLocation))
            throw new InvalidOperationException("目录 JSON 没有保存在要求的程序根目录。");
        using var document = JsonDocument.Parse(File.ReadAllText(DirectoryConfigurationLocation),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (!root.GetProperty("Sources").EnumerateArray().Select(source => source.GetProperty("Id").GetString()).SequenceEqual(_settings.Sources.Select(source => source.Id)) ||
            !root.GetProperty("PluginDirectories").EnumerateArray().Select(directory => directory.GetString()).SequenceEqual(_settings.PluginDirectories) ||
            !DirectoryConfigurationHint.Contains(DirectoryConfigurationLocation, StringComparison.Ordinal))
            throw new InvalidOperationException("目录 JSON 与启动时目录设置或界面位置提示不一致。");
    }

    private async Task CheckClosingCallbacksAsync()
    {
        var preview = Presets.FirstOrDefault(row => row.HasPreview);
        PresetGrid.SelectedItem = preview;
        var pendingLoad = LoadSelectedPreview();
        // Use the real teardown path while an audio read may still be awaiting its UI continuation.
        CompleteWindowClose();
        int generationAfterClose = _selectionGeneration;
        PresetGrid.SelectedItem = null;
        PresetGrid.SelectedItem = preview;
        await pendingLoad;
        await LoadSelectedPreview();
        StopPlayback();
        UpdatePlayback();
        Play_Click(ScanPluginsButton, new RoutedEventArgs());
        StartPlayback(0, null);
        var volume = VolumeSlider.Value;
        VolumeSlider.Value = volume == 1 ? .5 : 1;
        VolumeSlider.Value = volume;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (!_isClosing || _mediaOpened || _isPlaying || _playWhenOpen || _loadedPreviewPath.Length > 0 ||
            _selectionGeneration != generationAfterClose)
            throw new InvalidOperationException("关闭窗口后的选择、音频加载或播放回调重新启用了播放器。");
    }

    private async Task SettleLayoutAsync()
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var previousClient = ClientSize;
        int stableSamples = 0;
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(50);
            var client = ClientSize;
            stableSamples = client == previousClient && client.Width > 0 && client.Height > 0 ? stableSamples + 1 : 0;
            previousClient = client;
            if (stableSamples < 3) continue;

            // Native desktops can reject a requested size without another resize notification.
            // Keep subsequent invalidated measurements at the actual client size, including long text.
            Width = client.Width;
            Height = client.Height;
            RefreshClientLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var content = (Control)Content!;
            if (ClientSize == client && content.IsMeasureValid && content.IsArrangeValid &&
                Math.Abs(content.Bounds.Width - client.Width) <= .5 && Math.Abs(content.Bounds.Height - client.Height) <= .5)
                return;
            stableSamples = 0;
        }
        throw SmokeLayoutFailure("窗口客户区与布局在 3 秒内未稳定。");
    }

    private Rect SmokeBounds(Control control)
    {
        var content = (Control)Content!;
        var point = control.TranslatePoint(default, content) ?? throw new InvalidOperationException("控件不在窗口可视树内。");
        return new Rect(point, control.Bounds.Size);
    }

    private void CheckSmokeLayout()
    {
        var content = (Control)Content!;
        var area = new Rect(content.Bounds.Size);
        CheckPreviewShortcutHint();
        if (!UserGuideButton.IsEffectivelyVisible || !UserGuideButton.IsEnabled ||
            UserGuideButton.Bounds.Width <= 0 || UserGuideButton.Bounds.Height <= 0 ||
            !SmokeBounds(StatusFooter).Contains(SmokeBounds(UserGuideButton)) ||
            SmokeBounds(UserGuideButton).Intersects(SmokeBounds(BuildVersionLabel)) ||
            SmokeBounds(UserGuideButton).Intersects(SmokeBounds(StatusFooterText)))
            throw SmokeLayoutFailure("操作说明入口缺失、不可用或与页脚文字重叠。");
        if (BuildVersionLabel.Text != AppVersion || !BuildVersionLabel.IsEffectivelyVisible ||
            SmokeBounds(BuildVersionLabel).Intersects(SmokeBounds(StatusFooterText)))
            throw SmokeLayoutFailure("版本标识缺失或与状态文字重叠。");
        if (!area.Contains(SmokeBounds(ForceGeneration)) || SmokeBounds(ForceGeneration).Intersects(SmokeBounds(AppBrandName)))
            throw SmokeLayoutFailure("全量覆盖选项不可见或与标题重叠。");
        CheckScanButtonVisible(ScanPluginsButton);
        CheckScanButtonVisible(ScanPresetsButton);
        if (!AppearanceButton.IsEffectivelyVisible || !area.Contains(SmokeBounds(AppearanceButton)) ||
            SmokeBounds(AppearanceButton).Intersects(SmokeBounds(AppBrandName)) ||
            SmokeBounds(AppearanceButton).Intersects(SmokeBounds(ScanPluginsButton)))
            throw SmokeLayoutFailure("配色入口不可见或与标题、扫描按钮重叠。");
        if (SmokeBounds(ScanPluginsButton).Intersects(SmokeBounds(AppBrandName)) ||
            SmokeBounds(ScanPluginsButton).Intersects(SmokeBounds(ScanPresetsButton)) ||
            SmokeBounds(ScanPresetsButton).Intersects(SmokeBounds(ForceGeneration)))
            throw SmokeLayoutFailure("扫描音源、扫描音色与主工具栏重叠。");
        string? formats = PresetFormatHint.Text;
        foreach (var suffix in new[] { ".vital", ".vitalbank", ".fxp", ".SerumPreset", ".fxb" })
            if (formats?.Contains(suffix, StringComparison.Ordinal) != true) throw new InvalidOperationException("文件后缀说明缺失。");
        CheckLibraryWorkspaceGeometry();
        if (EmptyPresetState.IsEffectivelyVisible && SmokeBounds(EmptyPresetState).Bottom > SmokeBounds(PresetGrid).Bottom + 1)
            throw SmokeLayoutFailure("空列表说明溢出音色表格。");
        var copy = SmokeBounds(CopyPresetPathButton);
        var details = SmokeBounds(PresetDetailsScroll);
        if (!CopyPresetPathButton.IsEffectivelyVisible || copy.Width <= 0 || copy.Height < 24 ||
            copy.Left < details.Left - 1 || copy.Right > details.Right + 1)
            throw SmokeLayoutFailure("复制路径入口不可见或横向溢出详情区。");
    }

    private InvalidOperationException SmokeLayoutFailure(string message)
    {
        try
        {
            WriteSmokeLayoutState("ui-layout-error.json");
            CaptureSmokeImage("ui-layout-error.png");
        }
        catch (Exception error) { message += " 布局诊断保存失败：" + error.Message; }
        return new InvalidOperationException(message + $" 阶段：{_smokeLayoutPhase}；客户区：{ClientSize}；内容：{((Control)Content!).Bounds}。");
    }

    private void WriteSmokeLayoutState(string fileName)
    {
        var content = (Control)Content!;
        var screen = Screens.ScreenFromWindow(this);
        Control[] controls = [AppBrandName, AppearanceButton, ForceGeneration, ScanPluginsButton, ScanPresetsButton, BuildVersionLabel,
            StatusFooterText, MainToolbar, StatusFooter, PresetFormatHint, PresetGrid, TaskStatusBand, TaskProgressTitle, TaskProgressMetadata,
            TaskProgressDetails, TaskResourceDetails, GenerationProgressBar, Pages, CopyPresetPathButton, PresetDetailsScroll, LocatePresetButton,
            LibraryWorkspace, LibraryListPane, LibraryDetailsPane, FilterStrip, PlaybackBar, PlayPreviewButton,
            SearchBox, FilterTabs, FilterOptionsBand, FilterOptionsScroll, FilterOptionsItems, RecentLogList,
            PreviewShortcutHint, Waveform, SeekSlider];
        var state = new
        {
            Phase = _smokeLayoutPhase,
            RequestedWidth = _smokeRequestedViewport?.Width, RequestedHeight = _smokeRequestedViewport?.Height,
            WindowWidth = Width, WindowHeight = Height, ClientWidth = ClientSize.Width, ClientHeight = ClientSize.Height,
            ContentBounds = SmokeRect(content.Bounds), RenderScaling,
            ScreenWorkingArea = screen?.WorkingArea.ToString(),
            Controls = controls.Select(control => new
            {
                control.Name, control.IsEffectivelyVisible, control.IsMeasureValid, control.IsArrangeValid,
                Bounds = control.TranslatePoint(default, content) is { } point ? SmokeRect(new Rect(point, control.Bounds.Size)) : null,
                DesiredWidth = control.DesiredSize.Width, DesiredHeight = control.DesiredSize.Height
            }).ToList()
        };
        File.WriteAllText(Path.Combine(App.DataDirectory, fileName), JsonSerializer.Serialize(state, LibraryJson.Options));
    }

    private static object SmokeRect(Rect rect) => new { rect.X, rect.Y, rect.Width, rect.Height };

    private static void CheckPluginScanDefaults()
    {
        // Pure path checks: UI smoke tests must never inspect or associate the user's installed synths.
        var defaults = ResolvePluginScanDirectories([], []);
        if (defaults.Count == 0 || ApplicationPaths.DefaultPluginDirectories.Any(directory => !defaults.Any(value => ApplicationPaths.SamePath(value, directory))))
            throw new InvalidOperationException("首次使用或空目录列表没有恢复平台默认安装位置。");
        var syntheticRoot = Path.Combine(Path.GetTempPath(), "TEST-ONLY-plugin-roots");
        var customRoots = new[] { Path.Combine(syntheticRoot, "unavailable"), Path.Combine(syntheticRoot, "custom") };
        if (!ResolvePluginScanDirectories(customRoots, []).SequenceEqual(customRoots))
            throw new InvalidOperationException("自定义或不可访问的音源安装目录被替换或丢弃。");
        var vital = new PluginConfiguration { Synth = SynthKind.Vital, Backend = RenderBackend.VitalCli,
            ExecutablePath = Path.Combine(syntheticRoot, "Vital.app", "Contents", "MacOS", "Vital") };
        var inferred = ResolvePluginScanDirectories([], [vital]);
        if (!inferred.Any(directory => ApplicationPaths.SamePath(directory, syntheticRoot)) ||
            inferred.Any(directory => directory.EndsWith(Path.Combine("Contents", "MacOS"), StringComparison.Ordinal)))
            throw new InvalidOperationException("已配置的 Vital 应用程序未使用应用程序包所在安装目录。");
        if (vital.Backend != RenderBackend.VitalCli)
            throw new InvalidOperationException("初始化音源目录更改了现有渲染方式。");

        var unavailableRoot = Path.Combine(syntheticRoot, "unavailable");
        var newPlugin = new DiscoveredPlugin { Id = "TEST-ONLY-new", FilePath = Path.Combine(syntheticRoot, "new", "Serum2.vst3") };
        var offlinePlugin = new DiscoveredPlugin { Id = "TEST-ONLY-offline", FilePath = Path.Combine(unavailableRoot, "Vital.app") };
        var otherPlugin = new DiscoveredPlugin { Id = "TEST-ONLY-other", FilePath = Path.Combine(unavailableRoot + "-other", "Vital.app") };
        var scan = new PluginScanResult
        {
            Plugins = [newPlugin], UnavailableRoots = ["", "   ", "\0invalid", unavailableRoot],
            Warnings = ["TEST ONLY: invalid installation root", "TEST ONLY: unavailable installation root"]
        };
        var merged = MergePluginScanResults(scan, [offlinePlugin, otherPlugin]);
        if (merged.Count != 2 || !merged.Contains(newPlugin) || !merged.Contains(offlinePlugin) || merged.Contains(otherPlugin) ||
            scan.Warnings.Count != 2 || scan.UnavailableRoots.Count != 4)
            throw new InvalidOperationException("无效安装目录中断了有效扫描结果，或错误保留了其他目录的音源。");
        if (IsWithinUnavailablePluginRoot(newPlugin.FilePath, "") || IsWithinUnavailablePluginRoot(newPlugin.FilePath, "\0invalid"))
            throw new InvalidOperationException("无效安装目录不能作为恢复旧音源的依据。");
    }

    private void CheckScanButtonVisible(Button button)
    {
        var area = new Rect(((Control)Content!).Bounds.Size);
        var bounds = SmokeBounds(button);
        if (!button.IsEffectivelyVisible || bounds.Width <= 0 || bounds.Height <= 0 || !area.Contains(bounds))
            throw SmokeLayoutFailure($"扫描按钮 {button.Name} 未完整显示。");
    }

    private void CaptureSmokeImage(string name)
    {
        var content = (Control)Content!;
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)content.Bounds.Width), Math.Max(1, (int)content.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(content);
        bitmap.Save(Path.Combine(App.DataDirectory, name));
    }
}
