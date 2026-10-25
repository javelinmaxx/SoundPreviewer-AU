using System.Globalization;
using Avalonia.Data.Converters;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public sealed class SourceDetectionConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SourceFolder source ? source.AutoDetect ? "自动识别" : Labels.Synth(source.Synth) : "";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RenderBackendValues : Avalonia.Collections.AvaloniaList<RenderBackend>
{
    public RenderBackendValues() : base(Enum.GetValues<RenderBackend>()) { }
}

public sealed class BackendLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RenderBackend.Unconfigured => "未选择生成方式", RenderBackend.VitalCli => "Vital 原生",
        RenderBackend.ExternalWorker => "外部工作进程", RenderBackend.DawDreamer => "DawDreamer",
        RenderBackend.LogicPatch => "Logic Patch AU 链",
        RenderBackend.Sushi => "Sushi（本地研究）", _ => ""
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BackendIndexConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is RenderBackend backend ? (int)backend : 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && Enum.IsDefined(typeof(RenderBackend), index) ? (RenderBackend)index : Avalonia.Data.BindingOperations.DoNothing;
}
