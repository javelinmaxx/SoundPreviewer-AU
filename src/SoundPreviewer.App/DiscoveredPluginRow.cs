using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public sealed class DiscoveredPluginRow(DiscoveredPlugin record, IReadOnlyList<PluginConfiguration> configurations)
{
    public DiscoveredPlugin Record { get; } = record;
    public string SynthLabel => Labels.Synth(Record.Synth);
    public string NameLabel => Record.Synth == SynthKind.Unknown ? Record.Name : SynthLabel;
    public string ArchitectureLabel => Record.Architecture switch
    {
        PluginArchitecture.X64 => "64 位", PluginArchitecture.X86 => "32 位", PluginArchitecture.Universal => "通用", PluginArchitecture.Arm64 => "ARM64", _ => "未知"
    };
    public string FormatLabel => Record.Format switch
    {
        PluginBinaryFormat.AudioUnit => "AU", PluginBinaryFormat.Vst2 => "VST2", PluginBinaryFormat.Vst3 => "VST3", _ => "独立程序"
    };
    public string StatusLabel => Record.Format == PluginBinaryFormat.AudioUnit ? "已发现 AU · Logic 网页预览"
        : configurations.Any(p => PluginSelectionService.Matches(p, Record)) ? "已关联"
        : Record.Synth == SynthKind.Unknown ? "未支持音源"
        : !PluginSelectionService.IsCompatible(Record) ? "位数待适配" : "可选版本";
}
