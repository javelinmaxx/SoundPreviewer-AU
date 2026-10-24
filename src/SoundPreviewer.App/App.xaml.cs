using System.Text.Json;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _rememberLibraryOnOpen;
    public static string DataDirectory { get; private set; } = "";
    public static bool SmokeTest { get; private set; }
    public static bool TestInstalledStorage { get; private set; }
    public static bool ScanOnStart { get; private set; }
    public static AppSettings? ImportedSettings { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Program.RecordStartup("Avalonia framework initialized.");
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        var args = desktop.Args ?? [];
        TestInstalledStorage = args.Contains("--test-installed-storage");
        SmokeTest = args.Contains("--smoke-test") || TestInstalledStorage;
        ScanOnStart = args.Contains("--scan-on-start");
        var dataIndex = Array.IndexOf(args, "--data-directory");
        if (!SmokeTest || TestInstalledStorage)
        {
            try { BeginInstalledStartup(desktop, args, dataIndex); }
            catch (Exception error)
            {
                ReleaseLibraryLocks();
                FailInstalledStartup(desktop, error);
            }
            base.OnFrameworkInitializationCompleted();
            return;
        }
        try
        {
            if (dataIndex >= 0 && (dataIndex + 1 >= args.Length || args[dataIndex + 1].StartsWith("--", StringComparison.Ordinal)))
                throw new ArgumentException("--data-directory 需要指定音色库目录。");
            ApplicationPaths.ConfigureDataDirectory(dataIndex >= 0 ? args[dataIndex + 1] : null);
            DataDirectory = ApplicationPaths.DataDirectory;
            ApplicationPaths.ConfigureConfigurationDirectory(DataDirectory);
            _rememberLibraryOnOpen = !SmokeTest && (args.Contains("--remember-data-directory") ||
                (dataIndex < 0 && ApplicationPaths.SamePath(DataDirectory, ApplicationPaths.LegacyDataDirectory)));
            Directory.CreateDirectory(DataDirectory);
            var identity = OperatingSystem.IsWindows() ? DataDirectory.ToUpperInvariant() : DataDirectory;
            var name = (OperatingSystem.IsWindows() ? "Local\\" : "") + "SoundPreviewer-" + Fingerprints.HashText(identity)[..20];
            _instanceMutex = new Mutex(true, name, out var created);
            if (!created)
            {
                ShowStartupMessage(desktop, "此音色库已经打开。", "SoundPreviewer");
                base.OnFrameworkInitializationCompleted();
                return;
            }
            Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                if (SmokeTest)
                {
                    File.WriteAllText(Path.Combine(DataDirectory, "ui-error.txt"), eventArgs.Exception.ToString());
                    desktop.Shutdown(1);
                }
                else if (desktop.MainWindow is { } owner) _ = Dialogs.ShowAsync(owner, eventArgs.Exception.Message, "操作未完成");
            };
            var importIndex = Array.IndexOf(args, "--import-settings");
            if (importIndex >= 0 && importIndex + 1 < args.Length)
                ImportedSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[importIndex + 1]), LibraryJson.Options)
                    ?? throw new InvalidDataException("设置文件无效。");
            desktop.MainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            if (SmokeTest)
            {
                if (DataDirectory.Length > 0 && Directory.Exists(DataDirectory))
                    File.WriteAllText(Path.Combine(DataDirectory, "ui-startup-error.txt"), ex.ToString());
                desktop.Shutdown(1);
            }
            else ShowStartupMessage(desktop, ex.Message, "无法打开本地音色库");
        }
        desktop.Exit += (_, eventArgs) =>
        {
            _instanceMutex?.Dispose();
            if (SmokeTest && DataDirectory.Length > 0 && Directory.Exists(DataDirectory))
                File.WriteAllText(Path.Combine(DataDirectory, "ui-exit.json"), JsonSerializer.Serialize(new { ExitCode = eventArgs.ApplicationExitCode }));
        };
        base.OnFrameworkInitializationCompleted();
    }

    public static void Shutdown(int exitCode = 0) =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(exitCode);

    public static void LibraryOpened()
    {
        if (Current is not App { _rememberLibraryOnOpen: true } app) return;
        ApplicationPaths.RememberDataDirectory(DataDirectory, ApplicationPaths.PortableApplicationDirectory);
        app._rememberLibraryOnOpen = false;
    }

    public static void StartWithLibrary(string directory, bool requireExistingLibrary = true, bool importLibrary = false)
    {
        if (requireExistingLibrary && !File.Exists(Path.Combine(directory, "library.db")))
            throw new ArgumentException("所选目录没有 library.db，请选择原来的音色库数据目录。");
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序。")) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SoundPreviewer.dll"));
        start.ArgumentList.Add("--data-directory");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("--remember-data-directory");
        if (importLibrary) start.ArgumentList.Add("--import-library");
        var app = Current as App;
        app?.ReleaseLibraryLocks();
        try { using var process = Process.Start(start) ?? throw new InvalidOperationException("无法打开音色库。"); }
        catch
        {
            if (app is not null) app._instanceMutex = AcquireLibraryLock(DataDirectory);
            throw;
        }
    }

    private static void ShowStartupMessage(IClassicDesktopStyleApplicationLifetime desktop, string text, string title)
    {
        var window = new Window { Title = T(title), Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var close = new Button { Content = T("确定"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        close.Click += (_, _) => window.Close();
        var message = new TextBlock { Text = T(text), TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Margin = new Thickness(24), Children = { message } };
        if (title == "无法打开本地音色库")
        {
            var browse = new Button { Content = T("选择旧版音色库作为迁移来源"), Margin = new Thickness(0, 16, 0, 0) };
            browse.Click += async (_, _) =>
            {
                try
                {
                    var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("选择含 library.db 的旧库；生成文件仍保存到本程序 Preview"), AllowMultiple = false });
                    if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } directory) return;
                    bool replace = Directory.Exists(ApplicationPaths.DefaultPreviewDirectory) && Directory.EnumerateFileSystemEntries(ApplicationPaths.DefaultPreviewDirectory).Any();
                    if (replace && !await Dialogs.ShowAsync(window, "将先复制并校验所选旧库，再把当前 Preview 完整保留到程序旁的独立备份目录。所选库将成为新的本地 Preview，不合并两份库，原库和音频保留。", "导入旧版音色库", question: true)) return;
                    if (Current is App app)
                    {
                        app.ReleaseLibraryLocks();
                    }
                    StartWithLibrary(directory, importLibrary: replace);
                    window.Close();
                }
                catch (Exception error) { message.Text = error.Message; }
            };
            content.Children.Add(browse);
            content.Children.Add(new TextBlock { Text = T("数据固定保存到程序旁的 Preview。安装目录不可写时，请将程序放到可写目录后重试；迁移失败时原库保持原样。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        }
        content.Children.Add(close);
        window.Content = content;
        window.Opened += (_, _) => Program.RecordStartup("Startup message window opened: " + title);
        desktop.MainWindow = window;
    }
}
