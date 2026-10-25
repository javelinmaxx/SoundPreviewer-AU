using Avalonia.Interactivity;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async void PrepareLogicPatch_Click(object? sender, RoutedEventArgs e) => await RunTask(async token =>
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Logic Patch 音频生成需要 macOS 13+。");
        string support = LogicPatchRuntime.SupportDirectory(AppContext.BaseDirectory);
        string setup = Path.Combine(support, "scripts", "Setup-LogicPreview.sh");
        if (!File.Exists(setup)) throw new FileNotFoundException("缺少 Logic Patch 环境脚本，请重新完整构建应用。", setup);
        _taskTitle = "正在准备 Logic Patch 环境";
        _statusMessage = "正在编译本机 AU 宿主；需要 Python 3.10+ 和 Xcode Command Line Tools。";
        NotifyAll();
        var outcome = await ProcessRunner.RunAsync("/bin/bash", [setup], App.DataDirectory,
            TimeSpan.FromMinutes(10), token, new Dictionary<string, string?>
            {
                ["SOUNDPREVIEWER_PYTHON"] = LogicPatchRuntime.PythonExecutable(),
                ["SOUNDPREVIEWER_LOGIC_BUILD_DIR"] = LogicPatchRuntime.BuildDirectory(App.DataDirectory)
            });
        if (outcome.ExitCode != 0) throw new IOException("Logic Patch 环境准备失败：" + outcome.StandardError.Trim());
        var candidate = ClonePlugins();
        if (!LogicPatchRuntime.Configure(candidate, AppContext.BaseDirectory, App.DataDirectory, replaceExisting: true))
            throw new InvalidDataException("未找到生成的 Logic AU 宿主，或 Logic Patch 已选择其他后端。请先将该行生成方式设为未选择。");
        var saved = candidate.Single(plugin => plugin.Synth == SynthKind.LogicPatch);
        saved.ExecutableHash = await Task.Run(() => Fingerprints.RendererExecutableHash(saved, token), token);
        saved.PluginHash = Fingerprints.PluginBinaryHash(saved, [], token);
        var plugin = Plugins.Single(plugin => plugin.Synth == SynthKind.LogicPatch);
        plugin.Backend = saved.Backend;
        plugin.ExecutablePath = saved.ExecutablePath;
        plugin.PluginPath = saved.PluginPath;
        plugin.Version = saved.Version;
        plugin.ExecutableHash = saved.ExecutableHash;
        plugin.PluginHash = saved.PluginHash;
        plugin.AllowParallelRendering = saved.AllowParallelRendering;
        _settings.Plugins = ClonePlugins();
        _store.SaveSettings(_settings);
        RefreshGrid(PluginGrid);
        RefreshCompatibility();
        _taskTitle = "Logic Patch 环境已准备";
        _statusMessage = "添加 .patch 所在文件夹，音源选择 Logic Patch，扫描后即可生成预览。";
        Log(_statusMessage);
        NotifyAll();
    });
}
