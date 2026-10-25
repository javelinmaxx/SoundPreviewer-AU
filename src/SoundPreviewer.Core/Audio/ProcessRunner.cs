using System.Diagnostics;
using System.Text;

namespace SoundPreviewer.Core.Audio;

public sealed record ProcessOutcome(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null, Action<int>? processStarted = null,
        bool inheritProcessGroup = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environmentOverrides is not null)
            foreach (var (name, value) in environmentOverrides)
                if (value is null) start.Environment.Remove(name);
                else start.Environment[name] = value;
        // Nested desktop bridges already belong to a supervised worker group.
        // Keep their children in it so killing that worker cannot orphan an AU host.
        string? processGroupFile = inheritProcessGroup ? null : ProcessJob.ConfigureStart(start);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("无法启动渲染工作进程。");
        }
        catch
        {
            ProcessJob.DeleteProcessGroupFile(processGroupFile);
            throw;
        }
        ProcessJob job;
        try { job = new ProcessJob(process, processGroupFile, inheritProcessGroup); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            ProcessJob.DeleteProcessGroupFile(processGroupFile);
            throw;
        }
        using var containment = job;
        using var outputCancellation = new CancellationTokenSource();
        var stdout = ReadBoundedAsync(process.StandardOutput, outputCancellation.Token);
        var stderr = ReadBoundedAsync(process.StandardError, outputCancellation.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            processStarted?.Invoke(process.Id);
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            containment.Dispose();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            // An independently detached descendant can retain an inherited pipe even after
            // the worker exits. Cancellation must still finish within the requested timeout.
            outputCancellation.Cancel();
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"渲染进程超过 {timeout.TotalSeconds:0} 秒，已结束该进程及其子进程。");
        }
        return new ProcessOutcome(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximum = 16000;
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            int retained = Math.Min(count, maximum - text.Length);
            if (retained > 0) text.Append(buffer, 0, retained);
        }
        return text.ToString();
    }
}
