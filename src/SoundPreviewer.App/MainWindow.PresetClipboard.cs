using System.Diagnostics;
using Avalonia.Interactivity;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private bool _isCopyingPresetPath;

    public bool CanLocatePreset => !_isClosing && !_isStorageMaintenance && !_storageRelocated &&
        Selected is { } row && (File.Exists(row.Record.SourcePath) || row.Record.Synth == SynthKind.LogicPatch && Directory.Exists(row.Record.SourcePath));
    public string LocatePresetLabel
    {
        get
        {
            var preset = Selected?.Record;
            bool bank = preset is not null && (preset.InternalPath.Length > 0 || preset.BankSlot is not null ||
                Path.GetExtension(preset.SourcePath).ToLowerInvariant() is ".vitalbank" or ".fxb" or ".sbf" or ".swzip");
            return bank ? "定位音色库文件" : "定位音色文件";
        }
    }
    public string LocatePresetToolTip => (OperatingSystem.IsWindows() ? "在资源管理器中" : "在访达中") +
        "选中原文件；音色包内条目会定位到所属音色库文件。";

    private static ProcessStartInfo PrepareSourceReveal(string path, bool windows)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !(fullPath.EndsWith(".patch", StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))) throw new FileNotFoundException("原音色或音色库文件已不存在。", fullPath);
        var start = new ProcessStartInfo(windows ? "explorer.exe" : "/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add(windows ? "/select," : "-R");
        start.ArgumentList.Add(fullPath);
        return start;
    }

    public bool CanCopyPresetPath => !_isClosing && !_isStorageMaintenance && !_storageRelocated && !_isCopyingPresetPath &&
        PresetSourceUnavailableReason(Selected?.Record).Length == 0;
    public string PresetCopyHint => PresetSourceUnavailableReason(Selected?.Record) is { Length: > 0 } reason
        ? reason : "复制原文件的完整路径；音色包或银行条目复制所属音色库的文件地址。";

    private static string PresetSourceUnavailableReason(PresetRecord? preset)
    {
        if (preset is null) return "选中音色后可复制原文件地址。";
        try
        {
            if (!Path.IsPathFullyQualified(preset.SourcePath))
                return "无法确认原文件的完整本地路径，请重新扫描。";
            string fullPath = Path.GetFullPath(preset.SourcePath);
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (extension is not (".patch" or ".vital" or ".vitalbank" or ".fxp" or ".fxb" or ".serumpreset" or ".spf" or ".spf2" or ".sbf" or ".swzip"))
                return "此项不是已识别的音色或音色库文件，无法复制文件地址。";
            return (File.Exists(fullPath) || extension == ".patch" && Directory.Exists(fullPath)) ? "" : "找不到原音色或音色库文件，请先恢复来源位置或重新扫描。";
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return "无法确认原文件的完整本地路径，请恢复来源位置或重新扫描。";
        }
    }

    private static string GetPresetSourceFilePath(PresetRecord preset)
    {
        string reason = PresetSourceUnavailableReason(preset);
        if (reason.Length > 0) throw new InvalidOperationException(reason);
        // SourcePath names the actual file; InternalPath is only an archive entry.
        return Path.GetFullPath(preset.SourcePath);
    }

    private async Task CopyPresetFilePathAsync(PresetRecord preset)
    {
        string path = GetPresetSourceFilePath(preset);
        var clipboard = Clipboard ?? throw new InvalidOperationException("系统剪贴板暂不可用，请稍后重试。");
        await clipboard.SetTextAsync(path);
    }

    private async void CopyPresetPath_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanCopyPresetPath || Selected is not { } row) return;
        _isCopyingPresetPath = true;
        NotifyAll();
        try
        {
            await CopyPresetFilePathAsync(row.Record);
            if (!_isClosing) Log("已复制文件地址：" + GetPresetSourceFilePath(row.Record));
        }
        catch (Exception error) { if (!_isClosing) ShowError(error); }
        finally { _isCopyingPresetPath = false; NotifyAll(); }
    }
}
