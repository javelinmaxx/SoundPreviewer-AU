using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public static class LogicPatchRuntime
{
    public const string AdapterVersion = "logic-au-chain-desktop-v1";
    public static string SupportDirectory(string application) => Path.Combine(application, "Renderers", "LogicPatch");
    public static string BuildDirectory(string data) => Path.Combine(data, "renderers", "logic-preview");
    public static string BridgePath(string application) => Path.Combine(SupportDirectory(application), "scripts", "logic_patch_desktop.py");
    public static string PythonExecutable()
    {
        string? configured = Environment.GetEnvironmentVariable("SOUNDPREVIEWER_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        // Finder's PATH often omits Homebrew. The bridge reports Python < 3.10 clearly.
        var pathCandidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, "python3"))
            .Where(path => path is not ("/usr/bin/python3" or "/bin/python3"));
        return new[] { "/opt/homebrew/bin/python3", "/usr/local/bin/python3" }.Concat(pathCandidates)
            .Append("/usr/bin/python3").FirstOrDefault(File.Exists) ?? "python3";
    }

    public static bool Configure(IList<PluginConfiguration> plugins, string application, string data, bool replaceExisting = false)
    {
        string executable = Path.Combine(BuildDirectory(data), "LogicPreview");
        string bridge = BridgePath(application);
        if (!OperatingSystem.IsMacOS() || !File.Exists(executable) || !File.Exists(bridge)) return false;
        var configuration = plugins.FirstOrDefault(plugin => plugin.Synth == SynthKind.LogicPatch);
        if (configuration is null) { configuration = new() { Synth = SynthKind.LogicPatch }; plugins.Add(configuration); }
        if (configuration.Backend is not (RenderBackend.Unconfigured or RenderBackend.LogicPatch)) return false;
        if (!replaceExisting && configuration.Backend == RenderBackend.LogicPatch &&
            File.Exists(configuration.ExecutablePath) && File.Exists(configuration.PluginPath)) return false;
        bool firstPreparation = configuration.Backend == RenderBackend.Unconfigured;
        configuration.Backend = RenderBackend.LogicPatch;
        configuration.ExecutablePath = executable;
        configuration.PluginPath = bridge;
        configuration.Version = AdapterVersion;
        // A patch can contain several licensed AUs; do not open many instances by default.
        if (firstPreparation) configuration.AllowParallelRendering = false;
        return true;
    }
}
