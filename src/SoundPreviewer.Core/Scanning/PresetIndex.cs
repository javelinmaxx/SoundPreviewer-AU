using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Scanning;

public static class PresetIndex
{
    public static int RebuildDuplicates(IEnumerable<PresetRecord> presets, IReadOnlyList<SourceFolder>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var records = presets.ToList();
        var enabledSources = sources?.Where(source => source.Enabled).Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        var available = records.ToDictionary(record => record.Id,
            record => (enabledSources is null || enabledSources.Contains(record.SourceId)) && record.Status != PresetStatus.Missing && (File.Exists(record.SourcePath) || record.Synth == SynthKind.LogicPatch && Directory.Exists(record.SourcePath)), StringComparer.Ordinal);
        foreach (var record in records) record.DuplicateOfId = "";
        int duplicates = 0;
        foreach (var group in records.Where(record => IsMd5(record.ContentMd5)).GroupBy(record =>
                     (Md5: record.ContentMd5.ToLowerInvariant(), record.Synth, record.InternalPath, record.BankSlot)))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;
            var previews = members.ToDictionary(record => record.Id, HasRecoverablePreview, StringComparer.Ordinal);
            var ordered = members.OrderByDescending(record => available[record.Id])
                .ThenByDescending(record => enabledSources is null || enabledSources.Contains(record.SourceId))
                .ThenByDescending(record => previews[record.Id])
                .ThenByDescending(record => record.Favorite)
                .ThenByDescending(record => record.Feedback != 0)
                .ThenBy(record => record.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Id, StringComparer.Ordinal).ToList();
            var canonical = ordered[0];
            if (available[canonical.Id] && (canonical.Status is PresetStatus.Pending or PresetStatus.Failed or PresetStatus.Ready or PresetStatus.NeedsReview) && !previews[canonical.Id])
            {
                // MD5 groups the index; SHA256 and render metadata guard reuse of audio.
                var donor = ordered.Skip(1).FirstOrDefault(record => !string.IsNullOrWhiteSpace(canonical.ContentHash) &&
                    record.ContentHash == canonical.ContentHash && previews[record.Id]);
                if (donor is not null)
                {
                    CopyPreview(donor, canonical);
                    canonical.Status = PresetStatus.NeedsReview;
                    canonical.Message = "已复用相同内容的已有预览，等待试听确认。";
                }
            }
            foreach (var duplicate in ordered.Skip(1))
            {
                duplicate.DuplicateOfId = canonical.Id;
                duplicates++;
            }
        }
        return duplicates;
    }

    internal static bool IsMd5(string? value) => value is { Length: 32 } && value.All(Uri.IsHexDigit);

    private static bool HasStoredPreview(PresetRecord record) =>
        !string.IsNullOrWhiteSpace(record.CacheKey) && !string.IsNullOrWhiteSpace(record.TemplateJson) && record.Notes is { Count: > 0 } && File.Exists(record.PreviewPath);

    internal static bool HasRecoverablePreview(PresetRecord record) => HasRecoverablePreview(record, CancellationToken.None);

    internal static bool HasRecoverablePreview(PresetRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasStoredPreview(record)) return false;
        try
        {
            var template = JsonSerializer.Deserialize<PreviewTemplate>(record.TemplateJson, LibraryJson.Options);
            if (template is null) return false;
            template.Validate();
            var metrics = PreviewAudioFile.Validate(record.PreviewPath, cancellationToken);
            if (metrics.SampleRate != template.SampleRate) return false;
            WorkerProcessClient.ValidateNotes(new RenderResult { Metrics = metrics, Notes = record.Notes, AdapterVersion = record.AdapterVersion }, template);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            return false;
        }
    }

    private static void CopyPreview(PresetRecord source, PresetRecord target)
    {
        target.PreviewPath = source.PreviewPath;
        target.CacheKey = source.CacheKey;
        target.RenderedUtc = source.RenderedUtc;
        target.Metrics = Clone(source.Metrics);
        target.Notes = Clone(source.Notes)!;
        target.AdapterVersion = source.AdapterVersion;
        target.PluginVersion = source.PluginVersion;
        target.TemplateJson = source.TemplateJson;
        target.Attempts = 0;
        target.AttemptedCacheKey = "";
    }

    private static T? Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, LibraryJson.Options), LibraryJson.Options);
}
