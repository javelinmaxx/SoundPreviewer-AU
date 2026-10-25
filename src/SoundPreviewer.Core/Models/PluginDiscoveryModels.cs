namespace SoundPreviewer.Core.Models;

public enum PluginArchitecture { Unknown, X86, X64, Arm64, Universal }
public enum PluginBinaryFormat { Vst2, Vst3, Standalone, AudioUnit }
// Zero preserves the platform of discovery records saved by the original Windows app.
public enum PluginPlatform { Windows, MacOS }

public sealed class DiscoveredPlugin
{
    public string Id { get; set; } = "";
    public SynthKind Synth { get; set; } = SynthKind.Unknown;
    public string FilePath { get; set; } = "";
    public string BinaryPath { get; set; } = "";
    public string Version { get; set; } = "";
    public PluginArchitecture Architecture { get; set; }
    public PluginPlatform Platform { get; set; }
    public PluginBinaryFormat Format { get; set; }
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class PluginScanResult
{
    public List<DiscoveredPlugin> Plugins { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> UnavailableRoots { get; init; } = [];
}
