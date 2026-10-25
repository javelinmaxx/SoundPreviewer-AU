using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoundPreviewer.Core.Audio;

internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle? handle;
    private readonly Process process;
    private readonly string? processGroupFile;
    private int processGroup;
    private bool disposed;

    // macOS has no Windows job objects. A small system-shell supervisor starts the actual
    // command in its own process group and waits for it. Its EXIT trap also removes any
    // descendants that outlive the command, including descendants holding redirected pipes.
    // All command/path values are positional arguments, never interpolated into shell code.
    internal static string? ConfigureStart(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        string marker = Path.Combine(Path.GetFullPath(start.WorkingDirectory), $".soundpreviewer-process-{Guid.NewGuid():N}.pid");
        string executable = start.FileName;
        string[] arguments = start.ArgumentList.ToArray();
        start.FileName = "/bin/sh";
        start.ArgumentList.Clear();
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            set -m || exit 125
            marker=$1
            shift
            "$@" &
            child=$!
            trap 'kill -KILL -- -"$child" 2>/dev/null || :' EXIT
            printf '%s\n' "$child" > "$marker" || exit 125
            wait "$child" 2>/dev/null
            result=$?
            exit "$result"
            """);
        start.ArgumentList.Add("SoundPreviewer");
        start.ArgumentList.Add(marker);
        start.ArgumentList.Add(executable);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return marker;
    }

    public ProcessJob(Process process, string? processGroupFile = null, bool inheritProcessGroup = false)
    {
        this.process = process;
        this.processGroupFile = processGroupFile;
        if (!OperatingSystem.IsWindows())
        {
            if (OperatingSystem.IsMacOS() && !inheritProcessGroup) ReadProcessGroup();
            return;
        }
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建渲染进程隔离作业。");
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        int size = Marshal.SizeOf<ExtendedLimits>();
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, memory, false);
            if (!SetInformationJobObject(handle, 9, memory, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置渲染进程退出清理。");
            if (!AssignProcessToJobObject(handle, process.Handle) && !process.HasExited)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法隔离渲染工作进程。");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (handle is not null) handle.Dispose();
        else
        {
            // Signal the group before the supervisor: killing only an exited parent cannot
            // discover its reparented children with Process.Kill(entireProcessTree: true).
            if (processGroup > 0 && !process.HasExited) KillProcessGroup(-processGroup, 9);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        DeleteProcessGroupFile(processGroupFile);
    }

    private void ReadProcessGroup()
    {
        if (processGroupFile is null) throw new InvalidOperationException("macOS 渲染进程缺少隔离配置。");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                string text = File.ReadAllText(processGroupFile).Trim();
                if (int.TryParse(text, out int group) && group > 0)
                {
                    processGroup = group;
                    DeleteProcessGroupFile(processGroupFile);
                    return;
                }
            }
            catch (FileNotFoundException) { }
            // The supervisor may fail (for example, a read-only working directory). Let its
            // exit code/stderr describe the failure instead of waiting five seconds.
            if (process.HasExited) return;
            Thread.Sleep(5);
        }
        throw new IOException("无法确认 macOS 渲染进程隔离组。");
    }

    internal static void DeleteProcessGroupFile(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillProcessGroup(int processId, int signal);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTime;
        public long PerJobUserTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
