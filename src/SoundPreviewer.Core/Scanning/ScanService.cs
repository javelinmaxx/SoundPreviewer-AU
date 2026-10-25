using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Core.Scanning;

public sealed class ScanService
{
    private const long MaximumVitalBytes = 16 * 1024 * 1024;
    private const int MaximumBankEntries = 4096;
    private const long MaximumBankExpandedBytes = 256 * 1024 * 1024;
    private readonly PresetCategoryService categories;

    public ScanService(PresetCategoryService? categories = null) => this.categories = categories ?? new PresetCategoryService();

    public static void AssignSynth(PresetRecord record, SynthKind synth)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enum.IsDefined(synth) || synth == SynthKind.Unknown)
            throw new ArgumentException("请选择已支持的音源。", nameof(synth));
        string extension = Path.GetExtension(record.SourcePath);
        if (!Supports(synth, extension))
            throw new ArgumentException($"{PresetDetection.SynthName(synth)} 不能处理 {extension} 格式，音源指定未变更。", nameof(synth));
        bool changed = record.Synth != synth;
        if (changed)
        {
            bool missing = record.Status == PresetStatus.Missing;
            record.Synth = synth;
            ClearPreview(record);
            record.Attempts = 0;
            record.AttemptedCacheKey = "";
            record.Status = missing ? PresetStatus.Missing : PresetStatus.Pending;
            record.Message = "已手动指定音源，等待验证实际预制加载。";
        }
        record.SynthAssignedManually = true;
        record.DetectionReason = "用户手动指定为 " + PresetDetection.SynthName(synth) + "。";
        PresetMetadataService.UpdateAutomaticTags(record, PresetDetection.MergeTags(record.AutomaticTags ?? record.Tags, synth, record.SourcePath));
        if (PresetDetection.IsSpireFormat(extension))
        {
            if (record.Status != PresetStatus.Missing) record.Status = PresetStatus.Unsupported;
            record.Message = SpireFormatMessage(extension);
            ClearPreview(record);
        }
        if (extension.Equals(".fxb", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(record.InternalPath))
        {
            if (synth == SynthKind.Vital && extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase) && record.InternalPath.Length > 0 && !changed)
                return; // Retain the scanner's embedded-resource validation result.
            if (record.Status != PresetStatus.Missing) record.Status = PresetStatus.Unsupported;
            record.Message = "无法预览：此银行条目尚未通过资源及槽位检查；请在音源中导出独立音色，再添加导出目录并扫描。";
        }
    }

    public Task<ScanResult> ScanAsync(IReadOnlyList<SourceFolder> sources, IReadOnlyList<PresetRecord> existing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(existing);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceSnapshot = sources.Select(source => new SourceFolder { Id = source.Id, Path = source.Path, Synth = source.Synth, AutoDetect = source.AutoDetect, Enabled = source.Enabled }).ToList();
        var recordSnapshot = existing.ToList();
        return Task.Run(() => Scan(sourceSnapshot, recordSnapshot, categories, cancellationToken), cancellationToken);
    }

    private static ScanResult Scan(IReadOnlyList<SourceFolder> sources, IReadOnlyList<PresetRecord> existing, PresetCategoryService categories, CancellationToken token)
    {
        var warnings = new List<string>();
        bool categorize = false;
        try
        {
            var loaded = categories.Load();
            categorize = loaded.Success;
            if (!loaded.Success) warnings.Add(loaded.Error);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { warnings.Add("分类规则不可用，已有分类保持不变：" + error.Message); }
        var records = existing.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Clone(g.First()), StringComparer.Ordinal);
        var previousLocations = records.Values.GroupBy(LocationKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var scannedIds = new HashSet<string>(StringComparer.Ordinal);
        var scannedFiles = new HashSet<string>(ApplicationPaths.PathComparer);
        var scannedFileContexts = new HashSet<string>(ApplicationPaths.PathComparer);
        var completedSources = new HashSet<string>(StringComparer.Ordinal);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceDefinitions = sources.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var enabledSources = sources.Where(s => s.Enabled).OrderBy(s => s.AutoDetect).ToList();
        int added = 0;
        int changed = 0;
        foreach (var source in enabledSources)
        {
            token.ThrowIfCancellationRequested();
            try { roots[source.Id] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                warnings.Add($"音色来源路径无效：{source.Path}；{ex.Message}");
            }
        }

        foreach (var source in enabledSources)
        {
            token.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(source.Id, out string? root)) continue;
            if (!Directory.Exists(root))
            {
                warnings.Add($"音色来源不可访问，保留已有记录：{root}");
                continue;
            }
            bool complete = true;
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        complete = false;
                        warnings.Add($"已跳过目录链接，保留已有记录：{directory}");
                        continue;
                    }
                    foreach (string path in directory.EndsWith(".patch", StringComparison.OrdinalIgnoreCase)
                        ? new[] { directory } : Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            warnings.Add($"无法读取文件属性：{path}；{ex.Message}");
                            complete = false;
                            continue;
                        }
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            complete = false;
                            warnings.Add($"已跳过文件或目录链接，保留已有记录：{path}");
                            continue;
                        }
                        if ((attributes & FileAttributes.Directory) != 0 && !path.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
                        {
                            directories.Push(path);
                            continue;
                        }
                        if (!(source.AutoDetect ? PresetDetection.SupportsAny(Path.GetExtension(path)) : Supports(source.Synth, Path.GetExtension(path)))) continue;
                        string fileContext = (source.AutoDetect ? "auto" : source.Synth.ToString()) + "|" + path;
                        if (source.AutoDetect ? scannedFiles.Contains(path) : scannedFileContexts.Contains(fileContext)) continue;
                        try
                        {
                            var candidates = ReadPresets(source, root, path, token, warnings);
                            scannedFiles.Add(path);
                            scannedFileContexts.Add(fileContext);
                            foreach (var candidate in candidates)
                            {
                                string location = LocationKey(candidate);
                                PresetRecord? previous = null;
                                if (previousLocations.TryGetValue(location, out var matches))
                                {
                                    var eligible = matches.Where(p => !scannedIds.Contains(p.Id) && (source.AutoDetect || p.Id == candidate.Id || p.SourceId == source.Id ||
                                        p.Synth == SynthKind.Unknown || p.Status == PresetStatus.Unidentified ||
                                        (sourceDefinitions.TryGetValue(p.SourceId, out var owner) ? owner.AutoDetect : p.Synth == candidate.Synth))).ToList();
                                    previous = eligible.FirstOrDefault(p => p.SynthAssignedManually) ?? eligible.FirstOrDefault(p => p.Id == candidate.Id) ??
                                        eligible.FirstOrDefault(p => p.SourceId == source.Id) ?? eligible.FirstOrDefault(p => p.Synth == candidate.Synth) ?? eligible.FirstOrDefault();
                                }
                                if (previous is not null)
                                {
                                    candidate.Id = previous.Id;
                                    PresetMetadataService.PreserveEdits(previous, candidate);
                                    if (previous.SynthAssignedManually)
                                    {
                                        string automaticReason = candidate.DetectionReason;
                                        AssignSynth(candidate, previous.Synth);
                                        candidate.DetectionReason += " 自动识别线索：" + automaticReason;
                                    }
                                    PresetMetadataService.UpdateAutomaticTags(candidate, PresetDetection.MergeTags(previous.AutomaticTags ?? previous.Tags, candidate.Synth, candidate.SourcePath));
                                    scannedIds.Add(candidate.Id);
                                    if (previous.Synth == candidate.Synth && string.Equals(previous.ContentHash, candidate.ContentHash, StringComparison.Ordinal))
                                    {
                                        RefreshLocation(previous, candidate);
                                        if (candidate.Status is PresetStatus.Unsupported or PresetStatus.Unidentified)
                                        {
                                            previous.Status = candidate.Status;
                                            previous.Message = candidate.Message;
                                            ClearPreview(previous);
                                        }
                                        else if (previous.Status is PresetStatus.Unsupported or PresetStatus.Unidentified)
                                        {
                                            previous.Status = PresetStatus.Pending;
                                            previous.Message = candidate.Message;
                                            ClearPreview(previous);
                                        }
                                        else if (previous.Status == PresetStatus.Missing)
                                        {
                                            previous.Status = candidate.Status;
                                            previous.Message = candidate.Message;
                                            if (candidate.Status == PresetStatus.Pending && File.Exists(previous.PreviewPath))
                                            {
                                                previous.Status = PresetStatus.NeedsReview;
                                                previous.Message = "源音色已重新出现，保留的预览须重新试听确认。";
                                            }
                                            else ClearPreview(previous);
                                        }
                                    }
                                    else
                                    {
                                        candidate.Favorite = previous.Favorite;
                                        candidate.Feedback = previous.Feedback;
                                        candidate.FeedbackUtc = previous.FeedbackUtc;
                                        records[candidate.Id] = candidate;
                                        changed++;
                                    }
                                }
                                else
                                {
                                    scannedIds.Add(candidate.Id);
                                    records.Add(candidate.Id, candidate);
                                    added++;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
                        {
                            warnings.Add($"无法完整读取音色，保留已有记录：{path}；{ex.Message}");
                            complete = false;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法完整扫描目录，保留已有记录：{directory}；{ex.Message}");
                    complete = false;
                }
            }
            if (complete) completedSources.Add(source.Id);
        }

        token.ThrowIfCancellationRequested();
        foreach (var record in records.Values)
        {
            if (completedSources.Contains(record.SourceId) && !scannedIds.Contains(record.Id))
            {
                record.Status = PresetStatus.Missing;
                record.Message = "源音色已移除或不再属于此来源。";
            }
        }
        if (categorize) categories.Apply(records.Values, token);
        int duplicates = PresetIndex.RebuildDuplicates(records.Values, sources);
        return new ScanResult
        {
            Presets = records.Values.OrderBy(p => p.Synth).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList(),
            Warnings = warnings,
            Added = added,
            Changed = changed,
            Duplicates = duplicates
        };
    }

    private static bool Supports(SynthKind synth, string extension)
    {
        if (synth == SynthKind.Unknown) return false;
        if (synth == SynthKind.LogicPatch) return extension.Equals(".patch", StringComparison.OrdinalIgnoreCase);
        if (extension.Equals(".patch", StringComparison.OrdinalIgnoreCase)) return false;
        if (synth == SynthKind.Vital) return extension.Equals(".vital", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase);
        if (synth == SynthKind.Spire && PresetDetection.IsSpireFormat(extension)) return true;
        if (extension.Equals(".fxp", StringComparison.OrdinalIgnoreCase)) return true;
        return synth == SynthKind.Serum2 && extension.Equals(".SerumPreset", StringComparison.OrdinalIgnoreCase) ||
            synth == SynthKind.Sylenth1 && extension.Equals(".fxb", StringComparison.OrdinalIgnoreCase);
    }

    private static List<PresetRecord> ReadPresets(SourceFolder source, string root, string path, CancellationToken token, List<string> warnings)
    {
        if (path.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
        {
            var patchDetection = new SynthDetection(SynthKind.LogicPatch, "依据 Logic .patch 包识别；实际 AU 链兼容性将在渲染时检查。");
            if (!Directory.Exists(path))
            {
                var invalid = CreateRecord(source, patchDetection, path, Path.GetFileNameWithoutExtension(path), Path.GetFileName(root),
                    Fingerprints.HashFile(path, token), "", new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
                invalid.Status = PresetStatus.Unsupported;
                invalid.Message = "Logic .patch 必须是包目录，不能是平面文件。";
                return [invalid];
            }
            var snapshot = LogicPatchSource.Read(path, token);
            string packageName = Path.GetDirectoryName(Path.GetRelativePath(root, path)) ?? "";
            var patch = CreateRecord(source, patchDetection, path, Path.GetFileNameWithoutExtension(path),
                string.IsNullOrEmpty(packageName) || packageName == "." ? Path.GetFileName(root) : packageName,
                snapshot.Sha256, snapshot.Md5, snapshot.Bytes, snapshot.ModifiedUtc);
            patch.Message = "Logic AU 链预览；需要 macOS、已安装的 AU 与 Logic Patch 生成环境。";
            return [patch];
        }
        var file = new FileInfo(path);
        long fileSize = file.Length;
        if (file.Extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase) && fileSize > VitalBankPresetSource.MaximumBankBytes)
            throw new InvalidDataException("无法预览：Vital 音色包超过 256 MiB 读取限制。" + VitalBankPresetSource.ExportInstructions);
        DateTime lastWriteUtc = file.LastWriteTimeUtc;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var fileHashes = Hash(stream, token);
        string fileHash = fileHashes.Sha256;
        stream.Position = 0;
        string extension = file.Extension;
        VstEnvelope? envelope = extension.Equals(".fxp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".fxb", StringComparison.OrdinalIgnoreCase)
            ? PresetDetection.ReadEnvelope(stream, fileSize, extension.Equals(".fxb", StringComparison.OrdinalIgnoreCase)) : null;
        var detection = source.AutoDetect ? PresetDetection.Detect(path, root, envelope) : new SynthDetection(source.Synth, "扫描来源已指定为 " + PresetDetection.SynthName(source.Synth) + "。");
        string package = Path.GetDirectoryName(Path.GetRelativePath(root, path)) ?? "";
        if (package.Length == 0 || package == ".") package = Path.GetFileName(root);

        if (extension.Equals(".vitalbank", StringComparison.OrdinalIgnoreCase))
        {
            var bankRecords = new List<PresetRecord>();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries.Where(e => Path.GetExtension(e.FullName).Equals(".vital", StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count > MaximumBankEntries || entries.Any(e => e.Length > MaximumVitalBytes) || entries.Sum(e => e.Length) > MaximumBankExpandedBytes)
                throw new InvalidDataException("Vital 音色包超过安全读取大小限制。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                string internalPath = entry.FullName.Replace('\\', '/');
                if (internalPath.StartsWith('/') || internalPath.Split('/').Any(part => part.Length == 0 || part is ".." or "." || part.Contains(':')))
                    throw new InvalidDataException("Vital 音色包含不安全的内部路径。");
                if (!names.Add(internalPath)) throw new InvalidDataException("Vital 音色包含重复的内部路径。");
                if (entry.Length > MaximumVitalBytes) throw new InvalidDataException("Vital 音色包中的预设超过安全读取大小限制。");
                using var entryStream = entry.Open();
                using var content = new MemoryStream();
                CopyBounded(entryStream, content, MaximumVitalBytes, token);
                content.Position = 0;
                string entryHash = Hash(content, token).Sha256;
                content.Position = 0;
                var record = CreateRecord(source, detection, path, Path.GetFileNameWithoutExtension(internalPath), Path.GetFileNameWithoutExtension(path), Fingerprints.HashText($"{fileHash}|{internalPath}|{entryHash}"), fileHashes.Md5, entry.Length, lastWriteUtc, internalPath);
                try
                {
                    ValidateVital(content, record);
                    content.Position = 0;
                    using var document = JsonDocument.Parse(content);
                    VitalBankPresetSource.ValidateEmbeddedResources(document.RootElement);
                    record.Message = "已核对包内预制及内嵌采样、波表；生成时只读取此条目，不导入或更改 Vital 资源目录。";
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    record.Status = PresetStatus.Unsupported;
                    record.Message = ex is InvalidDataException ? ex.Message : $"无法预览：Vital 音色包条目无效：{ex.Message}。" + VitalBankPresetSource.ExportInstructions;
                    warnings.Add($"{path} / {internalPath}：{record.Message}");
                }
                bankRecords.Add(record);
            }
            if (entries.Count == 0)
            {
                var empty = CreateRecord(source, detection, path, Path.GetFileNameWithoutExtension(path), package, fileHash, fileHashes.Md5, fileSize, lastWriteUtc);
                empty.Status = PresetStatus.Unsupported;
                empty.Message = "无法预览：此 Vital 音色包没有可识别的 .vital 条目。" + VitalBankPresetSource.ExportInstructions;
                bankRecords.Add(empty);
            }
            VerifyStable(file, fileSize, lastWriteUtc);
            return bankRecords;
        }

        var preset = CreateRecord(source, detection, path, Path.GetFileNameWithoutExtension(path), package, fileHash, fileHashes.Md5, fileSize, lastWriteUtc);
        if (preset.Synth == SynthKind.Unknown)
        {
            preset.Status = PresetStatus.Unidentified;
            preset.Message = detection.Reason;
            VerifyStable(file, fileSize, lastWriteUtc);
            return [preset];
        }
        if (PresetDetection.IsSpireFormat(extension))
        {
            preset.Status = PresetStatus.Unsupported;
            preset.Message = SpireFormatMessage(extension);
        }
        else if (extension.Equals(".vital", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (fileSize > MaximumVitalBytes) throw new JsonException("Vital 预设超过安全读取大小限制。");
                ValidateVital(stream, preset);
            }
            catch (JsonException ex)
            {
                preset.Status = PresetStatus.Unsupported;
                preset.Message = $"Vital 预设 JSON 无效：{ex.Message}";
                warnings.Add($"{path}：{preset.Message}");
            }
        }
        else if (extension.Equals(".fxb", StringComparison.OrdinalIgnoreCase))
        {
            preset.Status = PresetStatus.Unsupported;
            preset.Message = "FXB 为不透明音色库；需要原生适配器确认音色槽位，暂不拆分或生成预览。";
            if (!string.IsNullOrWhiteSpace(envelope?.Diagnostic)) preset.Message += " " + envelope.Diagnostic;
        }
        else if (extension.Equals(".fxp", StringComparison.OrdinalIgnoreCase))
        {
            if (envelope?.IsValid != true)
            {
                preset.Status = PresetStatus.Unsupported;
                preset.Message = "FXP 文件头无效或此文件实际为音色库。";
                warnings.Add($"{path}：{preset.Message}");
            }
            else if (!string.IsNullOrWhiteSpace(envelope.Diagnostic))
                preset.Message = envelope.Diagnostic + " 实际预制加载仍须由音源确认。";
        }
        VerifyStable(file, fileSize, lastWriteUtc);
        return [preset];
    }

    private static string SpireFormatMessage(string extension) => extension.Equals(".sbf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".swzip", StringComparison.OrdinalIgnoreCase)
        ? "已索引 Spire 音色库；原生格式加载、音色槽位及资源依赖尚未适配，不拆分或生成预览。"
        : "已索引 Spire 专用预设；原生格式加载尚未适配，不生成预览。";

    private static void VerifyStable(FileInfo file, long length, DateTime lastWriteUtc)
    {
        file.Refresh();
        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != lastWriteUtc)
            throw new IOException("扫描过程中源文件发生变化，请重新扫描。");
    }

    private static void ValidateVital(Stream stream, PresetRecord preset)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            throw new JsonException("缺少有效的 settings 对象。");
        if (root.TryGetProperty("preset_name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            preset.Name = name.GetString()!;
    }

    private static PresetRecord CreateRecord(SourceFolder source, SynthDetection detection, string path, string name, string package, string hash, string md5, long size, DateTime modified, string internalPath = "") => new()
    {
        Id = Fingerprints.PresetId(source.AutoDetect ? SynthKind.Unknown : source.Synth, path, internalPath),
        SourceId = source.Id,
        Synth = detection.Synth,
        DetectionReason = detection.Reason,
        Tags = PresetDetection.Tags(detection.Synth, path),
        SourcePath = Path.GetFullPath(path),
        InternalPath = internalPath,
        Name = name,
        Package = package,
        ContentHash = hash,
        ContentMd5 = md5,
        FileSize = size,
        LastWriteUtc = modified,
        Status = PresetStatus.Pending
    };

    private static void RefreshLocation(PresetRecord previous, PresetRecord candidate)
    {
        previous.SourceId = candidate.SourceId;
        previous.SourcePath = candidate.SourcePath;
        previous.Name = candidate.Name;
        previous.Package = candidate.Package;
        previous.FileSize = candidate.FileSize;
        previous.LastWriteUtc = candidate.LastWriteUtc;
        previous.ContentMd5 = candidate.ContentMd5;
        previous.DetectionReason = candidate.DetectionReason;
        previous.SynthAssignedManually = candidate.SynthAssignedManually;
        previous.Tags = candidate.Tags;
        previous.TagIds = candidate.TagIds;
        previous.CategoryIds = candidate.CategoryIds;
        previous.CategoryCatalogId = candidate.CategoryCatalogId;
        previous.TagCatalogId = candidate.TagCatalogId;
        previous.AutomaticTags = candidate.AutomaticTags;
    }

    private static void ClearPreview(PresetRecord preset)
    {
        preset.PreviewPath = "";
        preset.CacheKey = "";
        preset.RenderedUtc = null;
        preset.Metrics = null;
        preset.Notes = [];
        preset.AdapterVersion = "";
        preset.PluginVersion = "";
        preset.TemplateJson = "";
    }

    private static PresetRecord Clone(PresetRecord record) => JsonSerializer.Deserialize<PresetRecord>(JsonSerializer.Serialize(record, LibraryJson.Options), LibraryJson.Options)!;

    private static string LocationKey(PresetRecord record)
    {
        string path;
        try { path = ApplicationPaths.PathIdentity(record.SourcePath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { path = OperatingSystem.IsWindows() ? record.SourcePath.ToUpperInvariant() : record.SourcePath; }
        return JsonSerializer.Serialize(new { Path = path, record.InternalPath, record.BankSlot });
    }

    private static (string Sha256, string Md5) Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
            md5.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant());
    }

    private static void CopyBounded(Stream source, Stream destination, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            total += count;
            if (total > maximum) throw new InvalidDataException("音色条目展开后超过安全读取大小限制。");
            destination.Write(buffer, 0, count);
        }
    }
}
