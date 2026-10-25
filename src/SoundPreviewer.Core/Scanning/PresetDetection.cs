using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

internal sealed record VstEnvelope(bool IsValid, string PluginId, string Error, string Diagnostic);
internal sealed record SynthDetection(SynthKind Synth, string Reason);

internal static partial class PresetDetection
{
    public static bool SupportsAny(string extension) => extension.ToLowerInvariant() is ".patch" or ".vital" or ".vitalbank" or ".serumpreset" or ".fxp" or ".fxb" || IsSpireFormat(extension);

    // Reveal Sound's manual documents legacy SPF/SBF; its sound store documents SPF2/SWZIP.
    // Recognizing a dedicated suffix only establishes indexing identity, not a verified loader.
    public static bool IsSpireFormat(string extension) => extension.ToLowerInvariant() is ".spf" or ".spf2" or ".sbf" or ".swzip";

    public static string SynthName(SynthKind synth) => synth switch
    {
        SynthKind.Vital => "Vital",
        SynthKind.Serum1 => "Serum 1",
        SynthKind.Serum2 => "Serum 2",
        SynthKind.Sylenth1 => "Sylenth1",
        SynthKind.Spire => "Spire",
        SynthKind.LogicPatch => "Logic Patch",
        _ => "未识别"
    };

    public static List<string> Tags(SynthKind synth, string path)
    {
        return [];
    }

    public static List<string> MergeTags(IEnumerable<string>? existing, SynthKind synth, string path)
    {
        var automaticNames = Enum.GetValues<SynthKind>().Select(SynthName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var custom = (existing ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag) && !automaticNames.Contains(tag) && !SupportsAny(tag));
        return custom.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static VstEnvelope ReadEnvelope(Stream stream, long length, bool bank)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[28];
        int bytes = 0;
        while (bytes < header.Length)
        {
            int count = stream.Read(header[bytes..]);
            if (count == 0) break;
            bytes += count;
        }
        stream.Position = 0;
        if (bytes != header.Length || !header[..4].SequenceEqual("CcnK"u8))
            return new(false, "", "缺少有效的 CcnK 文件头。", "");
        bool parameter = header[8..12].SequenceEqual(bank ? "FxBk"u8 : "FxCk"u8);
        bool chunk = header[8..12].SequenceEqual(bank ? "FBCh"u8 : "FPCh"u8);
        long minimum = bank ? (chunk ? 160 : 156) : (chunk ? 60 : 56);
        if ((!parameter && !chunk) || length < minimum)
            return new(false, "", bank ? "FXB 文件头类型无效或文件过短。" : "FXP 文件头无效或此文件实际为音色库。", "");
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        string diagnostic = "";
        if (declared == length)
            diagnostic = "文件长度字段包含完整文件大小，已按兼容格式识别。";
        else if (declared != length - 8)
        {
            if (bank && declared >= minimum - 8 && (long)declared + 8 < length)
                diagnostic = "银行包含额外尾部数据，仅识别文件头身份，未验证银行载荷。";
            else return new(false, "", "文件长度字段与实际大小不符。", "");
        }
        var id = header[16..20];
        string pluginId = id.IndexOfAnyExceptInRange((byte)32, (byte)126) < 0 ? Encoding.ASCII.GetString(id) : "0x" + Convert.ToHexString(id);
        return new(true, pluginId, "", diagnostic);
    }

    public static SynthDetection Detect(string path, string root, VstEnvelope? envelope)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".vital", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase))
            return new(SynthKind.Vital, $"依据 Vital 专用格式 {extension.ToLowerInvariant()} 识别。");
        if (extension.Equals(".SerumPreset", StringComparison.OrdinalIgnoreCase))
            return new(SynthKind.Serum2, "依据 Serum 2 专用格式 .SerumPreset 识别。");
        if (IsSpireFormat(extension))
            return new(SynthKind.Spire, $"依据 Spire 专用格式 {extension.ToLowerInvariant()} 识别；载荷和原生加载尚未验证。");
        var hint = FindDirectoryHint(path, root);
        string hintText = hint is null ? "" : $" 最近目录“{hint.Name}”包含{hint.Description}线索。";
        if (envelope is null || !envelope.IsValid)
            return new(SynthKind.Unknown, "未能识别音源：" + (envelope?.Error ?? "缺少可验证的插件文件头。") + hintText);
        if (envelope.PluginId == "syl1")
        {
            string reason = "依据已核对的插件 ID syl1 识别为 Sylenth1。";
            if (hint is not null && (hint.Synths.Any(s => s != SynthKind.Sylenth1) || hint.SerumFamily))
                reason += hintText + " 文件头优先，未采用不一致的目录名称。";
            return new(SynthKind.Sylenth1, reason);
        }
        if (envelope.PluginId == "XfsX")
        {
            if (hint?.Synths.Contains(SynthKind.Serum1) == true && hint.Synths.Contains(SynthKind.Serum2))
                return new(SynthKind.Unknown, "文件头 XfsX 为旧版 Serum 格式；最近目录同时标记 Serum 1 和 Serum 2，目标音源不明确。" + hintText);
            if (hint?.Synths.Contains(SynthKind.Serum2) == true)
                return new(SynthKind.Serum2, "文件头 XfsX 为旧版 Serum 格式；依据最近目录的 Serum 2 标记归入 Serum 2，实际加载仍须验证。" + hintText);
            string reason = "依据已核对的插件 ID XfsX 识别为旧版 Serum 格式，归入 Serum 1。";
            if (hint is not null && hint.Synths.Any(s => s is not (SynthKind.Serum1 or SynthKind.Serum2)))
                reason += hintText + " 文件头优先，未采用不一致的目录名称。";
            return new(SynthKind.Serum1, reason);
        }
        return new(SynthKind.Unknown, $"插件 ID “{envelope.PluginId}”尚未核对，不能依据目录名称认定为已支持音源。" + hintText);
    }

    private static DirectoryHint? FindDirectoryHint(string path, string root)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (directory is not null)
        {
            string current = Path.TrimEndingDirectorySeparator(directory.FullName);
            string prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
            if (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) break;
            var matches = SynthTokens().Matches(directory.Name);
            if (matches.Count > 0)
            {
                var kinds = new HashSet<SynthKind>();
                bool serumFamily = false;
                foreach (Match match in matches)
                {
                    if (match.Groups["serum"].Success)
                    {
                        if (match.Groups["version"].Value == "1") kinds.Add(SynthKind.Serum1);
                        else if (match.Groups["version"].Value == "2") kinds.Add(SynthKind.Serum2);
                        else serumFamily = true;
                    }
                    else if (match.Groups["sylenth"].Success) kinds.Add(SynthKind.Sylenth1);
                    else kinds.Add(match.Groups["spire"].Success ? SynthKind.Spire : SynthKind.Vital);
                }
                string description = string.Join("、", kinds.OrderBy(s => s).Select(SynthName).Concat(serumFamily ? ["Serum"] : Array.Empty<string>()));
                return new(directory.Name, kinds, serumFamily, description);
            }
            if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase)) break;
            directory = directory.Parent;
        }
        return null;
    }

    private sealed record DirectoryHint(string Name, HashSet<SynthKind> Synths, bool SerumFamily, string Description);

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?<serum>serum)(?:[ _.-]*(?<version>[12]))?|(?<sylenth>sylenth)(?:[ _.-]*1)?|(?<spire>spire)|vital)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SynthTokens();
}
