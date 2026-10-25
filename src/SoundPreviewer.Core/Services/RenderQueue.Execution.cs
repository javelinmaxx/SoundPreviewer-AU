using System.Diagnostics;
using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Core.Services;

public sealed partial class RenderQueue
{
    private sealed class QueueItem(PresetRecord original)
    {
        public PresetRecord Original { get; } = original;
        public PresetRecord Identity { get; } = Clone(original);
        public PresetRecord Record { get; } = Clone(original);
        public PresetRecord? Previous { get; set; }
        public bool AttemptInProgress { get; set; }
    }

    private sealed record RenderJob(string Key, PluginConfiguration Plugin, List<QueueItem> Items);
    private sealed record Reservation(long OutputBytes, long ReplacedBytes, long BudgetBytes, List<OwnedCache> OldCaches);

    private sealed partial class Execution
    {
        private readonly LibraryStore store;
        private readonly AppSettings originalSettings;
        private readonly AppSettings settings;
        private readonly IReadOnlyList<PresetRecord> originals;
        private readonly string workerPath;
        private readonly IProgress<QueueProgress> progress;
        private readonly CancellationToken callerToken;
        private readonly bool force;
        private readonly IReadOnlyDictionary<SynthKind, RenderReadiness> readiness;
        private readonly object gate = new();
        private readonly QueueResult summary;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly Dictionary<SynthKind, Task<PreparedInitBaseline>> baselines = new();
        private readonly Dictionary<RenderJob, Reservation> reservations = new();
        private readonly Dictionary<object, long> scratchReservations = new();
        private readonly Dictionary<string, List<PresetRecord>> storedReferences = new(ApplicationPaths.PathComparer);
        private readonly Dictionary<string, QueueItem> requestedItems = new(StringComparer.Ordinal);
        private readonly HashSet<string> batchOutputs = new(ApplicationPaths.PathComparer);
        private List<PresetRecord>? storedSnapshot;
        private bool storedReferencesIndexed;
        private TaskCompletionSource budgetChanged = NewSignal();
        private PreviewReferences? references;
        private long? occupiedBytes;
        private long reservedBudgetBytes;
        private int activeWorkers;
        private bool prepared;
        private CancellationToken token;

        private string PreviewExtension => settings.PreviewFormat switch
        {
            PreviewAudioFormat.Aac => ".m4a",
            PreviewAudioFormat.Wav => ".wav",
            _ => throw new ArgumentException("无法识别预览存储格式，请重新选择 AAC 或 WAV。")
        };

        public Execution(LibraryStore store, AppSettings settings, IReadOnlyList<PresetRecord> originals,
            string workerPath, IProgress<QueueProgress> progress, CancellationToken token, bool force, IRenderResourceProbe resourceProbe,
            IReadOnlyList<RenderReadiness>? readiness = null)
        {
            this.store = store;
            originalSettings = settings;
            this.settings = Clone(settings);
            this.originals = originals;
            this.workerPath = workerPath;
            this.progress = progress;
            callerToken = token;
            this.force = force;
            this.readiness = readiness?.GroupBy(state => state.Synth).ToDictionary(group => group.Key, group => group.First() with { })
                ?? new Dictionary<SynthKind, RenderReadiness>();
            this.resourceProbe = resourceProbe;
            targetConcurrency = RenderResourcePolicy.ResolveMaxConcurrency(this.settings.MaxParallelRenders, resourceProbe.Hardware);
            memoryLimitBytes = RenderResourcePolicy.ResolveMemoryLimitBytes(this.settings.MemoryBudgetGiB);
            summary = new()
            {
                Requested = originals.Count, RequestedConcurrency = this.settings.MaxParallelRenders,
                ResolvedConcurrency = targetConcurrency, MemoryBudgetBytes = memoryLimitBytes
            };
        }

