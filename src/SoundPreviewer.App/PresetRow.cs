using System.ComponentModel;
using System.IO;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public sealed class PresetRow(PresetRecord record, MetadataCatalog? categories = null, MetadataCatalog? tags = null, Func<string>? metadataLanguage = null) : INotifyPropertyChanged
{
    private bool _hasPreview = File.Exists(record.PreviewPath);
    public PresetRecord Record { get; } = record;
    public bool HasPreview => _hasPreview;
    public string Name => Record.Name;
    public string SynthLabel => Labels.Synth(Record.Synth);
    public string Package => Record.Package;
    public string StatusLabel => Labels.PreviewStatus(Record, HasPreview);
    public bool IsFailed => Labels.PreviewStatusKey(Record, HasPreview) == "failed";
    public bool IsGenerated => Labels.PreviewStatusKey(Record, HasPreview) == "generated";
    public bool IsRendering => Labels.PreviewStatusKey(Record, HasPreview) == "rendering";
    public string DuplicateLabel => Record.DuplicateOfId.Length > 0 ? "相同内容已有保留项，生成时共用保留项的预览；可查看保留项试听。" : "";
    public string Message
    {
        get
        {
            if (!HasPreview && Record.Status is PresetStatus.Ready or PresetStatus.NeedsReview)
                return "原预览文件不存在，可重新生成。" + (Record.Message.Length > 0 ? Environment.NewLine + InitBaselineService.ExplainStoredRejection(Record.Synth, Record.Message) : "");
            if (Record.Status == PresetStatus.Unsupported &&
                Record.Message.Contains("尚未验证原生音色包导入及依赖资源", StringComparison.Ordinal) &&
                Path.GetExtension(Record.SourcePath).Equals(".vitalbank", StringComparison.OrdinalIgnoreCase))
                return "此包内条目尚未通过当前版本的资源检查，请重新扫描音色；若仍无法预览，可在 Vital 中导入后另存为独立 .vital 文件再扫描。";
            if (Record.Status == PresetStatus.Unsupported &&
                Record.Message.Contains("银行加载与槽位尚未验证", StringComparison.Ordinal) &&
                Path.GetExtension(Record.SourcePath).Equals(".fxb", StringComparison.OrdinalIgnoreCase))
                return "当前无法直接生成 FXB 音色库预览。请在对应音源中打开音色库，将所需音色导出为独立 .fxp 文件后重新扫描。";
            if (Record.Message.Length > 0) return InitBaselineService.ExplainStoredRejection(Record.Synth, Record.Message);
            return Record.Status switch
            {
                PresetStatus.Unsupported => "当前格式无法直接生成预览，请在对应音源中另存为受支持的独立音色后重新扫描。",
                PresetStatus.Missing => "来源文件不存在，请检查来源目录后重新扫描。",
                PresetStatus.Unidentified => "尚未识别对应音源，请指定音源后重新扫描。",
                _ => ""
            };
        }
    }
    public IEnumerable<string> TagLabels => tags is null || Record.TagIds is null ? Record.Tags : tags.Labels(Record.TagIds, metadataLanguage?.Invoke() ?? "zh-CN");
    public IEnumerable<string> CategoryLabels => categories is null || Record.CategoryIds is null ? Record.CategoryTags : categories.Labels(Record.CategoryIds, metadataLanguage?.Invoke() ?? "zh-CN");
    public string TagsLabel => string.Join(" · ", TagLabels);
    public string CategoryTagsLabel => string.Join(" · ", CategoryLabels);
    public IEnumerable<string> CategoryKeys => Record.CategoryIds?.Select(id => "id:" + id) ?? Record.CategoryTags;
    public IEnumerable<string> TagKeys => Record.TagIds?.Select(id => "id:" + id) ?? Record.Tags;
    public bool MatchesMetadata(string search) => categories?.Matches(Record.CategoryIds, search) == true || tags?.Matches(Record.TagIds, search) == true;
    public string SourceLabel => Path.GetDirectoryName(Record.SourcePath) ?? "";
    public string SourcePathLabel => Record.SourcePath;
    public string AttemptsLabel => Record.Attempts.ToString();
    public string RenderedLabel => Record.RenderedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public string DurationLabel => Record.Metrics is { } metrics ? $"{metrics.DurationSeconds:0.00} 秒" : "";
    public string PluginVersionLabel => Record.PluginVersion;
    public string AdapterVersionLabel => Record.AdapterVersion;
    public string LastRenderOutcomeLabel => Record.LastRenderOutcome;
    public string LastRenderMessageLabel => InitBaselineService.ExplainStoredRejection(Record.Synth, Record.LastRenderMessage);
    public string FeedbackLabel => Record.Feedback switch { 1 => "喜欢", -1 => "不喜欢", _ => "" };
    public string FavoriteLabel => Record.Favorite ? "★" : "";
    public string AudioLabel => Record.Metrics is { } m ? $"{m.DurationSeconds:0.0} 秒 · {m.SampleRate} Hz · {m.Channels} 声道" : "尚未生成";
    public string QualityLabel => Record.Metrics is { } m
        ? string.Join(" · ", new[] { m.IsSilent ? "静音待查" : "", m.HasClipping ? "削波风险" : "", m.HasNonFinite ? "异常采样" : "" }.Where(s => s.Length > 0)) : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        _hasPreview = File.Exists(Record.PreviewPath);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}

public sealed record Choice<T>(string Label, T Value);
public sealed record CompatibilityRow(string Synth, string Status, string Detail);

public static class Labels
{
    public static string Synth(SynthKind s) => s switch { SynthKind.LogicPatch => "Logic Patch", SynthKind.Serum1 => "Serum 1", SynthKind.Serum2 => "Serum 2", SynthKind.Unknown => "待识别", _ => s.ToString() };
    public static string PresetExtensions(SynthKind s) => s switch
    {
        SynthKind.LogicPatch => ".patch（Logic 包目录）",
        SynthKind.Vital => ".vital / .vitalbank",
        SynthKind.Serum1 => ".fxp",
        SynthKind.Serum2 => ".SerumPreset / .fxp",
        SynthKind.Sylenth1 => ".fxp / .fxb",
        SynthKind.Spire => ".spf / .spf2 / .sbf / .swzip / .fxp（手动指定）",
        SynthKind.Unknown => ".fxp / .fxb",
        _ => ""
    };
    public static string PreviewStatusKey(PresetRecord record, bool hasPreview) => record.Status switch
    {
        PresetStatus.Rendering => "rendering",
        _ when hasPreview => "generated",
        PresetStatus.Pending or PresetStatus.Ready or PresetStatus.NeedsReview => "pending",
        PresetStatus.Failed => "failed",
        _ => "unavailable"
    };
    public static string PreviewStatus(PresetRecord record, bool hasPreview) => PreviewStatusKey(record, hasPreview) switch
    {
        "rendering" => "生成中", "generated" => "已生成", "pending" => "待生成", "failed" => "生成失败", _ => "无法预览"
    };
    public static string Status(PresetStatus s) => s switch
    {
        PresetStatus.Pending => "待生成", PresetStatus.Rendering => "生成中", PresetStatus.Ready or PresetStatus.NeedsReview => "已生成",
        PresetStatus.Failed => "生成失败", _ => "无法预览"
    };
}
