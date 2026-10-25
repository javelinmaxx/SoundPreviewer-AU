using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;
using SoundPreviewer.Core.Services;
using SoundPreviewer.Core.Storage;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    // Keep the original Windows fixture suite deterministic when it runs on a Mac.
    private static int ApplyWindowsAutomatic(IReadOnlyList<DiscoveredPlugin> discovered, IList<PluginConfiguration> configurations, string? host = null) =>
        PluginSelectionService.ApplyAutomatic(discovered, configurations, host, PluginPlatform.Windows, PluginArchitecture.X64);
    private static void AssignWindowsPlugin(PluginConfiguration configuration, DiscoveredPlugin plugin, string? host = null) =>
        PluginSelectionService.Assign(configuration, plugin, host, PluginPlatform.Windows, PluginArchitecture.X64);
    private static int RefreshWindowsHost(IList<PluginConfiguration> configurations, string? host) =>
        PluginSelectionService.RefreshBundledHost(configurations, host, PluginPlatform.Windows);
    private static RenderReadiness EvaluateWindowsReadiness(PluginConfiguration configuration, IReadOnlyList<DiscoveredPlugin> discovered) =>
        RenderReadinessService.Evaluate(configuration, discovered, PluginPlatform.Windows, PluginArchitecture.X64);
    private static IReadOnlyList<RenderReadiness> EvaluateWindowsReadiness(AppSettings settings) =>
        RenderReadinessService.Evaluate(settings, PluginPlatform.Windows, PluginArchitecture.X64);

    private static async Task TestAudioUnitDiscoveryAsync()
    {
        string directory = Fixture("au-bundle-discovery");
        // AU factories do not export VSTPluginMain or GetPluginFactory.
        string au = WriteMacBundle(directory, "Serum.component", "Serum", "com.xferrecords.Serum", "1.36.8",
            universal: true, binaryPlist: true, badEntry: true);
        string effect = WriteMacBundle(directory, "SerumFX.component", "SerumFX", "com.xferrecords.SerumFX", "1.0", arm64: true);
        string broken = WriteMacBundle(directory, "Broken.component", "Broken", "org.test.broken", "1.0");
        File.WriteAllText(Path.Combine(broken, "Contents", "MacOS", "Broken"), "not a Mach-O binary");
        string escaped = WriteMacBundle(directory, "Escape.component", "Escape", "org.test.escape", "1.0");
        File.WriteAllText(Path.Combine(escaped, "Contents", "Info.plist"),
            "<plist><dict><key>CFBundleExecutable</key><string>../../outside</string></dict></plist>");
        var result = await new PluginScanService().ScanAsync([directory, au]);
        Equal(2, result.Plugins.Count, "AU bundles deduplicate overlapping roots and reject corrupt or escaping binaries.");
        True(result.Plugins.All(plugin => plugin.Format == PluginBinaryFormat.AudioUnit && plugin.Platform == PluginPlatform.MacOS),
            "AU bundles retain their own format and platform, including effects.");
        var instrument = result.Plugins.Single(plugin => plugin.FilePath == au);
        Equal(PluginArchitecture.Universal, instrument.Architecture, "AU discovery checks Intel and ARM slices without requiring VST exports.");
        Equal(PluginArchitecture.Arm64, result.Plugins.Single(plugin => plugin.FilePath == effect).Architecture, "ARM effect bundles are discoverable.");
        Equal(instrument.BinaryPath, PluginScanService.ResolveBundleExecutable(au), "AU executable resolution accepts binary plists.");
        True(result.Warnings.Count >= 2, "Invalid AU packages produce diagnostics while scanning continues.");
        var restored = JsonSerializer.Deserialize<DiscoveredPlugin>(JsonSerializer.Serialize(instrument, LibraryJson.Options), LibraryJson.Options)!;
        Equal(PluginBinaryFormat.AudioUnit, restored.Format, "AU format survives settings serialization.");
        Equal(2, (int)PluginBinaryFormat.Standalone, "Adding AU keeps persisted legacy format values unchanged.");
        var configuration = new PluginConfiguration { Synth = SynthKind.Serum1 };
        Equal(0, PluginSelectionService.ApplyAutomatic(result.Plugins, [configuration], platform: PluginPlatform.MacOS,
            architecture: PluginArchitecture.Arm64), "Discovery alone must not configure an AU as a VST renderer.");
        Throws<ArgumentException>(() => PluginSelectionService.Assign(configuration, instrument, platform: PluginPlatform.MacOS));
        Equal("", configuration.PluginPath, "Refusing unsupported AU association preserves configuration.");
        string vst = WriteMacBundle(directory, "Serum.vst", "Serum", "com.xferrecords.Serum", "1.36.8", universal: true);
        result = await new PluginScanService().ScanAsync([directory]);
        Equal(1, PluginSelectionService.ApplyAutomatic(result.Plugins, [configuration], platform: PluginPlatform.MacOS,
            architecture: PluginArchitecture.Arm64), "An AU installation does not block automatic VST association.");
        Equal(vst, configuration.PluginPath, "Automatic selection chooses the supported VST bundle.");
        True(ApplicationPaths.AudioUnitDirectories.Count == 2 && ApplicationPaths.AudioUnitDirectories.All(path =>
            path.EndsWith("Audio/Plug-Ins/Components", StringComparison.Ordinal)), "Both system and user AU locations are provided.");
        if (OperatingSystem.IsMacOS()) True(ApplicationPaths.AudioUnitDirectories.All(ApplicationPaths.DefaultPluginDirectories.Contains),
            "New Mac installations include both AU roots by default.");
    }

    private static async Task TestMacPluginDiscoveryAsync()
    {
        string directory = Fixture("mac-plugin-discovery");
        string serumBundle = WriteMacBundle(directory, "Serum.vst", "Serum", "com.xferrecords.Serum", "1.36.8", universal: true);
        string serum2Bundle = WriteMacBundle(directory, "Serum 2.vst3", "Serum 2", "com.xfer.serum2", "2.0.1", arm64: true, trie: true);
        string sylenthBundle = WriteMacBundle(directory, "Sylenth1.vst", "Sylenth1", "com.lennardigital.Sylenth1", "3.0.7");
        string sylenthPlist = Path.Combine(sylenthBundle, "Contents", "Info.plist");
        var duplicate = XDocument.Load(sylenthPlist);
        duplicate.Root!.Element("dict")!.Add(new XElement("key", "CFBundleIdentifier"), new XElement("string", "com.lennardigital.Sylenth1"));
        duplicate.Save(sylenthPlist); // Some shipping Mac plugins repeat identical keys.
        string vitalBundle = WriteMacBundle(directory, "Vital.app", "Vital", "com.matttytel.vital", "1.5.5", universal: true, binaryPlist: true);
        WriteMacBundle(directory, "Unknown.vst", "Serum", "com.unrelated.Serum", "1.0");
        WriteMacBundle(directory, "SerumFX.vst", "SerumFX", "com.xferrecords.SerumFX", "1.0");
        WriteMacBundle(directory, "Broken.vst3", "Serum 2", "com.xfer.serum2", "2.0", badEntry: true);
        string escape = WriteMacBundle(directory, "Escape.app", "Vital", "com.matttytel.vital", "1.0");
        File.WriteAllText(Path.Combine(escape, "Contents", "Info.plist"),
            "<plist><dict><key>CFBundleExecutable</key><string>../../outside</string></dict></plist>");
        string malformed = Path.Combine(directory, "Malformed.vst");
        Directory.CreateDirectory(Path.Combine(malformed, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(malformed, "Contents", "Info.plist"), "bplist00broken");
        var scanner = new PluginScanService();
        var result = await scanner.ScanAsync([directory, serumBundle]);
        Equal(5, result.Plugins.Count, "Mac bundles deduplicate overlapping roots and reject missing entries, FX instruments and escaping executable paths.");
        True(result.Plugins.All(plugin => plugin.Platform == PluginPlatform.MacOS), "Mach-O discovery records the native platform explicitly.");
        var serum = result.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1);
        var serum2 = result.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum2);
        var sylenth = result.Plugins.Single(plugin => plugin.Synth == SynthKind.Sylenth1);
        var vital = result.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital);
        Equal(PluginArchitecture.Universal, serum.Architecture, "All Universal slices are checked before advertising both native architectures.");
        Equal(PluginArchitecture.Arm64, serum2.Architecture, "A stripped ARM64 plugin can be identified from its validated export trie.");
        Equal(PluginArchitecture.X64, sylenth.Architecture, "Intel macOS plugins retain x64 architecture.");
        Equal(PluginBinaryFormat.Standalone, vital.Format, "Vital.app is discovered through its binary Info.plist metadata.");
        Equal("1.5.5", vital.Version, "The binary plist version is read without launching plist conversion tools.");
        Equal(serumBundle, serum.FilePath, "Mac plugin selection retains the enclosing bundle path.");
        Equal(serum2Bundle, serum2.FilePath, "Mac VST3 selection retains its outer bundle path.");
        Equal(sylenthBundle, sylenth.FilePath, "Mac VST2 selection retains its outer bundle path.");
        Equal(vital.BinaryPath, PluginScanService.ResolveBundleExecutable(vitalBundle), "Manual app selection resolves the actual CLI executable.");
        True(PluginScanService.ResolveBundleExecutable(escape) is null, "Manual bundle resolution rejects executable path traversal.");
        True(PluginScanService.ResolveBundleExecutable(malformed) is null, "Malformed binary plist resolution returns no executable.");
        True(result.Plugins.Single(plugin => plugin.Synth == SynthKind.Unknown).Reason.Length > 0,
            "A familiar product name cannot overcome missing manufacturer metadata.");
        True(result.Warnings.Count >= 3, "Malformed packages surface bounded diagnostics while valid bundles remain available.");
        Equal(PluginPlatform.Windows, JsonSerializer.Deserialize<DiscoveredPlugin>("{}", LibraryJson.Options)!.Platform,
            "Legacy Windows discovery JSON keeps its original platform.");
        var roundTrip = JsonSerializer.Deserialize<PluginScanResult>(JsonSerializer.Serialize(result, LibraryJson.Options), LibraryJson.Options)!;
        Equal(PluginArchitecture.Universal, roundTrip.Plugins.Single(plugin => plugin.Synth == SynthKind.Vital).Architecture,
            "Universal architecture survives shared settings serialization.");

        var vitalConfiguration = new PluginConfiguration { Synth = SynthKind.Vital };
        Equal(1, PluginSelectionService.ApplyAutomatic([vital], [vitalConfiguration], platform: PluginPlatform.MacOS, architecture: PluginArchitecture.Arm64),
            "A compatible Mac Vital app is selected automatically.");
        Equal(vital.BinaryPath, vitalConfiguration.ExecutablePath, "Native CLI launches Contents/MacOS/Vital, not the .app directory.");
        Equal(RenderBackend.VitalCli, vitalConfiguration.Backend, "Native Mac selection uses the shared Vital adapter.");
        True(PluginSelectionService.Matches(vitalConfiguration, vital), "Standalone matching handles the outer bundle and actual executable.");
        Equal(0, PluginSelectionService.ApplyAutomatic([vital], [vitalConfiguration], platform: PluginPlatform.MacOS, architecture: PluginArchitecture.Arm64),
            "Repeated Mac selection is idempotent.");
        var windows = new DiscoveredPlugin
        {
            Synth = SynthKind.Serum1, FilePath = serum.BinaryPath, BinaryPath = serum.BinaryPath,
            Architecture = PluginArchitecture.X64, Platform = PluginPlatform.Windows, Format = PluginBinaryFormat.Vst2, Version = "1.0"
        };
        var configuration = new PluginConfiguration { Synth = SynthKind.Serum1 };
        Equal(0, PluginSelectionService.ApplyAutomatic([windows], [configuration], platform: PluginPlatform.MacOS, architecture: PluginArchitecture.Arm64),
            "Windows discovery cannot automatically configure a Mac renderer.");
        Throws<ArgumentException>(() => PluginSelectionService.Assign(configuration, windows, platform: PluginPlatform.MacOS));
        configuration.PluginPath = windows.FilePath;
        Equal(1, PluginSelectionService.ApplyAutomatic([windows, serum], [configuration], platform: PluginPlatform.MacOS, architecture: PluginArchitecture.Arm64),
            "A known Windows selection cannot prevent association with the compatible installed Mac version.");
        True(!PluginSelectionService.IsCompatible(sylenth, PluginPlatform.MacOS, PluginArchitecture.Arm64),
            "An Intel-only plugin is not silently loaded into a native ARM64 host.");
        True(PluginSelectionService.IsCompatible(serum, PluginPlatform.MacOS, PluginArchitecture.X64) &&
             PluginSelectionService.IsCompatible(serum, PluginPlatform.MacOS, PluginArchitecture.Arm64), "Universal supports either native Mac host.");
        var intelConfiguration = new PluginConfiguration { Synth = SynthKind.Sylenth1 };
        Equal(0, PluginSelectionService.ApplyAutomatic([sylenth], [intelConfiguration], platform: PluginPlatform.MacOS, architecture: PluginArchitecture.Arm64),
            "Automatic selection rejects a mismatched process architecture.");

        string python = Path.Combine(directory, "TEST-ONLY-python3");
        File.WriteAllText(python, "TEST ONLY; never executed by discovery or readiness.");
        configuration.Backend = RenderBackend.DawDreamer;
        configuration.ExecutablePath = python;
        PluginSelectionService.Assign(configuration, serum, platform: PluginPlatform.MacOS, architecture: PluginArchitecture.Arm64);
        var readiness = RenderReadinessService.Evaluate(configuration, result.Plugins, PluginPlatform.MacOS, PluginArchitecture.Arm64);
        True(readiness.CanAttemptRender && !readiness.IsVerified, "Shared FXP readiness accepts a validated compatible Mac VST2 bundle without claiming actual plugin verification.");
        configuration.PluginPath = windows.FilePath;
        True(!RenderReadinessService.Evaluate(configuration, [windows], PluginPlatform.MacOS, PluginArchitecture.Arm64).CanAttemptRender,
            "Readiness rejects a Windows binary on macOS.");
        var intel = new PluginConfiguration { Synth = SynthKind.Sylenth1, PluginPath = sylenth.FilePath, ExecutablePath = python, Version = "3.0.7", Backend = RenderBackend.DawDreamer };
        True(!RenderReadinessService.Evaluate(intel, result.Plugins, PluginPlatform.MacOS, PluginArchitecture.Arm64).CanAttemptRender,
            "Readiness rejects an Intel-only plugin in a native ARM64 host.");
        True(RenderReadinessService.Evaluate(intel, result.Plugins, PluginPlatform.MacOS, PluginArchitecture.X64).CanAttemptRender,
            "The same Intel Mac plugin can be attempted in a matching x64 host.");
        True(RenderReadinessService.Evaluate(vitalConfiguration, result.Plugins, PluginPlatform.MacOS, PluginArchitecture.Arm64).CanAttemptRender,
            "Readiness accepts Vital's resolved executable inside its app bundle.");

        byte[] universal = File.ReadAllBytes(serum.BinaryPath);
        BinaryPrimitives.WriteUInt32BigEndian(universal.AsSpan(36), 4096); // Make the ARM slice overlap the Intel slice.
        File.WriteAllBytes(serum.BinaryPath, universal);
        var corrupt = await scanner.ScanAsync([serumBundle]);
        True(corrupt.Plugins.Count == 0 && corrupt.Warnings.Count > 0, "Overlapping Universal slices cannot establish trusted architecture or identity.");
        duplicate.Root!.Element("dict")!.Add(new XElement("key", "CFBundleIdentifier"), new XElement("string", "com.unrelated.sylenth1"));
        duplicate.Save(sylenthPlist);
        True((await scanner.ScanAsync([sylenthBundle])).Plugins.Count == 0, "Conflicting duplicate identity keys cannot confirm a product.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync([directory], cancellation.Token));
    }

    private static string WriteMacBundle(string directory, string fileName, string product, string identifier, string version,
        bool arm64 = false, bool universal = false, bool binaryPlist = false, bool badEntry = false, bool trie = false)
    {
        string bundle = Path.Combine(directory, fileName), executable = product.Replace(" ", "", StringComparison.Ordinal);
        string binaries = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(binaries);
        var values = new Dictionary<string, string>
        {
            ["CFBundleName"] = product, ["CFBundleIdentifier"] = identifier, ["CFBundleExecutable"] = executable,
            ["CFBundleShortVersionString"] = version, ["CFBundleVersion"] = version, ["TestUnicode"] = "合成器"
        };
        string metadata = Path.Combine(bundle, "Contents", "Info.plist");
        if (binaryPlist) File.WriteAllBytes(metadata, MakeBinaryPlist(values));
        else new XDocument(new XElement("plist", new XElement("dict", values.SelectMany(pair => new[]
            { new XElement("key", pair.Key), new XElement("string", pair.Value) })))).Save(metadata);
        bool standalone = Path.GetExtension(bundle) == ".app";
        string export = badEntry ? "_NotAPlugin" : Path.GetExtension(bundle) == ".vst3" ? "_GetPluginFactory" : "_VSTPluginMain";
        byte[] thin = MakeMachO(arm64, standalone, export, trie);
        if (universal)
        {
            byte[] intel = MakeMachO(false, standalone, export, trie), arm = MakeMachO(true, standalone, export, trie);
            byte[] fat = new byte[8192 + arm.Length];
            BinaryPrimitives.WriteUInt32BigEndian(fat, 0xcafebabe);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(8), 0x01000007);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(16), 4096);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(20), (uint)intel.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(24), 12);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(28), 0x0100000c);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(36), 8192);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(40), (uint)arm.Length);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(44), 12);
            intel.CopyTo(fat, 4096); arm.CopyTo(fat, 8192);
            thin = fat;
        }
        File.WriteAllBytes(Path.Combine(binaries, executable), thin);
        return bundle;
    }

    private static byte[] MakeMachO(bool arm64, bool standalone, string export, bool trie)
    {
        byte[] name = Encoding.ASCII.GetBytes(export + "\0");
        byte[] data = new byte[128 + name.Length];
        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        Word(0, 0xfeedfacf); Word(4, arm64 ? 0x0100000cU : 0x01000007U); Word(12, standalone ? 2U : 8U);
        if (standalone) return data;
        Word(16, 1); Word(20, trie ? 16U : 24U);
        if (trie)
        {
            // Root edge -> terminal node with a regular export at address 0x40.
            byte[] tree = new byte[name.Length + 8];
            tree[1] = 1; name.CopyTo(tree, 2); tree[2 + name.Length] = (byte)(3 + name.Length);
            tree[3 + name.Length] = 2; tree[4 + name.Length] = 0; tree[5 + name.Length] = 0x40;
            Word(32, 0x80000033); Word(36, 16); Word(40, 64); Word(44, (uint)tree.Length);
            tree.CopyTo(data, 64);
        }
        else
        {
            Word(32, 2); Word(36, 24); Word(40, 64); Word(44, 1); Word(48, 80); Word(52, (uint)name.Length + 1);
            Word(64, 1); data[68] = 0x0f; data[69] = 1; Word(72, 0x1000);
            name.CopyTo(data, 81);
        }
        return data;
    }

    private static byte[] MakeBinaryPlist(Dictionary<string, string> values)
    {
        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        var offsets = new List<ushort>();
        void Text(string value)
        {
            offsets.Add(checked((ushort)stream.Position));
            bool unicode = value.Any(character => character > 127);
            stream.WriteByte((byte)((unicode ? 0x60 : 0x50) | Math.Min(value.Length, 15)));
            if (value.Length >= 15) { stream.WriteByte(0x10); stream.WriteByte(checked((byte)value.Length)); }
            stream.Write((unicode ? Encoding.BigEndianUnicode : Encoding.ASCII).GetBytes(value));
        }
        foreach (string key in values.Keys) Text(key);
        foreach (string value in values.Values) Text(value);
        offsets.Add(checked((ushort)stream.Position));
        stream.WriteByte((byte)(0xd0 | values.Count));
        for (int i = 0; i < values.Count * 2; i++) stream.WriteByte((byte)i);
        long table = stream.Position;
        foreach (ushort offset in offsets) { stream.WriteByte((byte)(offset >> 8)); stream.WriteByte((byte)offset); }
        byte[] trailer = new byte[32]; trailer[6] = 2; trailer[7] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)offsets.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), (ulong)offsets.Count - 1);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)table);
        stream.Write(trailer);
        return stream.ToArray();
    }
}
