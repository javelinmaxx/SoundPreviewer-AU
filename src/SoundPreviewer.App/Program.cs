using Avalonia;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoundPreviewer.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var diagnostics = CreateStartupLog();
        bool software = args.Contains("--software-rendering", StringComparer.Ordinal) ||
            Environment.GetEnvironmentVariable("SOUNDPREVIEWER_SOFTWARE_RENDERING") == "1";
        RecordStartup($"Starting {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; software rendering requested: {software}.");
        try { return BuildAvaloniaApp(software).StartWithClassicDesktopLifetime(args); }
        catch (Exception error)
        {
            RecordStartup(error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Trace.Flush();
            if (diagnostics is not null) Trace.Listeners.Remove(diagnostics);
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(false);

    private static AppBuilder BuildAvaloniaApp(bool softwareRendering)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        // Skia/Metal shader compilation has timed out on Intel Mac CI. Software
        // mode is an explicit diagnostic fallback, preserving normal GPU defaults.
        if (softwareRendering && OperatingSystem.IsMacOS())
            builder.With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Software] });
        return builder;
    }

    internal static void RecordStartup(string message) => Trace.WriteLine($"{DateTimeOffset.UtcNow:O} {message}");

    private static TextWriterTraceListener? CreateStartupLog()
    {
        try
        {
            string? requested = Environment.GetEnvironmentVariable("SOUNDPREVIEWER_STARTUP_LOG");
            string folder = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "SoundPreviewer")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundPreviewer", "Logs");
            string path = requested is { Length: > 0 } ? Path.GetFullPath(requested) :
                Path.Combine(folder, $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var listener = new TextWriterTraceListener(new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)));
            Trace.Listeners.Add(listener);
            Trace.AutoFlush = true;
            Console.Error.WriteLine("Startup diagnostics: " + path);
            return listener;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine("Could not create startup diagnostics: " + error.Message);
            return null;
        }
    }
}
