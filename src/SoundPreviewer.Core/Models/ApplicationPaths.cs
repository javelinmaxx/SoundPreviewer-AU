using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Core.Models;

/// <summary>Portable libraries default to Preview beside the application; executable resources stay in their platform bundle.</summary>
public static class ApplicationPaths
{
    private static string? dataDirectory;
    private static string? configurationDirectory;
    public static string ApplicationDirectory => Path.GetFullPath(AppContext.BaseDirectory);
    public static string PortableApplicationDirectory => ResolvePortableApplicationDirectory(ApplicationDirectory, OperatingSystem.IsMacOS());
    public static string DefaultPreviewDirectory => Path.Combine(PortableApplicationDirectory, "Preview");
    public static string ConfigurationDirectory => configurationDirectory ?? GetConfigurationDirectory(PortableApplicationDirectory);
    public static string DirectoryConfigurationPath => Path.Combine(ConfigurationDirectory, DirectoryConfigurationService.FileName);
    public static string LibraryLocationPath => Path.Combine(ConfigurationDirectory, "library-location.json");
    public static string LegacyDataDirectory => Path.Combine(ApplicationDirectory, "local-data");
    public static string UserDataDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SoundPreviewer")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundPreviewer");
    public static string DataDirectory => dataDirectory ??= ResolveDefaultDataDirectory(PortableApplicationDirectory, UserDataDirectory);
    public static string WorkerPath
    {
        get
        {
            string executable = Path.Combine(ApplicationDirectory, "SoundPreviewer.Worker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            return File.Exists(executable) ? executable : Path.Combine(ApplicationDirectory, "SoundPreviewer.Worker.dll");
        }
    }

    public static void ConfigureDataDirectory(string? directory, string? applicationDirectory = null, string? userDataDirectory = null)
    {
        string application = applicationDirectory ?? PortableApplicationDirectory;
        // Resolve once for this process: opening another library may update the shared locator.
        dataDirectory = string.IsNullOrWhiteSpace(directory)
            ? ResolveDefaultDataDirectory(application, userDataDirectory ?? UserDataDirectory)
            : Path.GetFullPath(directory, application);
    }

    public static string GetConfigurationDirectory(string applicationRoot) => Path.Combine(Path.GetFullPath(applicationRoot), "Config");

    // Isolated desktop diagnostics supply their private root before constructing UI services.
    public static void ConfigureConfigurationDirectory(string? applicationRoot = null) =>
        configurationDirectory = applicationRoot is null ? null : GetConfigurationDirectory(applicationRoot);

    public static string ResolveDefaultDataDirectory(string applicationDirectory, string userDataDirectory)
    {
        string application = Path.GetFullPath(applicationDirectory);
        string locationPath = PreferredLocationPath(application);
        string oldUserDirectory = Path.GetFullPath(userDataDirectory);
        string oldLocation = PreferredLocationPath(oldUserDirectory);
        var (primary, primaryError) = TryReadLocationMetadata(locationPath);
        var (fallback, fallbackError) = TryReadLocationMetadata(oldLocation);
        var primaryTime = primary?.SavedUtc ?? new DateTimeOffset(File.Exists(locationPath) ? File.GetLastWriteTimeUtc(locationPath) : DateTime.MinValue, TimeSpan.Zero);
        if (fallback?.FormatVersion == 2 && fallback.SavedUtc is not null && fallback.SavedUtc >= primaryTime)
            return ReadLibraryLocation(oldLocation);
        if (primary is not null) return ReadLibraryLocation(locationPath);
        if (primaryError is not null) throw primaryError;
        // A v2 user locator is an explicit selection saved when the program directory was read-only.
        if (fallback?.FormatVersion == 2) return ReadLibraryLocation(oldLocation);
        string previewDirectory = Path.Combine(application, "Preview");
        if (File.Exists(Path.Combine(previewDirectory, "library.db"))) return previewDirectory;
        string legacyDirectory = Path.Combine(application, "local-data");
        if (File.Exists(Path.Combine(legacyDirectory, "library.db"))) return legacyDirectory;
        if (fallbackError is not null) throw fallbackError;
        if (File.Exists(oldLocation)) return ReadLibraryLocation(oldLocation);
        return File.Exists(Path.Combine(oldUserDirectory, "library.db")) ? oldUserDirectory : previewDirectory;
    }

    private static string PreferredLocationPath(string root)
    {
        string current = Path.Combine(GetConfigurationDirectory(root), "library-location.json");
        return File.Exists(current) ? current : Path.Combine(root, "library-location.json");
    }

    private static (LibraryLocation? Location, Exception? Error) TryReadLocationMetadata(string path)
    {
        if (!File.Exists(path)) return (null, null);
        try { return (ReadLocationMetadata(path), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return (null, error); }
    }

    public static string ResolvePortableApplicationDirectory(string executableDirectory, bool macOS)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        string? contents = Path.GetDirectoryName(directory), bundle = contents is null ? null : Path.GetDirectoryName(contents);
        if (macOS && Path.GetFileName(directory) == "MacOS" && Path.GetFileName(contents) == "Contents" &&
            bundle is not null && Path.GetExtension(bundle).Equals(".app", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(bundle)!;
        return directory;
    }

    private static string ReadLibraryLocation(string locationPath)
    {
        var location = ReadLocationMetadata(locationPath);
        string directory = Path.GetFullPath(location.DataDirectory, Path.GetDirectoryName(locationPath)!);
        if (!File.Exists(Path.Combine(directory, "library.db")))
            throw new DirectoryNotFoundException($"已记住的音色库无法找到：{directory}。请连接原磁盘，或使用“打开已有音色库”重新选择；现有记录未更改。");
        return directory;
    }

    private static LibraryLocation ReadLocationMetadata(string locationPath)
    {
        if (new FileInfo(locationPath).Length > 16 * 1024) throw new InvalidDataException("音色库位置文件过大。请重新选择已有音色库。");
        var location = JsonSerializer.Deserialize<LibraryLocation>(File.ReadAllText(locationPath));
        if (location is null || location.FormatVersion is not (1 or 2) || string.IsNullOrWhiteSpace(location.DataDirectory) ||
            (location.FormatVersion == 1 && !Path.IsPathFullyQualified(location.DataDirectory)))
            throw new InvalidDataException("音色库位置文件版本或路径无效。请重新选择已有音色库。");
        return location;
    }

    public static string RememberDataDirectory(string directory, string? locatorDirectory = null, string? fallbackLocatorDirectory = null)
    {
        string fullPath = Path.GetFullPath(directory, PortableApplicationDirectory);
        if (!File.Exists(Path.Combine(fullPath, "library.db"))) throw new DirectoryNotFoundException("只能记住已经成功打开的音色库目录。");
        string userDirectory = Path.GetFullPath(locatorDirectory ?? PortableApplicationDirectory);
        try { return WriteLibraryLocation(fullPath, userDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            string? fallback = fallbackLocatorDirectory ?? (locatorDirectory is null ? UserDataDirectory : null);
            if (fallback is null || SamePath(userDirectory, fallback)) throw;
            return WriteLibraryLocation(fullPath, Path.GetFullPath(fallback));
        }
    }

    private static string WriteLibraryLocation(string fullPath, string userDirectory)
    {
        string config = GetConfigurationDirectory(userDirectory);
        Directory.CreateDirectory(config);
        string locationPath = Path.Combine(config, "library-location.json");
        string temporary = locationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                string savedPath = IsWithin(fullPath, userDirectory) ? Path.GetRelativePath(config, fullPath) : fullPath;
                JsonSerializer.Serialize(stream, new LibraryLocation(2, savedPath, DateTimeOffset.UtcNow));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, locationPath, overwrite: true);
            return locationPath;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record LibraryLocation(int FormatVersion, string DataDirectory, DateTimeOffset? SavedUtc = null);

    // Windows keeps its historical case-insensitive IDs. Unix volumes may be case-sensitive.
    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string PathIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return fullPath.ToUpperInvariant();
        if (!OperatingSystem.IsMacOS()) return fullPath;
        // APFS can be either case-sensitive or case-insensitive. Ask the kernel for the
        // existing entry's spelling instead of folding distinct files into one identity.
        var bytes = new byte[4096];
        if (RealPath(fullPath, bytes) == IntPtr.Zero) return fullPath;
        int end = Array.IndexOf(bytes, (byte)0);
        return end > 0 ? Encoding.UTF8.GetString(bytes, 0, end) : fullPath;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] resolved);

    public static bool SamePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(PathIdentity(first), PathIdentity(second), StringComparison.Ordinal); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    public static bool IsWithin(string path, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string candidate = Path.GetFullPath(path, ApplicationDirectory);
        return candidate.Equals(root, PathComparison) || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    // AUv2 bundles live here; Apple's built-in and AUv3 units need registry discovery.
    public static IReadOnlyList<string> AudioUnitDirectories =>
        ["/Library/Audio/Plug-Ins/Components",
         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Audio/Plug-Ins/Components")];

    public static IReadOnlyList<string> DefaultPluginDirectories => OperatingSystem.IsMacOS()
        ? [.. AudioUnitDirectories, "/Library/Audio/Plug-Ins/VST", "/Library/Audio/Plug-Ins/VST3",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Audio/Plug-Ins/VST"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Audio/Plug-Ins/VST3"),
            "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications")]
        : [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VstPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steinberg", "VstPlugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3")];
}
