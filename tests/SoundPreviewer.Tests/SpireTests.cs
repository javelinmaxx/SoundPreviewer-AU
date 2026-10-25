using System.Text.Json;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestSpireFoundationAsync()
    {
        Equal(4, (int)SynthKind.Unknown, "Adding Spire must not reinterpret existing unidentified records.");
        Equal(5, (int)SynthKind.Spire, "Spire is appended after all existing persisted synth values.");
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"Plugins":[{"Synth":0,"Backend":1,"ExecutablePath":"legacy-vital"},
            {"Synth":1,"Version":"legacy-serum"},{"Synth":2},{"Synth":3}],
            "Sources":[{"Synth":4,"AutoDetect":true,"Path":"mixed-root"}]}
            """, LibraryJson.Options)!;
        Equal(6, settings.Plugins.Count, "Existing saved plugin lists gain the missing Spire configuration.");
        Equal("legacy-vital", settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital).ExecutablePath, "Migration preserves existing executable settings.");
        Equal("legacy-serum", settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).Version, "Migration preserves plugin metadata.");
        Equal(RenderBackend.Unconfigured, settings.Plugins.Single(plugin => plugin.Synth == SynthKind.Spire).Backend, "Migration never implies an installed or verified Spire adapter.");
        Equal(SynthKind.Unknown, settings.Sources.Single().Synth, "Existing mixed roots remain unidentified.");
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, LibraryJson.Options), LibraryJson.Options)!;
        Equal(6, copy.Plugins.Count, "Repeated loading does not duplicate the appended configuration.");

        string directory = Fixture("spire-foundation");
        string spireFolder = Path.Combine(directory, "Spire");
        string otherFolder = Path.Combine(directory, "Vital");
        Directory.CreateDirectory(spireFolder);
        Directory.CreateDirectory(otherFolder);
        string[] extensions = [".spf", ".SPF2", ".sbf", ".swzip"];
        foreach (string extension in extensions)
            File.WriteAllText(Path.Combine(otherFolder, "TEST-ONLY" + extension), "TEST ONLY: unparsed Spire format fixture " + extension);
        File.WriteAllText(Path.Combine(otherFolder, "not-a-real-format.sbf2"), "TEST ONLY: unrecognized suffix");
        string unknownPath = Path.Combine(spireFolder, "unknown.fxp");
        WriteFxp(unknownPath, 1, pluginId: "Spir");
        string serumPath = Path.Combine(spireFolder, "known-serum.fxp");
        WriteFxp(serumPath, 2, pluginId: "XfsX");
        var source = new SourceFolder { Id = "spire-auto", Path = directory, Synth = SynthKind.Unknown, AutoDetect = true };
        var scanner = new ScanService();
        var result = await scanner.ScanAsync([source], []);
        Equal(6, result.Presets.Count, "Only documented proprietary suffixes and generic FXP are indexed.");
        var proprietary = result.Presets.Where(preset => extensions.Contains(Path.GetExtension(preset.SourcePath), StringComparer.OrdinalIgnoreCase)).ToList();
        Equal(4, proprietary.Count, "All four documented Spire suffixes are indexed case-insensitively.");
        foreach (var preset in proprietary)
        {
            Equal(SynthKind.Spire, preset.Synth, "Spire's dedicated suffix takes priority over an unrelated Vital directory name.");
            Equal(PresetStatus.Unsupported, preset.Status, "Indexing an unverified proprietary payload cannot enable rendering.");
            True(preset.Tags.Count == 0, "Synth classification is separate from subjective tags, which start empty.");
            True(preset.Message.Contains("尚未适配", StringComparison.Ordinal), "The compatibility boundary is shown on every indexed proprietary preset.");
            True(preset.BankSlot is null && preset.InternalPath.Length == 0, "Opaque banks never invent child preset slots.");
            Equal("", preset.PreviewPath, "Indexing never creates synthetic preview audio.");
        }
        var unknown = result.Presets.Single(preset => preset.SourcePath == unknownPath);
        Equal(SynthKind.Unknown, unknown.Synth, "An unverified FXP ID cannot be guessed from its spelling or Spire directory.");
        Equal(PresetStatus.Unidentified, unknown.Status, "Unknown FXP remains available for explicit classification.");
        var serum = result.Presets.Single(preset => preset.SourcePath == serumPath);
        Equal(SynthKind.Serum1, serum.Synth, "A verified Serum ID wins over a Spire directory hint.");

        unknown.Favorite = true;
        unknown.Feedback = 1;
        string originalId = unknown.Id;
        ScanService.AssignSynth(unknown, SynthKind.Spire);
        Equal(originalId, unknown.Id, "Explicit Spire FXP classification preserves persistent record identity.");
        Equal(PresetStatus.Pending, unknown.Status, "Explicit FXP remains available for a user-configured external worker.");
        True(unknown.Favorite && unknown.Feedback == 1 && unknown.SynthAssignedManually, "Explicit classification preserves user feedback.");
        var rescanned = await scanner.ScanAsync([source], result.Presets);
        var manual = rescanned.Presets.Single(preset => preset.Id == originalId);
        Equal(SynthKind.Spire, manual.Synth, "An automatic rescan retains an explicit Spire FXP choice.");

        var nativePreset = proprietary.First();
        nativePreset.Status = PresetStatus.Ready;
        nativePreset.PreviewPath = "test-only-stale-preview.wav";
        nativePreset.CacheKey = "test-only-stale-cache";
        ScanService.AssignSynth(nativePreset, SynthKind.Spire);
        Equal(PresetStatus.Unsupported, nativePreset.Status, "Repeating a manual assignment cannot force an unsupported native format to render.");
        Equal("", nativePreset.PreviewPath, "Unsupported manual assignments clear stale derived previews.");
        Equal("", nativePreset.CacheKey, "Unsupported manual assignments invalidate stale cache identities.");
        var fixedSource = new SourceFolder { Id = "spire-fixed", Path = otherFolder, Synth = SynthKind.Spire };
        var fixedResult = await scanner.ScanAsync([fixedSource], []);
        Equal(4, fixedResult.Presets.Count, "An explicitly selected Spire source accepts all documented native suffixes.");
        True(fixedResult.Presets.All(preset => preset.Synth == SynthKind.Spire && preset.Status == PresetStatus.Unsupported), "Explicit source selection also respects the native-format adapter boundary.");

        var store = new LibraryStore(Path.Combine(directory, "spire-library.db"));
        store.Initialize();
        store.SaveSettings(settings);
        store.UpsertMany([manual, new PresetRecord { Id = "legacy-unknown", Synth = SynthKind.Unknown, Status = PresetStatus.Unidentified }]);
        Equal(SynthKind.Spire, store.LoadPresets().Single(preset => preset.Id == originalId).Synth, "Spire round trips through the existing SQLite schema.");
        Equal(SynthKind.Unknown, store.LoadPresets().Single(preset => preset.Id == "legacy-unknown").Synth, "Stored Unknown=4 does not collide with Spire=5.");
        Equal(6, store.LoadSettings().Plugins.Count, "Migrated configuration survives settings persistence.");
    }
}
