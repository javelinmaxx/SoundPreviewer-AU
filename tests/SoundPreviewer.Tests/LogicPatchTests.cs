using System.Text.Json;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;
using SoundPreviewer.Worker;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task TestLogicPatchIndexAsync()
    {
        string directory = Fixture("logic-patch-index");
        string patch = Path.Combine(directory, "Nested", "Test sound.patch");
        Directory.CreateDirectory(patch);
        File.WriteAllText(Path.Combine(patch, "data.plist"), "TEST ONLY metadata");
        File.WriteAllText(Path.Combine(patch, "#Root.cst"), "TEST ONLY strip");
        File.WriteAllText(Path.Combine(patch, "internal.vital"), "{\"settings\":{}}");
        File.WriteAllText(Path.Combine(directory, "outside.vital"), "{\"settings\":{}}");
        var source = new SourceFolder { Id = "logic-auto", Path = directory, AutoDetect = true };
        var scanner = new ScanService();
        var scan = await scanner.ScanAsync([source], []);
        Equal(2, scan.Presets.Count, "A patch package is one preset; its internal files are not indexed separately.");
        var preset = scan.Presets.Single(record => record.Synth == SynthKind.LogicPatch);
        Equal(patch, preset.SourcePath, "The source identity is the enclosing .patch directory.");
        Equal(PresetStatus.Pending, preset.Status, "Package indexing does not claim actual AU loadability.");
        True(preset.ContentHash.Length == 64 && preset.ContentMd5.Length == 32, "All package members contribute SHA256 and MD5 identities.");
        var explicitSource = new SourceFolder { Id = "logic-explicit", Path = patch, Synth = SynthKind.LogicPatch };
        var direct = await scanner.ScanAsync([explicitSource], []);
        Equal(1, direct.Presets.Count, "A source can directly select the .patch package itself.");
        Equal(preset.ContentHash, direct.Presets[0].ContentHash, "Explicit and automatic discovery use identical package fingerprints.");
        Throws<ArgumentException>(() => ScanService.AssignSynth(preset, SynthKind.Vital));
        ScanService.AssignSynth(preset, SynthKind.LogicPatch);
        string previousHash = preset.ContentHash;
        preset.Favorite = true;
        File.WriteAllText(Path.Combine(patch, "#Root.cst"), "TEST ONLY updated effect state");
        scan = await scanner.ScanAsync([source], [preset]);
        preset = scan.Presets.Single(record => record.Synth == SynthKind.LogicPatch);
        True(preset.ContentHash != previousHash && preset.Favorite, "Editing strip state changes the cache identity while preserving user metadata.");
        var missing = await scanner.ScanAsync([source], scan.Presets);
        True(missing.Presets.All(record => record.Status != PresetStatus.Missing), "Directory packages remain available on a subsequent scan.");
        string invalid = Path.Combine(directory, "flat.patch");
        File.WriteAllText(invalid, "not a package");
        scan = await scanner.ScanAsync([source], scan.Presets);
        Equal(PresetStatus.Unsupported, scan.Presets.Single(record => record.SourcePath == invalid).Status, "Flat .patch files have an explicit unsupported result.");
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(patch, "linked-asset");
            File.CreateSymbolicLink(link, Path.Combine(patch, "data.plist"));
            Throws<InvalidDataException>(() => LogicPatchSource.Read(patch));
            var retained = await scanner.ScanAsync([source], scan.Presets);
            True(retained.Warnings.Count > 0 && retained.Presets.Any(record => record.Id == preset.Id && record.Status != PresetStatus.Missing),
                "Unreadable or linked package content preserves the existing index with a diagnostic.");
            File.Delete(link);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => LogicPatchSource.Read(patch, cancellation.Token));
    }

    private static Task TestLogicPatchConfigurationAsync()
    {
        Equal(4, (int)SynthKind.Unknown, "Logic Patch does not reinterpret legacy Unknown values.");
        Equal(6, (int)SynthKind.LogicPatch, "Logic Patch appends a persisted synth identity.");
        Equal(5, (int)RenderBackend.LogicPatch, "Logic Patch appends a persisted backend identity.");
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"Plugins\":[]}", LibraryJson.Options)!;
        var plugin = settings.Plugins.Single(item => item.Synth == SynthKind.LogicPatch);
        Equal(RenderBackend.Unconfigured, plugin.Backend, "Migrated settings do not invent a prepared AU environment.");
        True(plugin.AllowLogicPatchApproximation, "AU-chain approximation is enabled by default.");
        var explicitStrict = JsonSerializer.Deserialize<PluginConfiguration>("{\"AllowLogicPatchApproximation\":false}", LibraryJson.Options)!;
        True(!explicitStrict.AllowLogicPatchApproximation, "An explicitly saved strict setting remains off.");
        True(!RenderReadinessService.Evaluate(plugin, [], PluginPlatform.MacOS).CanAttemptRender, "Missing environment blocks rendering.");
        string directory = Fixture("logic-patch-configuration");
        plugin.Backend = RenderBackend.LogicPatch;
        plugin.ExecutablePath = Path.Combine(directory, "LogicPreview");
        plugin.PluginPath = Path.Combine(directory, "logic_patch_desktop.py");
        foreach (string name in new[] { "LogicPreview", "logic_patch_desktop.py", "logic_patch_inspector.py", "logic_patch_preview.py" })
            File.WriteAllText(Path.Combine(directory, name), "TEST ONLY; not executable");
        True(RenderReadinessService.Evaluate(plugin, [], PluginPlatform.MacOS).CanAttemptRender, "Prepared configuration can attempt per-patch AU checks.");
        True(!RenderReadinessService.Evaluate(plugin, [], PluginPlatform.Windows).CanAttemptRender, "AU rendering is Mac-only.");
        string hash = Fingerprints.RendererExecutableHash(plugin);
        plugin.AllowLogicPatchApproximation = false;
        True(Fingerprints.RendererExecutableHash(plugin) != hash, "Approximation policy participates in render cache identity.");
        hash = Fingerprints.RendererExecutableHash(plugin);
        File.WriteAllText(Path.Combine(directory, "logic_patch_inspector.py"), "TEST ONLY updated parser");
        True(Fingerprints.RendererExecutableHash(plugin) != hash, "Changing the parser invalidates render identity.");
        plugin.Synth = SynthKind.Vital;
        Throws<ArgumentException>(() => RenderReadinessService.ValidateSettings([plugin]));
        True(!RenderReadinessService.Evaluate(plugin, [], PluginPlatform.MacOS).CanAttemptRender, "The AU bridge cannot be assigned to a different synth.");
        return Task.CompletedTask;
    }

    private static async Task TestLogicPatchSkipQueueAsync()
    {
        string directory = Fixture("logic-native-skip-queue");
        var store = new LibraryStore(Path.Combine(directory, "library.sqlite"));
        store.Initialize();
        var settings = new AppSettings
        {
            PreviewDirectory = Path.Combine(directory, "previews"), PreviewFormat = PreviewAudioFormat.Wav,
            MaxAttempts = 3, MaxParallelRenders = 1,
            Template = new PreviewTemplate { QuickPreview = false, HoldSeconds = 0.1, TailSeconds = 0.02 },
            Plugins = [new() { Synth = SynthKind.LogicPatch, Backend = RenderBackend.LogicPatch,
                ExecutablePath = TestExecutable, Version = "TEST ONLY skip protocol", TimeoutSeconds = 5 }]
        };
        Directory.CreateDirectory(settings.PreviewDirectory);
        var preset = Preset("skip-native-instrument");
        preset.Synth = SynthKind.LogicPatch;
        store.Upsert(preset);
        var progress = new InlineProgress();
        var queue = new RenderQueue(store, new TestRenderResourceProbe());
        var result = await queue.RunAsync(settings, [preset], TestExecutable, progress, CancellationToken.None);
        Equal(1, result.SkippedUnsupported, "Recognized native instruments are counted as skipped, not failed.");
        Equal(0, result.Failed, "Unsupported instrument discovery is not a render failure.");
        Equal(1, result.Processed, "Skipped jobs advance progress and finish the queue.");
        Equal(0, result.Remaining, "Skipped native patches do not remain pending forever.");
        Equal(PresetStatus.Unsupported, preset.Status, "Native instruments persist as unsupported without creating audio.");
        Equal(0, preset.Attempts, "Skipping a native instrument consumes no render attempts.");
        Equal("", preset.PreviewPath, "No substitute audio is published.");
        Equal(PresetStatus.Unsupported, store.LoadPresets().Single().Status, "The skip status survives persistence.");
        var repeated = await queue.RunAsync(settings, [preset], TestExecutable, new InlineProgress(), CancellationToken.None);
        Equal(0, repeated.Eligible, "A subsequent run excludes the unsupported patch instead of retrying.");
        string previous = Path.Combine(settings.PreviewDirectory, "user-existing.wav");
        CompletePreview(preset, previous, settings.Template);
        string hash = Fingerprints.HashFile(previous);
        store.Upsert(preset);
        result = await queue.RunAsync(settings, [preset], TestExecutable, new InlineProgress(), CancellationToken.None, forceRegenerate: true);
        Equal(1, result.SkippedUnsupported, "Force regeneration still skips native instruments.");
        Equal(hash, Fingerprints.HashFile(previous), "An existing preview is preserved byte-for-byte after the skip.");
        Equal(previous, preset.PreviewPath, "Previous preview metadata remains available.");
    }

    // CI supplies an Apple Sampler/low-pass patch exported by our native self-test.
    // This invokes the same separate .NET worker and Python/native bridge as the UI.
    private static async Task<int> TestLogicPatchNativeAsync(string patch, string nativeWorker, string bridge)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Native Logic integration needs macOS.");
        string directory = Path.Combine(Path.GetTempPath(), "logic-desktop-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var scan = await new ScanService().ScanAsync([new() { Path = Path.GetFullPath(patch), Synth = SynthKind.LogicPatch }], []);
            var preset = scan.Presets.Single();
            var plugin = new PluginConfiguration { Synth = SynthKind.LogicPatch, Backend = RenderBackend.LogicPatch,
                ExecutablePath = Path.GetFullPath(nativeWorker), PluginPath = Path.GetFullPath(bridge),
                Version = LogicPatchRuntime.AdapterVersion, TimeoutSeconds = 120, AllowParallelRendering = false };
            foreach (bool quick in new[] { false, true })
            {
                var template = new PreviewTemplate { QuickPreview = quick, HoldSeconds = 0.3, TailSeconds = 0.1, QuickPreviewSeconds = 0.2 };
                var result = await new WorkerProcessClient().RenderAsync(new()
                {
                    Preset = preset, Plugin = plugin, Template = template,
                    OutputPath = Path.Combine(directory, quick ? "quick.wav" : "fixed.wav")
                }, typeof(RenderDispatcher).Assembly.Location, CancellationToken.None);
                True(result.Success && result.NeedsReview, "The desktop bridge saves real AU audio without claiming Logic equivalence: " + result.Message);
                True(result.Message.Contains("Omitted Logic-native effect", StringComparison.Ordinal), "The native effect is explicitly omitted from the real AU-chain capture.");
                WorkerProcessClient.ValidateNotes(result, template);
                True(result.Metrics is { IsSilent: false, HasNonFinite: false }, "Actual AU audio has finite non-silent samples.");
                Equal(3, result.Notes.Count, "Desktop AU rendering maps all three requested registers.");
                True(File.Exists(result.OutputPath), "Validated desktop output is committed.");
            }
            string nativePatch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(patch))!, "TEST ONLY native instrument.patch");
            var nativeScan = await new ScanService().ScanAsync([new() { Path = nativePatch, Synth = SynthKind.LogicPatch }], []);
            string skippedOutput = Path.Combine(directory, "must-not-exist.wav");
            var skipped = await new WorkerProcessClient().RenderAsync(new()
            {
                Preset = nativeScan.Presets.Single(), Plugin = plugin, Template = new PreviewTemplate(), OutputPath = skippedOutput
            }, typeof(RenderDispatcher).Assembly.Location, CancellationToken.None);
            True(!skipped.Success && skipped.NonRetryable && skipped.SkippedUnsupported, "Native instrument skips propagate through the real desktop worker without retries.");
            True(!File.Exists(skippedOutput), "Native instruments produce no substitute preview WAV.");
            Console.WriteLine("PASS: native instrument skip, native effect omission, desktop .patch scanning, separate worker, Python/native AU rendering, fixed/quick note maps and committed WAVs.");
            return 0;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
