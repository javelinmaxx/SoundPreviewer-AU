using SoundPreviewer.Core.Models;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace SoundPreviewer.Core.Services;

public static class PluginSelectionService
{
    public const string SushiExecutableName = "sushi.exe";

    public static PluginPlatform CurrentPlatform => OperatingSystem.IsMacOS() ? PluginPlatform.MacOS : PluginPlatform.Windows;
    public static PluginArchitecture CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => PluginArchitecture.Arm64,
        Architecture.X86 => PluginArchitecture.X86,
        _ => PluginArchitecture.X64
    };

    public static bool IsCompatible(DiscoveredPlugin plugin, PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        PluginPlatform target = platform ?? CurrentPlatform;
        PluginArchitecture cpu = architecture ?? CurrentArchitecture;
        return plugin.Platform == target && (target == PluginPlatform.Windows
            ? plugin.Architecture == PluginArchitecture.X64
            : cpu is PluginArchitecture.X64 or PluginArchitecture.Arm64 &&
              (plugin.Architecture == cpu || plugin.Architecture == PluginArchitecture.Universal));
    }

    public static int RefreshBundledHost(IList<PluginConfiguration> configurations, string? newHostPath, PluginPlatform? platform = null,
        IReadOnlyList<DiscoveredPlugin>? discovered = null)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        if ((platform ?? CurrentPlatform) != PluginPlatform.Windows) return 0;
        if (!File.Exists(newHostPath)) return 0;
        string nextPath = Path.GetFullPath(newHostPath!);
        int changed = 0;
        foreach (var configuration in configurations)
        {
            if (configuration.Synth != SynthKind.Sylenth1 || configuration.Backend is not (RenderBackend.ExternalWorker or RenderBackend.DawDreamer) ||
                string.IsNullOrWhiteSpace(configuration.ExecutablePath)) continue;
            string previousPath;
            try { previousPath = Path.GetFullPath(configuration.ExecutablePath); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { continue; }
            if (!IsBundledX86Host(previousPath)) continue;
            bool restoreBackend = configuration.Backend == RenderBackend.DawDreamer && discovered?.Any(plugin =>
                Matches(configuration, plugin) && plugin.Platform == PluginPlatform.Windows &&
                plugin.Architecture == PluginArchitecture.X86 && plugin.Format == PluginBinaryFormat.Vst2) == true;
            if (configuration.Backend == RenderBackend.DawDreamer && !restoreBackend ||
                string.Equals(previousPath, nextPath, StringComparison.OrdinalIgnoreCase) && !restoreBackend) continue;
            configuration.Backend = RenderBackend.ExternalWorker;
            configuration.ExecutablePath = nextPath;
            configuration.ExecutableHash = "";
            configuration.InitBaselinePath = "";
            changed++;
        }
        return changed;
    }

    public static bool IsBundledX86Host(string path)
    {
        try
        {
            return string.Equals(Path.GetFileName(path), "SoundPreviewer.Vst2Host.exe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Vst2Host-x86", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool IsSushiExecutable(string path)
    {
        try { return string.Equals(Path.GetFileName(path), SushiExecutableName, StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool IsBundledSushiHost(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            return directory is not null && IsSushiExecutable(path) && string.Equals(Path.GetFileName(directory), "Sushi", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Renderers", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool IsWindowsX64Binary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.PEHeaders.CoffHeader.Machine == Machine.Amd64 && reader.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or BadImageFormatException) { return false; }
    }

    public static bool IsSushiHostAvailable(string path)
    {
        if (!IsSushiExecutable(path) || !IsWindowsX64Binary(path)) return false;
        return IsWindowsX64Binary(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "sndfile.dll"));
    }

    public static int RefreshBundledSushiHost(IList<PluginConfiguration> configurations, string? newHostPath,
        PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        if ((platform ?? CurrentPlatform) != PluginPlatform.Windows || (architecture ?? CurrentArchitecture) != PluginArchitecture.X64 ||
            string.IsNullOrWhiteSpace(newHostPath) || !IsBundledSushiHost(newHostPath) || !IsSushiHostAvailable(newHostPath)) return 0;
        string nextPath = Path.GetFullPath(newHostPath);
        int changed = 0;
        foreach (var configuration in configurations)
        {
            if (configuration.Backend != RenderBackend.Sushi || configuration.Synth != SynthKind.Serum1 ||
                !IsBundledSushiHost(configuration.ExecutablePath) || ApplicationPaths.SamePath(configuration.ExecutablePath, nextPath)) continue;
            configuration.ExecutablePath = nextPath;
            configuration.ExecutableHash = "";
            configuration.InitBaselinePath = "";
            changed++;
        }
        return changed;
    }

    public static int RefreshLocalDawDreamer(IList<PluginConfiguration> configurations, string dataDirectory, PluginPlatform? platform = null,
        PluginArchitecture? architecture = null)
    {
        var target = platform ?? CurrentPlatform;
        DawDreamerRuntimeService.TryGetLocalRuntime(dataDirectory, out var runtime, target, architecture);
        string legacyPython = target == PluginPlatform.Windows
            ? Path.Combine(dataDirectory, "renderers", "dawdreamer-cpython-x64", "Scripts", "python.exe")
            : Path.Combine(dataDirectory, "renderers", "dawdreamer-cpython", "bin", "python");
        int changed = 0;
        foreach (var configuration in configurations.Where(configuration => configuration.Backend == RenderBackend.DawDreamer &&
            configuration.Synth is SynthKind.Serum1 or SynthKind.Serum2 or SynthKind.Sylenth1))
        {
            bool blank = string.IsNullOrWhiteSpace(configuration.ExecutablePath);
            if (!blank && (File.Exists(configuration.ExecutablePath) || !IsDefaultDawDreamerPath(configuration.ExecutablePath))) continue;
            string? python = runtime is not null && (configuration.Synth != SynthKind.Serum2 || runtime.SupportsSerum2) ? runtime.PythonPath : null;
            // A moved venv is not repaired by pointing at its relocated launcher. Only
            // a newly prepared standalone runtime may replace a missing known default.
            if (python is null && blank && File.Exists(legacyPython)) python = legacyPython;
            if (python is null) continue;
            configuration.ExecutablePath = Path.GetFullPath(python);
            configuration.ExecutableHash = "";
            configuration.InitBaselinePath = "";
            changed++;
        }
        return changed;
    }

    public static int ApplyPreparedDawDreamer(IList<PluginConfiguration> configurations, string dataDirectory,
        IReadOnlyList<DiscoveredPlugin> discovered, PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        int changed = RefreshLocalDawDreamer(configurations, dataDirectory, platform, architecture);
        if (!DawDreamerRuntimeService.TryGetLocalRuntime(dataDirectory, out var runtime, platform, architecture)) return changed;
        foreach (var configuration in configurations.Where(configuration => configuration.Backend == RenderBackend.Unconfigured &&
            string.IsNullOrWhiteSpace(configuration.ExecutablePath) && configuration.Synth is SynthKind.Serum1 or SynthKind.Serum2 or SynthKind.Sylenth1))
        {
            if (configuration.Synth == SynthKind.Serum2 && !runtime!.SupportsSerum2) continue;
            var format = configuration.Synth == SynthKind.Serum2 ? PluginBinaryFormat.Vst3 : PluginBinaryFormat.Vst2;
            if (!discovered.Any(plugin => Matches(configuration, plugin) && plugin.Format == format &&
                IsCompatible(plugin, platform, architecture) && File.Exists(plugin.BinaryPath))) continue;
            configuration.Backend = RenderBackend.DawDreamer;
            configuration.ExecutablePath = runtime!.PythonPath;
            configuration.ExecutableHash = "";
            configuration.InitBaselinePath = "";
            changed++;
        }
        return changed;
    }

    private static bool IsDefaultDawDreamerPath(string path)
    {
        if (DawDreamerRuntimeService.IsManagedExecutablePath(path)) return true;
        try
        {
            string? launchers = Path.GetDirectoryName(Path.GetFullPath(path));
            string? environment = Path.GetDirectoryName(launchers);
            return string.Equals(Path.GetFileName(Path.GetDirectoryName(environment)), "renderers", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(Path.GetFileName(environment), "dawdreamer-cpython-x64", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(Path.GetFileName(launchers), "Scripts", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(Path.GetFileName(path), "python.exe", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(environment) == "dawdreamer-cpython" && Path.GetFileName(launchers) == "bin" && Path.GetFileName(path) == "python");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    public static int ApplyAutomatic(IReadOnlyList<DiscoveredPlugin> discovered, IList<PluginConfiguration> configurations, string? x86HostPath = null,
        PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        var changed = 0;
        foreach (var configuration in configurations.Where(p => p.Synth != SynthKind.Unknown))
        {
            var candidates = discovered.Where(p => p.Format != PluginBinaryFormat.AudioUnit && p.Synth == configuration.Synth &&
                    (IsCompatible(p, platform, architecture) || CanUseX86Host(configuration, p, x86HostPath, platform))
                    && (p.Format != PluginBinaryFormat.Standalone || configuration.Backend is RenderBackend.Unconfigured or RenderBackend.VitalCli))
                .OrderBy(p => Preference(p, configuration.Synth)).ThenByDescending(p => ParseVersion(p.Version))
                .ThenBy(p => p.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            var selected = candidates.FirstOrDefault(p => Matches(configuration, p));
            bool incompatibleSelection = discovered.Any(p => Matches(configuration, p) &&
                !IsCompatible(p, platform, architecture) && !CanUseX86Host(configuration, p, x86HostPath, platform));
            // Keep a user's existing selection even when its installation root is offline or omitted.
            if (selected is null && !incompatibleSelection && (File.Exists(configuration.PluginPath) || Directory.Exists(configuration.PluginPath)
                || configuration.Backend == RenderBackend.VitalCli && File.Exists(configuration.ExecutablePath))) continue;
            selected ??= candidates.FirstOrDefault();
            if (selected is null) continue;
            var before = (configuration.PluginPath, configuration.ExecutablePath, configuration.Version, configuration.Backend);
            Assign(configuration, selected, x86HostPath, platform, architecture);
            if (before != (configuration.PluginPath, configuration.ExecutablePath, configuration.Version, configuration.Backend)) changed++;
        }
        return changed;
    }

    public static bool Matches(PluginConfiguration configuration, DiscoveredPlugin plugin) => configuration.Synth == plugin.Synth
        && (plugin.Format != PluginBinaryFormat.Standalone || configuration.Backend == RenderBackend.VitalCli)
        && (ApplicationPaths.SamePath(plugin.Format == PluginBinaryFormat.Standalone ? configuration.ExecutablePath : configuration.PluginPath,
            plugin.FilePath) || plugin.Format == PluginBinaryFormat.Standalone &&
            ApplicationPaths.SamePath(configuration.ExecutablePath, plugin.BinaryPath));

    public static void Assign(PluginConfiguration configuration, DiscoveredPlugin plugin, string? x86HostPath = null,
        PluginPlatform? platform = null, PluginArchitecture? architecture = null)
    {
        if (plugin.Format == PluginBinaryFormat.AudioUnit)
            throw new ArgumentException("AU 已发现，但桌面预制后端尚不支持 AU。Logic .patch 请使用 Start-LogicPreview-Web.command（localhost:5002）。");
        if (plugin.Synth == SynthKind.Unknown || configuration.Synth != plugin.Synth)
            throw new ArgumentException("扫描结果不能关联到此音源。");
        if (plugin.Platform != (platform ?? CurrentPlatform)) throw new ArgumentException("所选音源与当前操作系统不兼容，请选择本平台版本。");
        if (!File.Exists(plugin.BinaryPath)) throw new FileNotFoundException("扫描到的音源文件已不存在，请重新扫描。", plugin.BinaryPath);
        if (plugin.Format == PluginBinaryFormat.Standalone)
        {
            if (plugin.Synth != SynthKind.Vital) throw new ArgumentException("只有 Vital 独立程序支持原生预览方式。");
            if (!IsCompatible(plugin, platform, architecture)) throw new ArgumentException("所选 Vital 程序与当前运行架构不兼容。");
            configuration.ExecutablePath = plugin.BinaryPath;
            configuration.Backend = RenderBackend.VitalCli;
            configuration.PluginPath = "";
            configuration.PluginHash = "";
        }
        else
        {
            configuration.PluginPath = plugin.FilePath;
            if (configuration.Backend == RenderBackend.VitalCli)
            {
                configuration.Backend = RenderBackend.Unconfigured;
                configuration.ExecutablePath = "";
                configuration.ExecutableHash = "";
            }
            if (CanUseX86Host(configuration, plugin, x86HostPath, platform) &&
                configuration.Backend is RenderBackend.Unconfigured or RenderBackend.DawDreamer)
            {
                configuration.Backend = RenderBackend.ExternalWorker;
                configuration.ExecutablePath = Path.GetFullPath(x86HostPath!);
                configuration.ExecutableHash = "";
            }
        }
        configuration.Version = plugin.Version;
    }

    private static bool CanUseX86Host(PluginConfiguration configuration, DiscoveredPlugin plugin, string? hostPath, PluginPlatform? platform = null) =>
        (platform ?? CurrentPlatform) == PluginPlatform.Windows && plugin.Platform == PluginPlatform.Windows &&
        plugin.Synth == SynthKind.Sylenth1 && plugin.Architecture == PluginArchitecture.X86 &&
        plugin.Format == PluginBinaryFormat.Vst2 && !string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath) &&
        (configuration.Backend is RenderBackend.Unconfigured or RenderBackend.DawDreamer ||
         configuration.Backend == RenderBackend.ExternalWorker &&
         string.Equals(configuration.ExecutablePath, hostPath, StringComparison.OrdinalIgnoreCase));

    private static int Preference(DiscoveredPlugin plugin, SynthKind synth) => (synth, plugin.Format) switch
    {
        (SynthKind.Vital, PluginBinaryFormat.Standalone) => 0,
        (SynthKind.Serum2, PluginBinaryFormat.Vst3) => 0,
        (_, PluginBinaryFormat.Vst2) => 1,
        _ => 2
    };

    private static Version ParseVersion(string value)
    {
        var normalized = value.Split('+', '-')[0].Trim().Replace(',', '.').Replace(" ", "");
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0);
    }
}
