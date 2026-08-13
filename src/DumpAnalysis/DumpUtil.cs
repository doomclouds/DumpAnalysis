using System.Runtime;
using System.Runtime.InteropServices;

namespace DumpAnalysis;

/// <summary>
/// Shared helpers for every scenario: arg parsing, MB formatting, and the
/// process header that makes a run dump-analysis-ready (prints the PID and
/// runtime identity you need before capturing a dump).
/// </summary>
internal static class DumpUtil
{
    public static int Arg(string[] args, int index, int fallback) =>
        index < args.Length && int.TryParse(args[index], out var v) ? v : fallback;

    public static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    public static void PrintHeader(string id, string description)
    {
        Console.WriteLine($"=== Scenario {id}: {description} ===");
        Console.WriteLine($"  PID        : {Environment.ProcessId}");
        Console.WriteLine($"  Runtime    : {RuntimeInformation.FrameworkDescription} ({Environment.Version})");
        Console.WriteLine($"  GC mode    : {(GCSettings.IsServerGC ? "Server" : "Workstation")}");
        Console.WriteLine($"  64-bit     : {Environment.Is64BitProcess}");
        Console.WriteLine();
    }
}
