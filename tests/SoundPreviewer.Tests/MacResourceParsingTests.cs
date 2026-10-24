using System.Reflection;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static Task TestMacResourceParsingAsync()
    {
        // Exercise actual macOS ps text on every CI OS; native process sampling
        // remains covered by TestSystemRenderResourceProbeAsync on macOS.
        var parse = typeof(SystemRenderResourceProbe)
            .GetMethod("ParseMacProcessTable", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, int, RenderMemorySnapshot>>();
        var sample = parse(" 99 1 99999 00:01.00\n\n\t12\t11\t256\t1-02:03:04.50\r\n 10 1 64 00:01.25\n11 10 128 02:03.50", 10);
        True(sample.IsComplete, "A complete process table retains memory admission eligibility.");
        Equal(3, sample.ProcessCount, "Out-of-order descendants are included and unrelated processes excluded.");
        Equal(448L * 1024, sample.ResidentBytes, "macOS ps reports RSS in KiB, not bytes.");
        Equal(64L * 1024, sample.RootResidentBytes, "The main application's RSS remains separate.");
        Equal<double?>(1.25, sample.Processes.Single(process => process.ProcessId == 10).CpuSeconds, "Fractional CPU seconds survive parsing.");
        Equal<double?>(123.5, sample.Processes.Single(process => process.ProcessId == 11).CpuSeconds, "Minute CPU counters are converted to seconds.");
        Equal<double?>(93784.5, sample.Processes.Single(process => process.ProcessId == 12).CpuSeconds, "Multi-day CPU counters retain hours and fractions.");
        foreach (string counter in new[] { "?", "00:x", "1:2:3:4", "-1:00", "1-00:x" })
        {
            var unknownCpu = parse("10 1 64 " + counter, 10);
            True(unknownCpu.IsComplete && unknownCpu.Processes.Single().CpuSeconds is null,
                "An unavailable CPU counter must not discard a readable memory counter.");
        }
        foreach (string malformed in new[] { "10 1 64", "10 1 64 00:01 extra", "x 1 64 00:01", "10 x 64 00:01", "10 1 -64 00:01", "   " })
            Throws<FormatException>(() => parse(malformed, 10), "Malformed memory records must fail closed.");
        Throws<OverflowException>(() => parse("10 1 9223372036854775807 00:01", 10), "RSS conversion overflow must fail closed.");
        True(!parse("", 10).IsComplete && !parse("11 1 64 00:01", 10).IsComplete,
            "Empty tables and a missing root cannot be treated as zero-memory success.");
        return Task.CompletedTask;
    }
}
