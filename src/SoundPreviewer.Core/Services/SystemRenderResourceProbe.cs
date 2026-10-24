using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SoundPreviewer.Core.Services;

/// <summary>Samples resident memory of one process and its descendants; this is not an OS memory quota.</summary>
public sealed class SystemRenderResourceProbe : IRenderResourceProbe
{
    private readonly int rootProcessId;
    private readonly Lazy<RenderHardwareInfo> hardware = new(DetectHardware);
    public RenderHardwareInfo Hardware => hardware.Value;

    public SystemRenderResourceProbe(int? rootProcessId = null)
    {
        this.rootProcessId = rootProcessId ?? Environment.ProcessId;
        if (this.rootProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(rootProcessId));
    }

    public RenderMemorySnapshot Capture()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return CaptureWindows();
            if (OperatingSystem.IsMacOS()) return CaptureMac();
            return new(0, false, 0, "当前平台没有受支持的进程树内存探测器。");
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or FormatException or OverflowException)
        { return new(0, false, 0, "无法完整读取本程序进程树内存：" + error.Message); }
    }

    private RenderMemorySnapshot CaptureWindows()
    {
        using var snapshot = Native.CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS, no unrelated module scans.
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var entry = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>(), Executable = "" };
        var table = new List<RenderProcessUsage>();
        if (!Native.Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        do { table.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, 0, null)); }
        while (Native.Process32Next(snapshot, ref entry));
        if (Marshal.GetLastPInvokeError() != 18) throw new Win32Exception(Marshal.GetLastPInvokeError()); // ERROR_NO_MORE_FILES
        var family = RenderMemorySnapshot.FromProcesses(rootProcessId, table);
        if (!family.IsComplete) return family;
        var usages = new List<RenderProcessUsage>();
        bool complete = true;
        string? error = null;
        foreach (var candidate in family.Processes)
        {
            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                process.Refresh();
                long resident = process.WorkingSet64;
                double? cpu = null;
                try { cpu = process.TotalProcessorTime.TotalSeconds; }
                catch (Exception cpuError) when (cpuError is Win32Exception or InvalidOperationException or NotSupportedException) { }
                usages.Add(candidate with { ResidentBytes = resident, CpuSeconds = cpu });
            }
            catch (ArgumentException) { } // A short-lived child exited after the process snapshot.
            catch (InvalidOperationException) { }
            catch (Win32Exception memoryError)
            {
                // Invalid PID / a signaled process is a normal sampling race. Access denied is not.
                if (memoryError.NativeErrorCode is 87 or 1168 || HasExited(candidate.ProcessId)) continue;
                complete = false;
                error ??= "无法读取进程 " + candidate.ProcessId + " 的内存：" + memoryError.Message;
            }
        }
        // Membership was fixed by Toolhelp before querying counters. An exited intermediate
        // parent must not hide a still-readable grandchild from this same snapshot.
        long residentBytes = 0;
        foreach (var process in usages) residentBytes = checked(residentBytes + process.ResidentBytes);
        var root = usages.FirstOrDefault(process => process.ProcessId == rootProcessId);
        return new(residentBytes, complete && root is not null, usages.Count, error ?? (root is null ? "无法读取主程序进程的内存占用。" : null))
        {
            RootResidentBytes = root?.ResidentBytes ?? 0, Processes = usages
        };
    }

    private static bool HasExited(int processId)
    {
        using var handle = Native.OpenProcess(0x00100000, false, processId); // SYNCHRONIZE only
        if (handle.IsInvalid) return Marshal.GetLastPInvokeError() is 87 or 1168;
        return Native.WaitForSingleObject(handle, 0) == 0;
    }

    private RenderMemorySnapshot CaptureMac()
    {
        string output = RunSystemTool("/bin/ps", ["-axo", "pid=,ppid=,rss=,time="]);
        return ParseMacProcessTable(output, rootProcessId);
    }

    private static RenderMemorySnapshot ParseMacProcessTable(string output, int rootProcessId)
    {
        var table = new List<RenderProcessUsage>();
        // Sampling runs every 100 ms. Slice the ps output in place instead of
        // allocating a line, field array and four strings for every system PID.
        ReadOnlySpan<char> remaining = output.AsSpan();
        Span<Range> fields = stackalloc Range[5]; // The fifth field detects unexpected trailing columns.
        while (!remaining.IsEmpty)
        {
            int newline = remaining.IndexOf('\n');
            ReadOnlySpan<char> line = newline < 0 ? remaining : remaining[..newline];
            remaining = newline < 0 ? [] : remaining[(newline + 1)..];
            if (line.IsEmpty) continue;
            int count = line.SplitAny(fields, " \t\r", StringSplitOptions.RemoveEmptyEntries);
            if (count != 4 || !int.TryParse(line[fields[0]], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ||
                !int.TryParse(line[fields[1]], NumberStyles.None, CultureInfo.InvariantCulture, out int parent) ||
                !long.TryParse(line[fields[2]], NumberStyles.None, CultureInfo.InvariantCulture, out long kilobytes))
                throw new FormatException("ps 返回了无法识别的进程表。");
            table.Add(new(pid, parent, checked(kilobytes * 1024), ParseCpuSeconds(line[fields[3]])));
        }
        return RenderMemorySnapshot.FromProcesses(rootProcessId, table);
    }

    private static double? ParseCpuSeconds(ReadOnlySpan<char> value)
    {
        double days = 0;
        int dash = value.IndexOf('-');
        if (dash >= 0)
        {
            if (!double.TryParse(value[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out days)) return null;
            value = value[(dash + 1)..];
        }
        Span<Range> parts = stackalloc Range[4];
        int count = value.Split(parts, ':');
        if (count is < 2 or > 3) return null;
        double seconds = 0;
        for (int i = 0; i < count; i++)
        {
            if (!double.TryParse(value[parts[i]], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double component) || component < 0) return null;
            seconds = seconds * 60 + component;
        }
        seconds += days * 86400;
        return double.IsFinite(seconds) ? seconds : null;
    }

    private static RenderHardwareInfo DetectHardware()
    {
        int logical = Math.Max(1, Environment.ProcessorCount);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                uint length = 0;
                Native.GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
                if (Marshal.GetLastPInvokeError() != 122 || length is < 8 or > 1024 * 1024) throw new Win32Exception(Marshal.GetLastPInvokeError());
                IntPtr buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!Native.GetLogicalProcessorInformationEx(0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    int count = 0, offset = 0;
                    while (offset < length)
                    {
                        if (length - offset < 8) throw new InvalidOperationException("处理器拓扑记录不完整。");
                        int relationship = Marshal.ReadInt32(buffer, offset), size = Marshal.ReadInt32(buffer, offset + 4);
                        if (size < 8 || size > length - offset) throw new InvalidOperationException("处理器拓扑记录无效。");
                        if (relationship == 0) count++;
                        offset += size;
                    }
                    if (count > 0) return new(logical, count) { DetectionMethod = "Windows GetLogicalProcessorInformationEx" };
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            else if (OperatingSystem.IsMacOS())
            {
                // One read-only call per probe, not a subprocess on every memory sample.
                if (int.TryParse(RunSystemTool("/usr/sbin/sysctl", ["-n", "hw.physicalcpu"]).Trim(), out int cores) && cores > 0)
                    return new(logical, cores) { DetectionMethod = "macOS sysctl hw.physicalcpu" };
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException) { }
        return new(logical, null) { DetectionMethod = "未知物理核数；自动档按可用逻辑线程的一半保守选择" };
    }

    private static string RunSystemTool(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统资源探测工具。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        bool timedOut = !process.WaitForExit(2000);
        if (timedOut)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        string text = output.GetAwaiter().GetResult(), errors = error.GetAwaiter().GetResult();
        if (timedOut) throw new IOException("系统资源探测工具超时。");
        if (process.ExitCode != 0) throw new IOException("系统资源探测工具失败：" + errors);
        return text;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeap;
            public uint ModuleId, Threads, ParentProcessId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
        }

        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeWaitHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);
    }
}
