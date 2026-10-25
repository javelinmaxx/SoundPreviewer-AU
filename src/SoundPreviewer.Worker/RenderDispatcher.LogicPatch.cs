using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Worker;

public static partial class RenderDispatcher
{
    private static async Task<RenderResult> RenderLogicPatchAsync(RenderRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Logic Patch AU 生成需要 macOS 13+。");
        if (request.Preset.Synth != SynthKind.LogicPatch || request.Plugin.Synth != SynthKind.LogicPatch || request.IsInitializationBaseline)
            throw new ArgumentException("Logic Patch 后端仅支持完整 .patch 包，不支持默认启动基准。");
        if (LogicPatchSource.Read(request.Preset.SourcePath, token).Sha256 != request.Preset.ContentHash)
            throw new InvalidDataException("Logic Patch 自扫描后发生变化，请重新扫描。");
        if (!File.Exists(request.Plugin.ExecutablePath) || !File.Exists(request.Plugin.PluginPath))
            throw new FileNotFoundException("请先准备 Logic Patch 环境。");
        string directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!;
        Directory.CreateDirectory(directory);
        string requestFile = Path.Combine(directory, "logic-request-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request, LibraryJson.Options), token);
            // The native AU process inherits the desktop job's process group so
            // stopping the queue also terminates Python and the active AU host.
            var outcome = await ProcessRunner.RunAsync(LogicPatchRuntime.PythonExecutable(),
                [request.Plugin.PluginPath, "--request", requestFile], directory,
                TimeSpan.FromSeconds(request.Plugin.TimeoutSeconds), token, inheritProcessGroup: true);
            if (outcome.ExitCode != 0) throw new InvalidDataException("Logic Patch 生成失败：" + outcome.StandardError.Trim());
            var result = JsonSerializer.Deserialize<RenderResult>(outcome.StandardOutput, LibraryJson.Options)
                ?? throw new InvalidDataException("Logic Patch 生成器未返回有效结果。");
            if (!result.Success || result.AdapterVersion != LogicPatchRuntime.AdapterVersion ||
                !ApplicationPaths.SamePath(result.OutputPath, request.OutputPath))
                throw new InvalidDataException("Logic Patch 返回的输出路径或适配器身份不匹配。");
            if (LogicPatchSource.Read(request.Preset.SourcePath, token).Sha256 != request.Preset.ContentHash)
                throw new InvalidDataException("Logic Patch 在生成期间发生变化，请重新扫描。");
            var audio = WaveFile.Read(request.OutputPath, token);
            result.Metrics = WaveFile.Analyze(audio, token);
            WorkerProcessClient.ValidateNotes(result, request.Template);
            return result;
        }
        finally { File.Delete(requestFile); }
    }
}
