using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

public sealed class PluginScanService
{
    private const long MaximumBinaryBytes = 512L * 1024 * 1024;
    private const uint MaximumExportNames = 4096;

    public static string? ResolveBundleExecutable(string path)
    {
        try
        {
            string extension = Path.GetExtension(path);
            if (!Directory.Exists(path) || !(extension.Equals(".app", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".vst", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".component", StringComparison.OrdinalIgnoreCase))) return null;
            string content = Path.Combine(path, "Contents"), binaries = Path.Combine(content, "MacOS");
            string plist = Path.Combine(content, "Info.plist");
            if (new[] { path, content, binaries, plist }.Any(item => (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)) return null;
            string executable = MacBundleMetadata.Read(plist).GetValueOrDefault("CFBundleExecutable", "");
            if (string.IsNullOrWhiteSpace(executable) || executable is "." or ".." || executable.IndexOfAny(['/', '\\', '\0']) >= 0) return null;
            string binary = Path.Combine(binaries, executable);
            return File.Exists(binary) && (File.GetAttributes(binary) & FileAttributes.ReparsePoint) == 0 ? binary : null;
        }
        catch (Exception error) when (IsFileError(error)) { return null; }
    }

    public Task<PluginScanResult> ScanAsync(IReadOnlyList<string> directories, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = directories.ToArray();
        return Task.Run(() => Scan(snapshot, cancellationToken), cancellationToken);
    }

    private static PluginScanResult Scan(IReadOnlyList<string> directories, CancellationToken token)
    {
        var result = new PluginScanResult();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("目录不能为空。");
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
            }
            catch (Exception error) when (IsFileError(error))
            {
                result.Warnings.Add($"插件目录无效：{directory}；{error.Message}");
                result.UnavailableRoots.Add(directory);
            }
        }
        foreach (string root in roots.OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                result.Warnings.Add($"插件目录不可访问：{root}");
                result.UnavailableRoots.Add(root);
                continue;
            }
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                if (!seenDirectories.Add(directory)) continue;
                try
                {
                    if (IsLink(directory, result))
                    {
                        if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) result.UnavailableRoots.Add(root);
                        continue;
                    }
                    string directoryExtension = Path.GetExtension(directory);
                    if (directoryExtension.Equals(".vst3", StringComparison.OrdinalIgnoreCase) ||
                        directoryExtension.Equals(".vst", StringComparison.OrdinalIgnoreCase) ||
                        directoryExtension.Equals(".app", StringComparison.OrdinalIgnoreCase) ||
                        directoryExtension.Equals(".component", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(Path.Combine(directory, "Contents", "MacOS"))) InspectMacBundle(directory);
                        else if (directoryExtension.Equals(".vst3", StringComparison.OrdinalIgnoreCase)) InspectBundle(directory);
                        continue;
                    }
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (IsLink(path, result)) continue;
                            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) pending.Push(path);
                            else InspectFile(path);
                        }
                        catch (Exception error) when (IsFileError(error))
                        {
                            result.Warnings.Add($"无法读取插件候选：{path}；{error.Message}");
                        }
                    }
                }
                catch (Exception error) when (IsFileError(error))
                {
                    result.Warnings.Add($"无法完整扫描插件目录：{directory}；{error.Message}");
                    if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) result.UnavailableRoots.Add(root);
                }
            }
        }
        result.Plugins.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.BinaryPath, right.BinaryPath));
        return result;

        void InspectMacBundle(string bundle)
        {
            string content = Path.Combine(bundle, "Contents");
            string binaries = Path.Combine(content, "MacOS");
            string metadataPath = Path.Combine(content, "Info.plist");
            if (IsLink(content, result) || IsLink(binaries, result) || IsLink(metadataPath, result)) return;
            var metadata = MacBundleMetadata.Read(metadataPath);
            string executable = metadata.GetValueOrDefault("CFBundleExecutable", "");
            if (string.IsNullOrWhiteSpace(executable) || executable is "." or ".." ||
                executable.IndexOfAny(['/', '\\', '\0']) >= 0)
                throw new InvalidDataException("macOS 包的 CFBundleExecutable 无效，不能离开 Contents/MacOS 目录。");
            string binary = Path.Combine(binaries, executable);
            if (IsLink(binary, result) || !seenBinaries.Add(binary)) return;
            token.ThrowIfCancellationRequested();
            PluginBinaryFormat format = Path.GetExtension(bundle).ToLowerInvariant() switch
            {
                ".app" => PluginBinaryFormat.Standalone,
                ".component" => PluginBinaryFormat.AudioUnit,
                ".vst3" => PluginBinaryFormat.Vst3,
                _ => PluginBinaryFormat.Vst2
            };
            var native = MachOPluginMetadata.Read(binary, format, token);
            string product = metadata.GetValueOrDefault("CFBundleName", metadata.GetValueOrDefault("CFBundleDisplayName", ""));
            string identifier = metadata.GetValueOrDefault("CFBundleIdentifier", "");
            string version = metadata.GetValueOrDefault("CFBundleShortVersionString", metadata.GetValueOrDefault("CFBundleVersion", ""));
            string fileName = Path.GetFileNameWithoutExtension(bundle);
            if (format != PluginBinaryFormat.AudioUnit &&
                (IsSerumEffect(fileName) || IsSerumEffect(product) || IsSerumEffect(identifier))) return;
            SynthKind synth = IdentifyMac(fileName, product, identifier, version);
            if (format == PluginBinaryFormat.Standalone && synth != SynthKind.Vital) return;
            string name = synth == SynthKind.Unknown ? (string.IsNullOrWhiteSpace(product) ? fileName : product.Trim()) : PresetDetection.SynthName(synth);
            result.Plugins.Add(new DiscoveredPlugin
            {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binary))).ToLowerInvariant(),
                Synth = synth, FilePath = bundle, BinaryPath = binary, Version = version.Trim(),
                Architecture = native, Platform = PluginPlatform.MacOS, Format = format, Name = name,
                Reason = format == PluginBinaryFormat.AudioUnit
                    ? "已发现 AUv2 .component 包并读取 Mach-O 架构；未验证 macOS 注册、授权或可加载性。Logic .patch 请使用本地网页预览器；桌面音源预制后端尚不支持 AU。"
                    : synth == SynthKind.Unknown
                    ? "macOS 包及 Mach-O 入口有效，但产品 / 厂商标识不足以确认音源；文件名不作为唯一身份依据。"
                    : $"macOS 包名称、产品 / 厂商标识对应 {name}，Mach-O 架构为 {native}；仅检查元数据和入口，未实例化插件。"
            });
        }

        void InspectBundle(string bundle)
        {
            bool foundBinary = false;
            foreach (string architectureDirectory in new[] { "x86_64-win", "x86-win", "arm64-win" })
            {
                token.ThrowIfCancellationRequested();
                string content = Path.Combine(bundle, "Contents");
                string binaries = Path.Combine(content, architectureDirectory);
                if (!Directory.Exists(binaries)) continue;
                if (IsLink(content, result) || IsLink(binaries, result)) continue;
                seenDirectories.Add(binaries);
                foreach (string binary in Directory.EnumerateFiles(binaries, "*", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    string extension = Path.GetExtension(binary);
                    if (!extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    foundBinary = true;
                    try
                    {
                        if (!IsLink(binary, result)) InspectFile(binary, bundle);
                    }
                    catch (Exception error) when (IsFileError(error))
                    {
                        result.Warnings.Add($"无法读取 VST3 包内插件：{binary}；{error.Message}");
                    }
                }
            }
            if (!foundBinary) result.Warnings.Add($"VST3 包缺少可读取的 Windows 插件文件：{bundle}");
        }

        void InspectFile(string path, string? bundle = null)
        {
            string extension = Path.GetExtension(path);
            bool executable = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
            if (!executable && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase)) return;
            if (executable && !KnownFileName(Path.GetFileNameWithoutExtension(path), "Vital")) return;
            if (!seenBinaries.Add(path)) return;
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length is < 64 or > MaximumBinaryBytes) throw new InvalidDataException("插件文件大小无效或超过 512 MB 元数据检查限制。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = reader.PEHeaders;
            if (headers.PEHeader is null) throw new InvalidDataException("不是 Windows PE 插件文件。");
            var architecture = headers.CoffHeader.Machine switch
            {
                Machine.I386 when headers.PEHeader.Magic == PEMagic.PE32 => PluginArchitecture.X86,
                Machine.Amd64 when headers.PEHeader.Magic == PEMagic.PE32Plus => PluginArchitecture.X64,
                Machine.Arm64 when headers.PEHeader.Magic == PEMagic.PE32Plus => PluginArchitecture.Arm64,
                _ => PluginArchitecture.Unknown
            };
            if (architecture == PluginArchitecture.Unknown) throw new InvalidDataException("不支持或不一致的 PE 位数 / 架构。");
            bool dll = (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0;
            if (dll == executable) throw new InvalidDataException("PE 文件类型与插件扩展名不一致。");
            var exports = executable ? new HashSet<string>(StringComparer.Ordinal) : ReadPluginExports(stream, headers, token);
            var metadata = FileVersionInfo.GetVersionInfo(path);
            if (IsSerumEffect(Path.GetFileNameWithoutExtension(path)) || IsSerumEffect(metadata.ProductName ?? "") || IsSerumEffect(metadata.FileDescription ?? "")) return;
            PluginBinaryFormat format;
            string entry;
            if (executable)
            {
                format = PluginBinaryFormat.Standalone;
                entry = "Windows 独立程序";
            }
            else if (exports.Contains("GetPluginFactory"))
            {
                format = PluginBinaryFormat.Vst3;
                entry = "GetPluginFactory";
            }
            else if (bundle is null && extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                (exports.Contains("VSTPluginMain") || exports.Contains("main")))
            {
                format = PluginBinaryFormat.Vst2;
                entry = exports.Contains("VSTPluginMain") ? "VSTPluginMain" : "main";
            }
            else
            {
                if (bundle is not null || extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase))
                    result.Warnings.Add($"插件未导出 VST3 工厂入口，已跳过：{path}");
                return;
            }
            SynthKind synth = Identify(Path.GetFileNameWithoutExtension(path), metadata);
            if (executable && synth != SynthKind.Vital) return;
            string filePath = format == PluginBinaryFormat.Vst3 ? bundle ?? Vst3Container(path) : path;
            string version = VersionText(metadata);
            string name = synth == SynthKind.Unknown ? (string.IsNullOrWhiteSpace(metadata.ProductName) ? Path.GetFileNameWithoutExtension(filePath) : metadata.ProductName.Trim()) : PresetDetection.SynthName(synth);
            string reason = synth == SynthKind.Unknown
                ? $"检测到 {entry} 导出入口，但产品元数据不足以确认已支持的音源；文件名不作为唯一身份依据。"
                : $"文件名与产品 / 厂商版本元数据对应 {name}，PE 架构为 {architecture}，入口为 {entry}；未实例化插件，渲染兼容性尚待验证。";
            result.Plugins.Add(new DiscoveredPlugin
            {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant(),
                Synth = synth, FilePath = filePath, BinaryPath = path, Version = version,
                Architecture = architecture, Format = format, Name = name, Reason = reason
            });
        }
    }

    private static SynthKind IdentifyMac(string fileName, string product, string identifier, string version)
    {
        string id = identifier.ToLowerInvariant();
        bool Vendor(string prefix) => id.StartsWith(prefix + ".", StringComparison.Ordinal);
        bool Product(string name) => KnownFileName(fileName, name) && KnownFileName(product, name) &&
            id.Split('.').Any(part => KnownFileName(part, name));
        if ((Vendor("com.xfer") || Vendor("com.xferrecords")) && (Product("Serum") || Product("Serum2")))
        {
            string major = version.Split('.')[0];
            return major switch { "1" when !Product("Serum2") => SynthKind.Serum1, "2" => SynthKind.Serum2, _ => SynthKind.Unknown };
        }
        if (Vendor("com.lennardigital") && Product("Sylenth1")) return SynthKind.Sylenth1;
        if ((Vendor("com.matttytel") || Vendor("com.vitalaudio") || Vendor("audio.vital")) && Product("Vital")) return SynthKind.Vital;
        return SynthKind.Unknown;
    }

    private static HashSet<string> ReadPluginExports(FileStream stream, PEHeaders headers, CancellationToken token)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var directory = headers.PEHeader!.ExportTableDirectory;
        if (directory.RelativeVirtualAddress == 0 || directory.Size == 0) return found;
        if (directory.Size < 40) throw new InvalidDataException("PE 导出目录截断。");
        byte[] data = ReadRva(stream, headers, (uint)directory.RelativeVirtualAddress, 40);
        uint functions = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24));
        uint addresses = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
        uint names = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32));
        uint ordinals = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(36));
        if (count > MaximumExportNames || functions > 65536) throw new InvalidDataException("PE 导出表数量无效或过大。");
        for (uint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(ReadRva(stream, headers, checked(names + index * 4), 4));
            var text = new StringBuilder();
            bool terminated = false;
            for (uint offset = 0; offset < 256; offset++)
            {
                byte value = ReadRva(stream, headers, checked(nameRva + offset), 1)[0];
                if (value == 0) { terminated = true; break; }
                if (value > 127) throw new InvalidDataException("PE 导出名称不是有效 ASCII。");
                text.Append((char)value);
            }
            if (!terminated) throw new InvalidDataException("PE 导出名称过长或未结束。");
            string name = text.ToString();
            if (name is not ("VSTPluginMain" or "main" or "GetPluginFactory")) continue;
            ushort ordinal = BinaryPrimitives.ReadUInt16LittleEndian(ReadRva(stream, headers, checked(ordinals + index * 2), 2));
            if (ordinal >= functions) throw new InvalidDataException("PE 导出序号超出函数表。");
            uint function = BinaryPrimitives.ReadUInt32LittleEndian(ReadRva(stream, headers, checked(addresses + ordinal * 4u), 4));
            if (function != 0 && function < headers.PEHeader.SizeOfImage) found.Add(name);
        }
        return found;
    }

    private static byte[] ReadRva(FileStream stream, PEHeaders headers, uint rva, int count)
    {
        long position = -1;
        if ((ulong)rva + (uint)count <= (uint)headers.PEHeader!.SizeOfHeaders) position = rva;
        else
        {
            foreach (var section in headers.SectionHeaders)
            {
                long offset = (long)rva - section.VirtualAddress;
                if (offset >= 0 && offset + count <= section.SizeOfRawData)
                {
                    position = section.PointerToRawData + offset;
                    break;
                }
            }
        }
        if (position < 0 || position + count > stream.Length) throw new InvalidDataException("PE 导出表地址超出文件范围。");
        byte[] bytes = new byte[count];
        stream.Position = position;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static SynthKind Identify(string fileName, FileVersionInfo metadata)
    {
        bool xfer = (metadata.CompanyName ?? "").Contains("Xfer", StringComparison.OrdinalIgnoreCase);
        if ((KnownFileName(fileName, "Serum") || KnownFileName(fileName, "Serum2")) && xfer && MetadataNames(metadata, "Serum"))
        {
            int major = metadata.FileMajorPart > 0 ? metadata.FileMajorPart : metadata.ProductMajorPart;
            return major switch { 1 => SynthKind.Serum1, 2 => SynthKind.Serum2, _ => SynthKind.Unknown };
        }
        if (KnownFileName(fileName, "Sylenth1") && (metadata.CompanyName ?? "").Contains("Lennar", StringComparison.OrdinalIgnoreCase) && MetadataNames(metadata, "Sylenth1"))
            return SynthKind.Sylenth1;
        if (KnownFileName(fileName, "Vital") && MetadataNames(metadata, "Vital")) return SynthKind.Vital;
        return SynthKind.Unknown;
    }

    private static bool MetadataNames(FileVersionInfo metadata, string name) =>
        (metadata.ProductName ?? "").Contains(name, StringComparison.OrdinalIgnoreCase) ||
        (metadata.FileDescription ?? "").Contains(name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileNameWithoutExtension(metadata.OriginalFilename ?? ""), name, StringComparison.OrdinalIgnoreCase);

    private static bool KnownFileName(string fileName, string product)
    {
        string compact = Compact(fileName);
        string expected = Compact(product);
        if (!compact.StartsWith(expected, StringComparison.Ordinal)) return false;
        return compact[expected.Length..] is "" or "x64" or "x86" or "64" or "32" or "vst2" or "vst3" or "standalone";
    }

    private static bool IsSerumEffect(string text)
    {
        string compact = Compact(text);
        return compact.Contains("serumfx", StringComparison.Ordinal) || compact.Contains("serum2fx", StringComparison.Ordinal) ||
            compact.Contains("serumeffect", StringComparison.Ordinal) || compact.Contains("serum2effect", StringComparison.Ordinal);
    }

    private static string Compact(string text) => text.Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string VersionText(FileVersionInfo metadata)
    {
        bool fileVersion = !string.IsNullOrWhiteSpace(metadata.FileVersion);
        string version = (fileVersion ? metadata.FileVersion : metadata.ProductVersion ?? "")!.Trim();
        if (!version.Contains(',')) return version;
        return fileVersion ? $"{metadata.FileMajorPart}.{metadata.FileMinorPart}.{metadata.FileBuildPart}.{metadata.FilePrivatePart}" :
            $"{metadata.ProductMajorPart}.{metadata.ProductMinorPart}.{metadata.ProductBuildPart}.{metadata.ProductPrivatePart}";
    }

    private static string Vst3Container(string path)
    {
        var directory = Directory.GetParent(path);
        if (directory?.Parent?.Name.Equals("Contents", StringComparison.OrdinalIgnoreCase) == true &&
            directory.Parent.Parent?.Extension.Equals(".vst3", StringComparison.OrdinalIgnoreCase) == true)
            return directory.Parent.Parent.FullName;
        return path;
    }

    private static bool IsLink(string path, PluginScanResult result)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return false;
        result.Warnings.Add($"已跳过插件目录或文件链接：{path}");
        return true;
    }

    private static bool IsFileError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or BadImageFormatException or Win32Exception or OverflowException;
}
