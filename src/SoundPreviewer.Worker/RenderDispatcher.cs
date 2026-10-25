using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;

namespace SoundPreviewer.Worker;

public static partial class RenderDispatcher
{
    public const string VitalAdapterVersion = "vital-native-seconds-v1";

    public static Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (request.IsInitializationBaseline && (!Enum.IsDefined(request.Preset.Synth) ||
            request.Preset.Synth != request.Plugin.Synth || request.Preset.SourcePath.Length > 0 ||
            request.Preset.ContentHash.Length > 0 || request.Preset.InternalPath.Length > 0 || request.Preset.BankSlot is not null))
            throw new InvalidDataException("默认启动基准请求必须使用已识别且匹配的音源身份，不得包含源预制。");
        if (request.Preset.Synth == SynthKind.Unknown || request.Plugin.Synth == SynthKind.Unknown ||
            request.Preset.Status == PresetStatus.Unidentified)
            return Task.FromResult(new RenderResult { Message = "无法识别此预制的音源，禁止生成预览。请先核对预制格式与插件身份。" });
        return request.Template.QuickPreview ? RenderQuickAsync(request, cancellationToken) : RenderFixedAsync(request, cancellationToken);
    }

    private static Task<RenderResult> RenderFixedAsync(RenderRequest request, CancellationToken cancellationToken) =>
        request.Plugin.Backend switch
        {
            RenderBackend.LogicPatch => RenderLogicPatchAsync(request, cancellationToken),
            RenderBackend.VitalCli => RenderVitalAsync(request, cancellationToken),
            RenderBackend.ExternalWorker => RenderExternalAsync(request, cancellationToken),
            RenderBackend.DawDreamer => RenderExternalAsync(request, cancellationToken, dawdreamer: true),
            RenderBackend.Sushi => RenderSushiAsync(request, cancellationToken),
            _ => Task.FromResult(new RenderResult { Message = "尚未配置音源渲染后端。" })
        };

    private static async Task<RenderResult> RenderQuickAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        const long maximumCaptureBytes = 256L * 1024 * 1024;
        bool vital = request.Plugin.Backend == RenderBackend.VitalCli;
        double targetSeconds = request.Template.QuickPreviewSeconds;
        double maximumHold = QuickPreviewAudio.MaximumCaptureHoldSeconds(targetSeconds);
        double firstHold = vital ? Math.Ceiling(targetSeconds) : targetSeconds;
        double[] holds = new[] { firstHold, 1, 2, 4, maximumHold }.Where(hold => hold >= firstHold).Distinct().Order().ToArray();
        var segments = new WaveData?[3];
        var notes = new NotePreview?[3];
        var complete = new bool[3];
        bool needsReview = false;
        bool allCapturesVerified = !request.IsInitializationBaseline && PresetLoadVerification.IsSupported(request.Plugin);
        PresetLoadProof? loadProof = null;
        string adapterVersion = "", nativeMessage = "";
        string output = Path.GetFullPath(request.OutputPath);
        string scratch = Path.Combine(Path.GetDirectoryName(output)!, "quick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        int captures = 0;
        try
        {
            foreach (double hold in holds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string capturePath = Path.Combine(scratch, $"capture-{captures++}.wav");
                var captureTemplate = new PreviewTemplate
                {
                    QuickPreview = false,
                    QuickPreviewSeconds = targetSeconds,
                    NoteOrder = request.Template.NoteOrder,
                    LowNote = request.Template.LowNote, MiddleNote = request.Template.MiddleNote, HighNote = request.Template.HighNote,
                    Velocity = vital ? 89 : request.Template.Velocity,
                    HoldSeconds = hold, TailSeconds = vital ? hold * 0.3 : QuickPreviewAudio.CaptureTailSeconds,
                    Bpm = request.Template.Bpm, SampleRate = request.Template.SampleRate
                };
                var captureRequest = new RenderRequest
                {
                    Preset = request.Preset, Plugin = request.Plugin, Template = captureTemplate,
                    IsInitializationBaseline = request.IsInitializationBaseline,
                    CacheKey = request.CacheKey, OutputPath = capturePath, MaxOutputBytes = maximumCaptureBytes
                };
                // The existing adapters capture all three pitches; keep satisfactory earlier clips unchanged.
                var capture = await RenderFixedAsync(captureRequest, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!capture.Success)
                {
                    capture.Message = "快速预览采集未完成：" + capture.Message;
                    return capture;
                }
                WorkerProcessClient.ValidateNotes(capture, captureTemplate);
                // A later round can contribute only some pitches; require every round to be
                // verified rather than allowing one trusted round to bless other captures.
                allCapturesVerified &= PresetLoadVerification.IsValid(capture, request.Preset, captureTemplate);
                if (allCapturesVerified)
                {
                    if (loadProof is not null && (loadProof.Scheme != capture.PresetLoadProof!.Scheme ||
                        loadProof.CanonicalStateSha256 != capture.PresetLoadProof.CanonicalStateSha256))
                        throw new InvalidDataException("自动延长时目标预制的回读状态发生变化，未提交混合来源预览。");
                    loadProof ??= capture.PresetLoadProof;
                }
                var audio = WaveFile.Read(capturePath, cancellationToken);
                if (adapterVersion.Length > 0 && adapterVersion != capture.AdapterVersion)
                    throw new InvalidDataException("自动延长时宿主适配器版本发生变化，未提交混合来源预览。");
                adapterVersion = capture.AdapterVersion;
                nativeMessage = capture.Message;
                needsReview |= capture.NeedsReview;
                for (int index = 0; index < 3; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (complete[index]) continue;
                    var nativeNote = capture.Notes[index];
                    int first = checked((int)Math.Round(nativeNote.StartSeconds * audio.SampleRate));
                    int frames = checked((int)Math.Round(nativeNote.DurationSeconds * audio.SampleRate));
                    if (first < 0 || frames <= 0 || first > audio.Frames - frames)
                        throw new InvalidDataException("原始采集音区超出音频范围。");
                    var source = new WaveData
                    {
                        SampleRate = audio.SampleRate, Channels = audio.Channels,
                        Samples = audio.Samples.AsSpan(first * audio.Channels, frames * audio.Channels).ToArray()
                    };
                    var analysis = QuickPreviewAudio.Analyze(source, targetSeconds, cancellationToken);
                    var clip = QuickPreviewAudio.Crop(source, analysis, targetSeconds, cancellationToken);
                    var metrics = WaveFile.Analyze(clip, cancellationToken);
                    if (metrics.HasNonFinite) throw new InvalidDataException("快速预览含非有限数值。");
                    segments[index] = clip;
                    notes[index] = new NotePreview
                    {
                        MidiNote = nativeNote.MidiNote, DurationSeconds = clip.DurationSeconds,
                        ActualHoldSeconds = nativeNote.ActualHoldSeconds, ActualTailSeconds = nativeNote.ActualTailSeconds,
                        ActualVelocity = nativeNote.ActualVelocity, ActualNormalizedVelocity = nativeNote.ActualNormalizedVelocity,
                        TrimmedLeadingSeconds = analysis.StartFrame / (double)source.SampleRate,
                        TrimmedTrailingSeconds = (source.Frames - analysis.ClipEndFrame) / (double)source.SampleRate,
                        Metrics = metrics
                    };
                    complete[index] = !analysis.NeedsMoreAudio;
                    needsReview |= metrics.IsSilent || metrics.HasClipping;
                }
                if (complete.All(value => value)) break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var finalSegments = segments.Select(segment => segment ?? throw new InvalidDataException("快速预览缺少音区。")).ToArray();
            var finalNotes = notes.Select(note => note ?? throw new InvalidDataException("快速预览缺少来源映射。")).ToList();
            long startFrame = 0;
            for (int index = 0; index < 3; index++)
            {
                finalNotes[index].StartSeconds = startFrame / (double)request.Template.SampleRate;
                startFrame += finalSegments[index].Frames;
            }
            var combined = WaveFile.Concatenate(finalSegments);
            if (checked(combined.Samples.LongLength * sizeof(float) + 56) > request.MaxOutputBytes)
                throw new InvalidDataException("快速预览输出超出存储剩余额度，未提交音频。");
            WaveFile.WriteFloat32(output, combined, cancellationToken);
            var result = new RenderResult
            {
                Success = true, OutputPath = output, Metrics = WaveFile.Analyze(combined, cancellationToken), Notes = finalNotes,
                AdapterVersion = Fingerprints.QuickPreviewPolicy + "/" + adapterVersion,
                PresetLoadProof = allCapturesVerified ? new PresetLoadProof
                {
                    Scheme = loadProof!.Scheme, SourceSha256 = request.Preset.ContentHash,
                    CanonicalStateSha256 = loadProof.CanonicalStateSha256,
                    Pitches = request.Template.Pitches.ToList()
                } : null,
                NeedsReview = needsReview || complete.Any(value => !value),
                Message = $"已生成三个紧凑的真实试听片段，完成 {captures} 轮采集。" +
                    (complete.Any(value => !value) ? $" 部分音区延长到 {maximumHold:0.##} 秒后仍较弱或有效声音不足 {targetSeconds:0.##} 秒，已保留真实片段待确认。" : "") +
                    " " + nativeMessage
            };
            WorkerProcessClient.ValidateNotes(result, request.Template);
            return result;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<RenderResult> RenderVitalAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (request.IsInitializationBaseline)
            throw new NotSupportedException("Vital 原生命令行尚不支持安全地生成无预制的默认启动基准；未使用自造 Init 预制代替。");
        if (request.Preset.Synth != SynthKind.Vital) throw new ArgumentException("Vital 原生适配器仅能处理 Vital 预制。");
        bool bankEntry = request.Preset.InternalPath.Length > 0 &&
            Path.GetExtension(request.Preset.SourcePath).Equals(".vitalbank", StringComparison.OrdinalIgnoreCase);
        if (request.Preset.BankSlot is not null || !bankEntry && (!string.IsNullOrEmpty(request.Preset.InternalPath) ||
            !Path.GetExtension(request.Preset.SourcePath).Equals(".vital", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("无法预览：Vital 需要独立 .vital 或已核对内嵌资源的音色包条目。" + VitalBankPresetSource.ExportInstructions);
        var template = request.Template;
        if (Math.Abs(template.HoldSeconds - Math.Round(template.HoldSeconds)) > 0.000001 || template.HoldSeconds < 1)
            throw new ArgumentException("Vital 原生 --length 按整数秒读取，请使用 1 到 30 秒的整数按键时长。");
        if (template.Velocity != 89 || Math.Abs(template.TailSeconds - template.HoldSeconds * 0.3) > 0.000001)
            throw new ArgumentException("Vital 原生力度固定为 0.7（约 MIDI 89），尾音固定为按键时长的 30%；请采用 Vital 原生参数。");
        string executable = Path.GetFullPath(request.Plugin.ExecutablePath);
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 Vital 独立程序，请指定当前平台的 Vital 可执行文件。", executable);
        if (!File.Exists(request.Preset.SourcePath)) throw new FileNotFoundException("源预制已丢失。", request.Preset.SourcePath);
        if (!bankEntry && new FileInfo(request.Preset.SourcePath).Length > 64L * 1024 * 1024)
            throw new InvalidDataException("Vital 预制超过 64 MB 读取限制。");
        byte[] presetBytes = bankEntry ? VitalBankPresetSource.ReadVerifiedEntry(request.Preset, cancellationToken) :
            await File.ReadAllBytesAsync(request.Preset.SourcePath, cancellationToken);
        string actualHash = Convert.ToHexString(SHA256.HashData(presetBytes)).ToLowerInvariant();
        if (!bankEntry && !string.IsNullOrWhiteSpace(request.Preset.ContentHash) &&
            !string.Equals(actualHash, request.Preset.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("源预制在扫描后发生变化，请重新扫描后再生成预览。");
        using (var presetJson = JsonDocument.Parse(presetBytes))
        {
            if (presetJson.RootElement.ValueKind != JsonValueKind.Object ||
                !presetJson.RootElement.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Vital 预制缺少有效 settings 对象，禁止把初始音色误记为此预制。");
            if (presetJson.RootElement.TryGetProperty("synth_version", out var version) &&
                version.ValueKind == JsonValueKind.String && Version.TryParse(version.GetString(), out var required) &&
                Version.TryParse(request.Plugin.Version, out var installed) && required > installed)
                throw new InvalidDataException($"预制需要 Vital {required}，配置的音源版本为 {installed}。");
        }

        string output = Path.GetFullPath(request.OutputPath);
        string scratch = Path.Combine(Path.GetDirectoryName(output)!, "vital-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var segments = new List<WaveData>();
            var notes = new List<NotePreview>();
            double start = 0;
            bool suspicious = false;
            for (int i = 0; i < template.Pitches.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string noteDirectory = Path.Combine(scratch, "note-" + i);
                Directory.CreateDirectory(noteDirectory);
                await File.WriteAllBytesAsync(Path.Combine(noteDirectory, "preset.vital"), presetBytes, cancellationToken);
                // Relative input avoids Vital's './' prefix corrupting Windows drive paths.
                var outcome = await ProcessRunner.RunAsync(executable,
                    ["--headless", "--render", "./preset.vital", "--midi", template.Pitches[i].ToString(CultureInfo.InvariantCulture),
                     "--length", ((int)template.HoldSeconds).ToString(CultureInfo.InvariantCulture),
                     "--bpm", template.Bpm.ToString(CultureInfo.InvariantCulture)],
                    noteDirectory, TimeSpan.FromSeconds(Math.Clamp(request.Plugin.TimeoutSeconds, 5, 3600)), cancellationToken);
                if (outcome.ExitCode != 0) throw new IOException($"Vital 渲染退出码 {outcome.ExitCode}：{outcome.StandardError}");
                string wavPath = Path.Combine(noteDirectory, "preset.wav");
                if (!File.Exists(wavPath)) throw new IOException("Vital 未产生 WAV；该安装版本可能不支持原生渲染。");
                var native = WaveFile.Read(wavPath);
                var nativeMetrics = WaveFile.Analyze(native);
                if (nativeMetrics.HasNonFinite) throw new InvalidDataException("Vital 输出含非有限数值。");
                if (native.SampleRate != 44100 || native.Channels != 2 ||
                    Math.Abs(native.DurationSeconds - template.HoldSeconds * 1.3) > 0.01)
                    throw new InvalidDataException("Vital 输出与已核对源码的格式 / 时长不符；此安装版本需要重新验证适配器。");
                suspicious |= nativeMetrics.IsSilent || nativeMetrics.HasClipping;
                var segment = WaveFile.Resample(native, template.SampleRate);
                segments.Add(segment);
                double actualHold = Math.Ceiling(template.HoldSeconds * 44100 / 64) * 64 / 44100;
                notes.Add(new NotePreview
                {
                    MidiNote = template.Pitches[i], StartSeconds = start, DurationSeconds = segment.DurationSeconds,
                    ActualHoldSeconds = actualHold, ActualTailSeconds = segment.DurationSeconds - actualHold,
                    ActualVelocity = 89, ActualNormalizedVelocity = 0.7, Metrics = WaveFile.Analyze(segment)
                });
                start += segment.DurationSeconds;
            }
            WaveFile.WritePcm16(output, WaveFile.Concatenate(segments));
            var metrics = WaveFile.Validate(output);
            return new RenderResult
            {
                Success = true, OutputPath = output, Metrics = metrics, Notes = notes,
                AdapterVersion = bankEntry ? VitalAdapterVersion + "+embedded-bank-v1" : VitalAdapterVersion, NeedsReview = true,
                Message = "已生成真实 Vital 输出；原生 CLI 不报告预制加载成功，须人工确认音色及外部资源。" +
                    (template.SampleRate == 48000 ? " 原生 44100 Hz 输出已重采样为 48000 Hz。" : "") +
                    (suspicious ? " 检测到静音或接近削波的音区。" : "")
            };
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<RenderResult> RenderExternalAsync(RenderRequest request, CancellationToken cancellationToken, bool dawdreamer = false)
    {
        string executable = Path.GetFullPath(request.Plugin.ExecutablePath);
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到已配置的外部渲染器。", executable);
        if (Path.GetFileNameWithoutExtension(executable).Equals("SoundPreviewer.Worker", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("外部渲染器不能配置为 SoundPreviewer.Worker 自身。");
        string output = Path.GetFullPath(request.OutputPath);
        string scratch = Path.Combine(Path.GetDirectoryName(output)!, "external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string requestFile = Path.Combine(scratch, "request.json");
            string resultFile = Path.Combine(scratch, "result.json");
            await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request, LibraryJson.Options), cancellationToken);
            var arguments = new List<string>();
            if (dawdreamer)
            {
                string script = Path.Combine(AppContext.BaseDirectory, "Renderers", "dawdreamer_worker.py");
                if (!File.Exists(script)) throw new FileNotFoundException("缺少随应用发布的 DawDreamer 桥接脚本。", script);
                arguments.AddRange(["-I", "-B", script]);
            }
            arguments.AddRange(["--request", requestFile, "--result", resultFile]);
            IReadOnlyDictionary<string, string?>? environmentOverrides = !dawdreamer &&
                Path.GetFileName(executable).Equals("SoundPreviewer.Vst2Host.exe", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, string?> { ["DOTNET_ROOT"] = null } : null;
            // All DawDreamer protocol paths are absolute. CreateProcess still
            // rejects a long CWD even when Python accepts the long JSON paths.
            string workingDirectory = scratch;
            if (dawdreamer && OperatingSystem.IsWindows() && scratch.Length >= 240)
            {
                workingDirectory = AppContext.BaseDirectory;
                if (workingDirectory.Length >= 240) workingDirectory = Path.GetPathRoot(executable)!;
            }
            var outcome = await ProcessRunner.RunAsync(executable, arguments, workingDirectory,
                TimeSpan.FromSeconds(Math.Clamp(request.Plugin.TimeoutSeconds, 5, 3600)), cancellationToken, environmentOverrides);
            if (!File.Exists(resultFile))
            {
                string phaseFile = resultFile + ".phase";
                string phase = dawdreamer && File.Exists(phaseFile) && new FileInfo(phaseFile).Length <= 4096
                    ? await File.ReadAllTextAsync(phaseFile, cancellationToken) : "未知阶段";
                throw new IOException($"外部渲染器没有返回结果（退出码 {outcome.ExitCode} / 0x{unchecked((uint)outcome.ExitCode):X8}，{phase}）：{outcome.StandardError}");
            }
            if (new FileInfo(resultFile).Length > 1024 * 1024) throw new InvalidDataException("外部渲染器结果超过 1 MB 限制。");
            var result = JsonSerializer.Deserialize<RenderResult>(await File.ReadAllTextAsync(resultFile, cancellationToken), LibraryJson.Options)
                ?? throw new InvalidDataException("外部结果 JSON 无效。");
            if (!result.Success)
            {
                if (string.IsNullOrWhiteSpace(result.Message)) result.Message = "外部渲染器返回失败，未提供原因。";
                return result;
            }
            if (outcome.ExitCode != 0) throw new IOException($"外部渲染器异常退出（{outcome.ExitCode}）。");
            if (!string.Equals(Path.GetFullPath(result.OutputPath), output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("外部渲染器返回的路径与请求不符。");
            result.Metrics = WaveFile.Validate(output);
            if (result.Metrics.SampleRate != request.Template.SampleRate)
                throw new InvalidDataException("外部渲染器采样率与请求不符。");
            WorkerProcessClient.ValidateNotes(result, request.Template);
            for (int i = 0; i < 3; i++)
            {
                var note = result.Notes[i];
                if (note.ActualVelocity != request.Template.Velocity ||
                    Math.Abs(note.ActualHoldSeconds - request.Template.HoldSeconds) > 0.01 ||
                    Math.Abs(note.ActualTailSeconds - request.Template.TailSeconds) > 0.01)
                    throw new InvalidDataException("外部渲染器未按请求的力度 / 时长演奏，需显式使用匹配的试听模板。");
            }
            result.NeedsReview |= result.Metrics.IsSilent || result.Metrics.HasClipping;
            return result;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