        public async Task<QueueResult> RunAsync()
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            using var monitorCancellation = new CancellationTokenSource();
            Task monitor = Task.CompletedTask;
            batchCancellation = cancellation;
            token = cancellation.Token;
            var running = new Dictionary<Task, RenderJob>();
            Report(QueueStages.Preparing, "正在准备生成队列...", indeterminate: true);
            try
            {
                token.ThrowIfCancellationRequested();
                if (settings.MaxAttempts is < 1 or > 5) throw new ArgumentException("重试上限须为 1 到 5。");
                if (!double.IsFinite(settings.StorageBudgetGb) || settings.StorageBudgetGb <= 0)
                    throw new ArgumentException("存储预算须大于 0。");
                CaptureResources();
                ThrowIfResourceStopped();
                monitor = MonitorResourcesAsync(monitorCancellation.Token);
                if (force)
                {
                    var stored = store.LoadPresets();
                    references = new PreviewReferences(stored, originals, settings.PreviewDirectory, token);
                    storedSnapshot = stored;
                }
                var waiting = PrepareJobs();
                CaptureResources();
                while (waiting.Count > 0 || running.Count > 0)
                {
                    ThrowIfResourceStopped();
                    token.ThrowIfCancellationRequested();
                    var resourceWake = ResourceChangedTask();
                    while (running.Count < targetConcurrency)
                    {
                        if (!TryStartJob(waiting, running.Values, running.Count, out var job)) break;
                        running.Add(RunJobAsync(job!), job!);
                    }
                    ThrowIfResourceStopped();
                    if (running.Count == 0)
                    {
                        if (waiting.Count == 0) break;
                        await resourceWake.WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    var finished = await Task.WhenAny(running.Keys.Append(resourceWake)).ConfigureAwait(false);
                    if (ReferenceEquals(finished, resourceWake)) continue;
                    running.Remove(finished);
                    await finished.ConfigureAwait(false);
                }
                DuplicatePreviewService.Synchronize(store, token);
                monitorCancellation.Cancel();
                await monitor.ConfigureAwait(false);
                ThrowIfResourceStopped();
                if (summary.PeakActiveCount == 0) resourceMessage = "无需启动预览工作进程。";
                summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
                Report(QueueStages.Completed, summary.Summary);
                return summary;
            }
            catch (Exception error)
            {
                // Every started job must finish its cancellation cleanup or commit before
                // returning control to the UI, including jobs whose WAV was already moved.
                cancellation.Cancel();
                try { await Task.WhenAll(running.Keys).ConfigureAwait(false); }
                catch { /* The initiating exception is reported after all jobs are drained. */ }
                monitorCancellation.Cancel();
                await monitor.ConfigureAwait(false);
                summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
                if (Volatile.Read(ref resourceFailure) is string reason)
                {
                    Report(QueueStages.Failed, reason + " " + summary.Summary);
                    throw new RenderResourceLimitException(reason, summary);
                }
                if (error is OperationCanceledException)
                    Report(QueueStages.Stopped, $"已停止，可继续；剩余 {summary.Remaining} 项。" + summary.Summary);
                else Report(QueueStages.Failed, error.Message + " " + summary.Summary);
                throw;
            }
            finally
            {
                monitorCancellation.Cancel();
                await monitor.ConfigureAwait(false);
                summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            }
        }

