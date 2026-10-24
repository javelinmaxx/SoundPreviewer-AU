using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class App
{
    private Mutex? _sourceMutex;
    public static IReadOnlyList<string> StorageStartupMessages { get; private set; } = [];
    public static bool StoragePreparationWindowShown { get; private set; }

    private void BeginInstalledStartup(IClassicDesktopStyleApplicationLifetime desktop, string[] args, int dataIndex)
    {
        if (dataIndex >= 0 && (dataIndex + 1 >= args.Length || args[dataIndex + 1].StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("--data-directory 需要指定旧音色库目录。");
        string application = ApplicationPaths.PortableApplicationDirectory;
        string target = ApplicationPaths.DefaultPreviewDirectory;
        string? requested = dataIndex >= 0 ? args[dataIndex + 1] : null;
        string userData = ApplicationPaths.UserDataDirectory;
        int testUserIndex = Array.IndexOf(args, "--test-user-data-directory");
        if (TestInstalledStorage && testUserIndex >= 0)
        {
            if (testUserIndex + 1 >= args.Length || args[testUserIndex + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("隔离启动检查需要指定历史用户目录。");
            userData = Path.GetFullPath(args[testUserIndex + 1], application);
        }
        var sourceWarnings = new List<string>();
        string source = Path.TrimEndingDirectorySeparator(InstalledStorageService.GetSourceDirectory(application, userData, requested, sourceWarnings));
        bool import = args.Contains("--import-library");
        if (import && requested is null) throw new ArgumentException("导入需要指定旧版音色库。");
        if (import && (ApplicationPaths.IsWithin(source, target) || ApplicationPaths.IsWithin(target, source)))
            throw new ArgumentException("导入来源不能等于、包含或位于当前 Preview 内。");
        _instanceMutex = AcquireLibraryLock(target);
        if (!ApplicationPaths.SamePath(source, target)) _sourceMutex = AcquireLibraryLock(source);
        DataDirectory = target;
        desktop.Exit += (_, eventArgs) =>
        {
            ReleaseLibraryLocks();
            if (SmokeTest && Directory.Exists(DataDirectory))
                File.WriteAllText(Path.Combine(DataDirectory, "ui-exit.json"), JsonSerializer.Serialize(new { ExitCode = eventArgs.ApplicationExitCode }));
        };
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

        if (!import && InstalledStorageService.TryOpenPreparedLibrary(application, source, out var existing))
        {
            OpenInstalledLibrary(desktop, args, existing!, sourceWarnings, show: false);
            return;
        }

        StoragePreparationWindowShown = true;
        var message = new TextBlock { Text = T("正在准备本地音色库…"), TextWrapping = TextWrapping.Wrap };
        var loading = new Window
        {
            Title = T("SoundPreviewer · 准备本地音色库"), Width = 540, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 14, Children =
                {
                    new TextBlock { Text = T("预览与数据保存位置：\n") + target, TextWrapping = TextWrapping.Wrap },
                    message, new ProgressBar { IsIndeterminate = true, Height = 6 }
                }
            }
        };
        bool finished = false;
        loading.Closing += (_, eventArgs) => eventArgs.Cancel = !finished;
        loading.Opened += async (_, _) =>
        {
            Program.RecordStartup("Storage preparation window opened.");
            string? retained = null;
            try
            {
                var progress = new Progress<string>(text => message.Text = text);
                var prepared = await Task.Run(() =>
                {
                    return InstalledStorageService.Prepare(application, userData, source, progress, replaceTarget: import);
                });
                retained = prepared.RetainedDirectory;
                OpenInstalledLibrary(desktop, args, prepared, sourceWarnings, show: true);
            }
            catch (Exception error)
            {
                ReleaseLibraryLocks();
                string detail = error.Message;
                if (retained is not null) detail += "\n导入前的 Preview 已保留（若已恢复原名则位于 Preview）：" + retained;
                FailInstalledStartup(desktop, new IOException(detail, error));
                if (!SmokeTest) desktop.MainWindow?.Show();
            }
            finally { finished = true; loading.Close(); }
        };
        desktop.MainWindow = loading;
    }

    private void OpenInstalledLibrary(IClassicDesktopStyleApplicationLifetime desktop, string[] args,
        InstalledStorageResult prepared, IReadOnlyList<string> sourceWarnings, bool show)
    {
        ApplicationPaths.ConfigureDataDirectory(prepared.Directory);
        DataDirectory = ApplicationPaths.DataDirectory;
        var messages = new List<string> { "预览与数据固定保存到：" + DataDirectory };
        if (!ApplicationPaths.SamePath(prepared.SourceDirectory, prepared.Directory))
            messages.Add("已从旧库复制并校验；原库与原音频保留在原位置：" + prepared.SourceDirectory);
        if (prepared.RetainedDirectory is not null)
            messages.Add("导入前的本地 Preview 完整保留在：" + prepared.RetainedDirectory);
        messages.AddRange(sourceWarnings);
        messages.AddRange(prepared.Warnings);
        StorageStartupMessages = messages;
        ReleaseMutex(ref _sourceMutex);
        var importIndex = Array.IndexOf(args, "--import-settings");
        if (importIndex >= 0 && importIndex + 1 < args.Length)
        {
            ImportedSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[importIndex + 1]), LibraryJson.Options)
                ?? throw new InvalidDataException("设置文件无效。");
            ImportedSettings.PreviewDirectory = DataDirectory;
        }
        // The locator is written only by LibraryOpened after the main window has
        // successfully loaded this library, on both the direct and migration paths.
        _rememberLibraryOnOpen = !SmokeTest;
        var main = new MainWindow();
        Program.RecordStartup("Main window constructed.");
        main.Opened += (_, _) => Program.RecordStartup("Main window opened.");
        desktop.MainWindow = main;
        if (show) main.Show();
    }

    private static Mutex AcquireLibraryLock(string directory)
    {
        string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        string name = (OperatingSystem.IsWindows() ? "Local\\" : "") + "SoundPreviewer-" + Fingerprints.HashText(identity)[..20];
        var mutex = new Mutex(true, name, out bool created);
        if (created) return mutex;
        mutex.Dispose();
        throw new IOException("此音色库正被另一个窗口或旧版本使用。请先退出该程序，再重试迁移：\n" + directory);
    }

    private void ReleaseLibraryLocks()
    {
        ReleaseMutex(ref _sourceMutex);
        ReleaseMutex(ref _instanceMutex);
    }

    private static void ReleaseMutex(ref Mutex? mutex)
    {
        if (mutex is null) return;
        try { mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* A failed isolated startup may hold an unowned handle. */ }
        mutex.Dispose();
        mutex = null;
    }

    private static void FailInstalledStartup(IClassicDesktopStyleApplicationLifetime desktop, Exception error)
    {
        Program.RecordStartup("Storage startup failed: " + error);
        if (!SmokeTest) { ShowStartupMessage(desktop, error.Message, "无法打开本地音色库"); return; }
        if (Directory.Exists(DataDirectory)) File.WriteAllText(Path.Combine(DataDirectory, "ui-startup-error.txt"), error.ToString());
        desktop.Shutdown(1);
    }

}