        private List<RenderJob> PrepareJobs()
        {
            var plugins = settings.Plugins.GroupBy(plugin => plugin.Synth).ToDictionary(group => group.Key, group => group.First());
            var jobs = new Dictionary<string, RenderJob>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<QueueItem>();
            var skipped = new List<QueueItem>();
            foreach (var original in originals)
            {
                token.ThrowIfCancellationRequested();
                var item = new QueueItem(original);
                var preset = item.Record;
                requestedItems.TryAdd(preset.Id, item);
                bool repeatedSelection = !ids.Add(preset.Id);
                if (repeatedSelection || preset.Synth == SynthKind.Unknown || !string.IsNullOrEmpty(preset.DuplicateOfId) ||
                    preset.Status is PresetStatus.Unsupported or PresetStatus.Missing or PresetStatus.Unidentified)
                    summary.AddExcluded(preset, repeatedSelection);
                else { candidates.Add(item); summary.Eligible++; }
            }
            prepared = true;
            Report(QueueStages.Preparing, summary.Selection.Description, indeterminate: true);
            foreach (var item in candidates)
            {
                token.ThrowIfCancellationRequested();
                var preset = item.Record;
                bool unconfigured = !plugins.TryGetValue(preset.Synth, out var plugin) || plugin.Backend == RenderBackend.Unconfigured;
                readiness.TryGetValue(preset.Synth, out var state);
                bool unavailable = !unconfigured && state?.CanAttemptRender == false;
                if ((unconfigured || unavailable) && (force || !HasPreviewMetadata(preset)))
                { SkipRenderer(item, plugin, state, unconfigured, skipped); continue; }
                Report(QueueStages.Validating, "正在核对已有预览及试听配置...", item, true);
                bool hasPreview = HasValidPreview(preset, token);
                if (hasPreview && !force)
                {
                    if (preset.Status is not (PresetStatus.Ready or PresetStatus.NeedsReview))
                    {
                        preset.Status = PresetStatus.NeedsReview;
                        preset.Message = "保留已生成的有效预览，请试听确认。";
                        Persist(item);
                    }
                    summary.Reused++;
                    Report(QueueStages.Validating, "已生成有效预览，默认跳过。", item);
                    continue;
                }
                if (unconfigured || unavailable) { SkipRenderer(item, plugin, state, unconfigured, skipped); continue; }
                settings.Template.Validate();
                string error = !File.Exists(plugin!.ExecutablePath) ? $"{preset.Synth} 渲染程序不存在。" :
                    plugin.TimeoutSeconds is < 5 or > 3600 ? "进程超时须为 5 到 3600 秒。" :
                    string.IsNullOrWhiteSpace(plugin.Version) ? $"请先填写 {preset.Synth} 的实际版本。" : "";
                if (error.Length > 0) { FailBeforeRendering(item, error, hasPreview); continue; }
                string key = Fingerprints.CacheKey(preset, plugin, settings.Template);
                string lastAttemptKey = preset.AttemptedCacheKey.Length > 0 ? preset.AttemptedCacheKey : preset.CacheKey;
                bool resetAttempts = force || lastAttemptKey.Length > 0 && lastAttemptKey != key ||
                    preset.Status is PresetStatus.Ready or PresetStatus.NeedsReview;
                if (!resetAttempts && preset.Attempts >= settings.MaxAttempts && preset.Status != PresetStatus.Pending)
                {
                    summary.RetryLimited++;
                    Report(QueueStages.Validating, "已达到重试上限。", item);
                    continue;
                }
                item.Previous = hasPreview ? Clone(preset) : null;
                if (resetAttempts) preset.Attempts = 0;
                else if (preset.Attempts >= settings.MaxAttempts && preset.Status == PresetStatus.Pending)
                    preset.Attempts = settings.MaxAttempts - 1;
                if (!jobs.TryGetValue(key, out var job))
                {
                    job = new(key, plugin, []);
                    jobs.Add(key, job);
                }
                job.Items.Add(item);
            }
            if (skipped.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var merged = store.MergeRenderStates(skipped.Select(item => (item.Record, item.Identity, false)).ToList(),
                    existingRecordsOnly: true);
                for (int index = 0; index < skipped.Count; index++)
                    LibraryStore.CopyRenderFields(skipped[index].Original, merged[index]);
                Report(QueueStages.Preparing, summary.Summary);
            }
            foreach (var job in jobs.Values)
                batchOutputs.Add(Path.GetFullPath(Path.Combine(settings.PreviewDirectory, job.Key + PreviewExtension)));
            // Keep each synth's jobs adjacent without changing the first-seen synth
            // order or the original order within a synth. Same-key aliases remain
            // one job and therefore still share one render / commit boundary.
            return jobs.Values.GroupBy(job => job.Plugin.Synth).SelectMany(group => group).ToList();
        }

        private async Task RunJobAsync(RenderJob job)
        {
            var itemClock = Stopwatch.StartNew();
            var pending = job.Items.ToList();
            string? completedAudio = null;
            try
            {
                var baseline = await BaselineAsync(job).ConfigureAwait(false);
                if (InitBaselineService.Supports(job.Plugin) && !baseline.State.CanCompare && !baseline.State.CanRenderWithVerifiedPresetLoad)
                {
                    lock (gate)
                        foreach (var item in pending)
                            FailBeforeRendering(item, "默认启动音色保护未就绪：" + baseline.State.Detail, item.Previous is not null);
                    return;
                }
                var reservation = await ReserveAsync(job).ConfigureAwait(false);
                var plugin = Clone(job.Plugin);
                completedAudio = Path.Combine(settings.PreviewDirectory, ".sp-render-" + Guid.NewGuid().ToString("N")[..16] + PreviewExtension);
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        foreach (var item in pending)
                        {
                            var preset = item.Record;
                            preset.Status = PresetStatus.Rendering;
                            preset.AttemptedCacheKey = job.Key;
                            preset.Attempts++;
                            item.AttemptInProgress = true;
                            preset.Message = $"第 {preset.Attempts} 次尝试";
                            preset.LastRenderOutcome = "生成中";
                            preset.LastRenderMessage = preset.Message;
                            Persist(item);
                        }
                    }
                    RenderResult? result = null;
                    string? failure = null;
                    lock (gate)
                    {
                        activeWorkers++;
                        summary.PeakActiveCount = Math.Max(summary.PeakActiveCount, activeWorkers);
                        Report(QueueStages.Rendering, pending[0].Record.Message, pending[0], true);
                    }
                    try
                    {
                        result = await new WorkerProcessClient().RenderAsync(new RenderRequest
                        {
                            Preset = Clone(pending[0].Record), Plugin = plugin, Template = Clone(settings.Template),
                            OutputPath = completedAudio, CacheKey = job.Key, MaxOutputBytes = reservation.OutputBytes
                        }, workerPath, token, (candidate, audio, comparisonToken) =>
                        {
                            if (baseline.State.CanRenderWithVerifiedPresetLoad)
                            {
                                if (!PresetLoadVerification.IsSupported(plugin) ||
                                    !PresetLoadVerification.IsValid(candidate, pending[0].Record, settings.Template))
                                    throw new InvalidDataException("目标预制加载核验失败：当前音源启动声音不稳定，必须核对同一源文件和三个音区的精确状态回读后才能保存。");
                                candidate.NeedsReview = true;
                                candidate.Message += " 已逐音区核对目标预制状态；启动声音存在随机变化，未使用声音基准比对，须试听确认。";
                            }
                            else if (baseline.Signature is not null)
                            {
                                var comparison = InitAudioComparer.Compare(baseline.Signature, audio, candidate.Notes, comparisonToken);
                                if (comparison.IsMatch) throw new InvalidDataException(InitBaselineService.DescribeRejection(plugin.Synth, comparison.Detail));
                            }
                            else
                            {
                                candidate.NeedsReview = true;
                                candidate.Message += " 默认音色保护未适配：" + baseline.State.Detail;
                            }
                            return Task.CompletedTask;
                        }, processId => RegisterWorker(job, processId)).ConfigureAwait(false);
                        if (!result.Success) failure = result.Message;
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or TimeoutException or ArgumentException)
                    { failure = error.Message; }
                    finally { lock (gate) activeWorkers--; }

                    // No cancellation check after Worker success: its atomic move is the
                    // commit boundary, and all same-key records must now be persisted.
                    if (result?.Success == true)
                    {
                        lock (gate) Commit(job, pending, result, reservation, itemClock.Elapsed.TotalSeconds);
                        return;
                    }
                    lock (gate)
                    {
                        foreach (var item in pending.ToArray())
                        {
                            item.AttemptInProgress = false;
                            var preset = item.Record;
                            bool unsupported = result?.SkippedUnsupported == true && result.NonRetryable &&
                                plugin.Synth == SynthKind.LogicPatch && plugin.Backend == RenderBackend.LogicPatch;
                            if (unsupported)
                            {
                                preset.Status = PresetStatus.Unsupported;
                                preset.Message = result!.Message;
                                preset.LastRenderOutcome = "已跳过";
                                preset.LastRenderMessage = preset.Message;
                                preset.Attempts = Math.Max(0, preset.Attempts - 1);
                                if (item.Previous is not null) RestorePreview(preset, item.Previous, "已跳过 Logic 原生乐器，保留上次预览：" + preset.Message);
                                pending.Remove(item);
                                summary.SkippedUnsupported++;
                                Persist(item);
                                Report(QueueStages.Rendering, preset.Message, item, itemElapsed: itemClock.Elapsed.TotalSeconds);
                                continue;
                            }
                            preset.Status = PresetStatus.Failed;
                            preset.Message = failure ?? "渲染工作进程未返回有效结果。";
                            preset.LastRenderOutcome = "失败";
                            preset.LastRenderMessage = preset.Message;
                            bool finished = result?.NonRetryable == true || preset.Attempts >= settings.MaxAttempts || InitBaselineService.IsRejection(preset.Message);
                            if (finished)
                            {
                                if (item.Previous is not null) RestorePreview(preset, item.Previous, "重新生成失败，保留上次预览：" + preset.Message);
                                pending.Remove(item);
                                summary.Failed++;
                            }
                            Persist(item);
                            Report(QueueStages.Rendering, preset.Message, item, itemElapsed: itemClock.Elapsed.TotalSeconds);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (gate)
                {
                    foreach (var item in pending)
                    {
                        if (!item.AttemptInProgress) continue;
                        var preset = item.Record;
                        bool resourceStop = Volatile.Read(ref resourceFailure) is not null;
                        preset.LastRenderOutcome = resourceStop ? "内存保护停止" : "已停止";
                        preset.LastRenderMessage = resourceStop ? resourceFailure! : "已停止；已存在的有效预览保留，可继续生成。";
                        if (item.Previous is not null) RestorePreview(preset, item.Previous, resourceStop ? "内存保护已停止，保留上次已生成的预览。" : "已停止，保留上次已生成的预览。");
                        else
                        {
                            preset.Status = PresetStatus.Pending;
                            preset.Attempts = Math.Max(0, preset.Attempts - 1);
                            preset.Message = resourceStop ? "内存保护已停止，可继续。" : "已停止，可继续。";
                        }
                        try { Persist(item); }
                        catch (Exception recoveryError) when (recoveryError is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException)
                        { Report(QueueStages.Failed, "未能写回中断状态，下次打开音色库时将恢复：" + recoveryError.Message, item); }
                        Report(QueueStages.Rendering, preset.Message, item, itemElapsed: itemClock.Elapsed.TotalSeconds);
                    }
                }
                throw;
            }
            catch (Exception error)
            {
                // A failed database transaction or publication must not strand other
                // started members in Rendering. A source changed by a scanner is left alone.
                lock (gate)
                    foreach (var item in pending.Where(candidate => candidate.AttemptInProgress))
                    {
                        var preset = item.Record;
                        preset.LastRenderOutcome = "失败";
                        preset.LastRenderMessage = error.Message;
                        if (item.Previous is not null) RestorePreview(preset, item.Previous, "重新生成未能提交，保留上次预览。" + error.Message);
                        else
                        {
                            LibraryStore.CopyAudioFields(preset, item.Identity);
                            preset.Status = PresetStatus.Failed;
                            preset.Message = error.Message;
                        }
                        try { Persist(item); }
                        catch (Exception recoveryError) when (recoveryError is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException)
                        { Report(QueueStages.Failed, "未能写回中断状态，下次打开音色库时将恢复：" + recoveryError.Message, item); }
                    }
                throw;
            }
            finally
            {
                if (completedAudio is not null)
                {
                    try { File.Delete(completedAudio); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { Report(QueueStages.Failed, "已保留未能清理的临时音频：" + completedAudio, job.Items[0]); }
                }
                CaptureResources();
                lock (gate) Release(job);
            }
        }

        private Task<PreparedInitBaseline> BaselineAsync(RenderJob job)
        {
            lock (gate)
            {
                if (!baselines.TryGetValue(job.Plugin.Synth, out var task))
                {
                    task = PrepareBaselineAsync(job);
                    baselines.Add(job.Plugin.Synth, task);
                }
                return task;
            }
        }

        private async Task<PreparedInitBaseline> PrepareBaselineAsync(RenderJob job)
        {
            var plugin = job.Plugin;
            string previous = plugin.InitBaselinePath;
            var scratch = new object();
            try
            {
                lock (gate)
                {
                    Report(QueueStages.Validating, "正在检查或建立默认启动音色基准...", job.Items[0], true);
                    if (InitBaselineService.Supports(plugin)) ReserveScratch(scratch, PeakDiskBytes() + 3 * OutputBytes());
                }
                var result = await InitBaselineService.PrepareAsync(settings, plugin, workerPath, token,
                    processId => RegisterWorker(job, processId)).ConfigureAwait(false);
                lock (gate)
                {
                    if (plugin.InitBaselinePath != previous)
                    {
                        store.SaveInitializationBaseline(settings, plugin.Synth, previous, plugin.InitBaselinePath);
                        var original = originalSettings.Plugins.FirstOrDefault(candidate => candidate.Synth == plugin.Synth);
                        if (original is not null && original.InitBaselinePath == previous) original.InitBaselinePath = plugin.InitBaselinePath;
                    }
                }
                return result;
            }
            finally
            {
                lock (gate)
                {
                    scratchReservations.Remove(scratch);
                    SignalBudgetChanged();
                }
            }
        }

        private async Task<Reservation> ReserveAsync(RenderJob job)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Task wait;
                lock (gate)
                {
                    Directory.CreateDirectory(settings.PreviewDirectory);
                    if (occupiedBytes is null)
                    {
                        Report(QueueStages.Preparing, "正在统计预览目录占用...", job.Items[0], true);
                        occupiedBytes = 0;
                        foreach (string path in Directory.EnumerateFiles(settings.PreviewDirectory, "*", SearchOption.TopDirectoryOnly).Where(PreviewAudioFile.IsSupportedExtension))
                        {
                            token.ThrowIfCancellationRequested();
                            occupiedBytes += new FileInfo(path).Length;
                        }
                    }
                    string output = Path.Combine(settings.PreviewDirectory, job.Key + PreviewExtension);
                    long replaced = File.Exists(output) ? new FileInfo(output).Length : 0;
                    var oldCaches = new List<OwnedCache>();
                    if (references is not null)
                        foreach (var group in job.Items.Where(item => item.Previous is not null).GroupBy(item => item.Previous!.PreviewPath, ApplicationPaths.PathComparer))
                        {
                            var previous = group.First().Previous!;
                            var old = references.Reclaimable(previous.PreviewPath, previous.CacheKey, output, group.Count());
                            // Another job can introduce a new reference to its own output
                            // while this job runs; do not spend that future file as credit.
                            if (old is not null && !batchOutputs.Contains(old.Path)) oldCaches.Add(old);
                        }
                    long maximum = OutputBytes();
                    long delta = maximum - replaced - oldCaches.Sum(cache => cache.Bytes);
                    if (occupiedBytes.Value + reservedBudgetBytes + delta <= settings.StorageBudgetGb * 1024 * 1024 * 1024)
                    {
                        try
                        {
                            ReserveScratch(job, checked(PeakDiskBytes() + replaced));
                            var reservation = new Reservation(maximum, replaced, Math.Max(0, delta), oldCaches);
                            reservations.Add(job, reservation);
                            reservedBudgetBytes += reservation.BudgetBytes;
                            return reservation;
                        }
                        catch (IOException) when (reservations.Count > 0) { }
                    }
                    else if (reservations.Count == 0)
                        throw new IOException("预览目录已达到存储预算，请调整预算或预览目录。");
                    wait = budgetChanged.Task;
                }
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }

        private long OutputBytes() => Math.Min(256L * 1024 * 1024, checked((long)Math.Ceiling(
            (settings.Template.QuickPreview ? settings.Template.QuickPreviewSeconds : settings.Template.HoldSeconds + settings.Template.TailSeconds)
            * 3 * settings.Template.SampleRate * 8 + 4096)));

        private long PeakDiskBytes()
        {
            double captureSeconds = settings.Template.QuickPreview
                ? QuickPreviewAudio.MaximumCaptureHoldSeconds(settings.Template.QuickPreviewSeconds) * 1.3
                : settings.Template.HoldSeconds + settings.Template.TailSeconds;
            long raw = checked((long)Math.Ceiling(captureSeconds * 3 * settings.Template.SampleRate * 8 + 4096));
            return checked(3 * raw + 2 * OutputBytes() + 16L * 1024 * 1024);
        }

        private void ReserveScratch(object owner, long bytes)
        {
            Directory.CreateDirectory(settings.PreviewDirectory);
            string directory = Path.GetFullPath(settings.PreviewDirectory);
            // On Unix, '/' need not be the volume containing an external Preview root.
            var drive = DriveInfo.GetDrives().Where(candidate => IsWithinVolume(directory, candidate.Name))
                .OrderByDescending(candidate => candidate.Name.Length).FirstOrDefault()
                ?? new DriveInfo(Path.GetPathRoot(directory)!);
            long available = drive.AvailableFreeSpace;
            if (available < scratchReservations.Values.Sum() + bytes)
                throw new IOException("磁盘可用空间不足以保留生成过程的临时音频，请释放空间或选择其他预览目录。");
            scratchReservations.Add(owner, bytes);
        }

        private void Commit(RenderJob job, List<QueueItem> pending, RenderResult result, Reservation reservation, double seconds)
        {
            string completedAudio = result.OutputPath;
            string outputPath = Path.Combine(settings.PreviewDirectory, job.Key + PreviewExtension);
            string backup = Path.Combine(settings.PreviewDirectory, ".sp-old-" + Guid.NewGuid().ToString("N")[..16] + PreviewExtension);
            bool published = false, committed = false;
            List<PresetRecord> merged;
            try
            {
                if (File.Exists(outputPath))
                {
                    EnsureStoredReferenceIndex();
                    File.Copy(outputPath, backup);
                }
                File.Move(completedAudio, outputPath, overwrite: true);
                published = true;
                result.OutputPath = outputPath;
                var updates = new List<(PresetRecord Rendered, PresetRecord Identity, bool AudioOnly)>();
                var ids = pending.Select(item => item.Record.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var item in pending)
                {
                    ApplyResult(item.Record, job, result);
                    updates.Add((item.Record, item.Identity, false));
                }
                if (storedReferences.TryGetValue(Path.GetFullPath(outputPath), out var shared))
                    foreach (var outside in shared.Where(record => record.CacheKey == job.Key && !ids.Contains(record.Id)))
                    {
                        var updated = Clone(outside);
                        ApplyResult(updated, job, result);
                        updates.Add((updated, outside, true));
                    }
                merged = store.MergeRenderStates(updates);
                committed = true;
            }
            catch
            {
                // Keep the old bytes and their old note mapping together on DB failure.
                if (published)
                {
                    if (File.Exists(backup)) File.Move(backup, outputPath, overwrite: true);
                    else File.Delete(outputPath);
                }
                throw;
            }
            finally
            {
                if (committed || !published)
                    try { File.Delete(backup); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { Report(QueueStages.Rendering, "新预览已保存，旧文件备份未能清理：" + backup, pending[0]); }
            }
            occupiedBytes = occupiedBytes!.Value - reservation.ReplacedBytes + new FileInfo(outputPath).Length;
            for (int index = 0; index < pending.Count; index++)
            {
                var item = pending[index];
                LibraryStore.CopyRenderFields(item.Original, merged[index]);
                item.AttemptInProgress = false;
                references?.Move(item.Record.Id, outputPath);
                if (index == 0) summary.Generated++; else summary.Reused++;
            }
            foreach (var outside in merged.Skip(pending.Count))
                if (requestedItems.TryGetValue(outside.Id, out var item))
                {
                    LibraryStore.CopyAudioFields(item.Record, outside);
                    LibraryStore.CopyAudioFields(item.Original, outside);
                    if (item.Original.Status == PresetStatus.Ready && outside.Status == PresetStatus.NeedsReview)
                        item.Record.Status = item.Original.Status = PresetStatus.NeedsReview;
                    Report(QueueStages.Rendering, "相同缓存的预览音区与指标已同步。", item);
                }
            foreach (var old in reservation.OldCaches)
            {
                var cleanup = references!.DeleteCommitted(old, outputPath);
                occupiedBytes -= cleanup.FreedBytes;
                if (cleanup.Warning.Length == 0) continue;
                foreach (var item in pending)
                {
                    item.Record.Message += " " + cleanup.Warning;
                    try { Persist(item); }
                    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException)
                    { Report(QueueStages.Rendering, "新预览已保存，缓存清理诊断未能入库：" + error.Message, item); }
                }
            }
            foreach (var item in pending)
                Report(QueueStages.Rendering, $"{item.Record.Message} 用时 {seconds:F2} 秒。", item, itemElapsed: seconds);
        }

        private void ApplyResult(PresetRecord preset, RenderJob job, RenderResult result)
        {
            preset.PreviewPath = result.OutputPath;
            preset.CacheKey = job.Key;
            preset.Metrics = Clone(result.Metrics!);
            preset.Notes = Clone(result.Notes);
            preset.AdapterVersion = result.AdapterVersion;
            preset.PluginVersion = job.Plugin.Version;
            preset.TemplateJson = JsonSerializer.Serialize(settings.Template, LibraryJson.Options);
            preset.RenderedUtc = DateTime.UtcNow;
            preset.Status = result.NeedsReview ? PresetStatus.NeedsReview : PresetStatus.Ready;
            preset.Message = result.Message;
            preset.LastRenderOutcome = "成功";
            preset.LastRenderMessage = result.Message;
        }

        private void EnsureStoredReferenceIndex()
        {
            if (storedReferencesIndexed) return;
            var stored = storedSnapshot ?? store.LoadPresets();
            var ids = stored.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var preset in stored.Concat(requestedItems.Values.Where(item => !ids.Contains(item.Identity.Id)).Select(item => item.Identity)))
            {
                if (string.IsNullOrWhiteSpace(preset.PreviewPath)) continue;
                string path;
                try { path = Path.GetFullPath(preset.PreviewPath); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { continue; }
                if (!storedReferences.TryGetValue(path, out var records)) storedReferences.Add(path, records = []);
                records.Add(preset);
            }
            storedReferencesIndexed = true;
        }

        private static bool IsWithinVolume(string path, string volume)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(volume));
            return string.Equals(path, root, ApplicationPaths.PathComparison) ||
                path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, ApplicationPaths.PathComparison);
        }

        private void Release(RenderJob job)
        {
            if (reservations.Remove(job, out var reservation)) reservedBudgetBytes -= reservation.BudgetBytes;
            scratchReservations.Remove(job);
            CompleteResourceLease(job);
            SignalBudgetChanged();
        }

        private void SignalBudgetChanged()
        {
            var previous = budgetChanged;
            budgetChanged = NewSignal();
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void FailBeforeRendering(QueueItem item, string error, bool hasPreview)
        {
            item.Record.LastRenderOutcome = "失败";
            item.Record.LastRenderMessage = error;
            if (!hasPreview) { item.Record.Status = PresetStatus.Failed; item.Record.Message = error; }
            Persist(item);
            summary.Failed++;
            Report(QueueStages.Validating, error, item);
        }

        private void SkipRenderer(QueueItem item, PluginConfiguration? plugin, RenderReadiness? state, bool unconfigured,
            List<QueueItem> skipped)
        {
            string status = state?.Status ?? "未选择生成方式";
            string detail = state?.Detail ?? "尚未选择可运行的预览生成方式，请选择对应音源的生成方式。";
            if (!string.IsNullOrWhiteSpace(plugin?.ExecutablePath)) detail += " 程序路径：" + plugin.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(plugin?.PluginPath)) detail += " 插件路径：" + plugin.PluginPath;
            string outcome = unconfigured ? "未选择生成方式" : "已配置，暂不可用";
            item.Record.LastRenderOutcome = outcome;
            item.Record.LastRenderMessage = outcome + " · " + status + "：" + detail;
            // Retain an existing preview's confirmation text and all cached audio fields.
            if (item.Record.Status is not (PresetStatus.Ready or PresetStatus.NeedsReview) && !File.Exists(item.Record.PreviewPath))
                item.Record.Message = item.Record.LastRenderMessage;
            if (unconfigured) summary.AddUnconfigured(item.Record.Synth, status, detail);
            else summary.AddUnavailable(item.Record.Synth, status, detail);
            skipped.Add(item);
        }

        private void Persist(QueueItem item)
        {
            var merged = store.MergeRenderState(item.Record, item.Identity);
            LibraryStore.CopyRenderFields(item.Original, merged);
        }

        private void Report(string stage, string message, QueueItem? item = null, bool indeterminate = false, double itemElapsed = 0)
        {
            lock (gate)
            {
                var concurrency = CurrentConcurrency();
                bool terminal = stage is QueueStages.Completed or QueueStages.Stopped or QueueStages.Failed && !indeterminate && item is null;
                progress.Report(new(summary.Processed, prepared ? summary.Eligible : summary.Requested, item?.Original.Name ?? "", message, item?.Original)
                {
                    Stage = stage, IsIndeterminate = indeterminate, ActiveCount = activeWorkers,
                    MaxConcurrency = targetConcurrency, ItemElapsedSeconds = itemElapsed,
                    WorkingSetBytes = memorySnapshot.ResidentBytes, MemoryBudgetBytes = memoryLimitBytes,
                    ReservedMemoryBytes = ReservedMemoryBytes, Selection = prepared ? summary.Selection : null,
                    ResourceMessage = resourceMessage, AllowedConcurrency = allowedConcurrency,
                    EffectiveConcurrency = terminal ? 0 : concurrency.Capacity,
                    PreparingCount = terminal ? 0 : Math.Max(0, memoryLeases.Count - activeWorkers),
                    ConcurrencyMessage = terminal ? "" : concurrency.Reason,
                    Generated = summary.Generated, Reused = summary.Reused, Failed = summary.Failed, SkippedUnsupported = summary.SkippedUnsupported,
                    Unconfigured = summary.Unconfigured, Unavailable = summary.Unavailable, RetryLimited = summary.RetryLimited
                });
            }
        }
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, LibraryJson.Options), LibraryJson.Options)!;
}
