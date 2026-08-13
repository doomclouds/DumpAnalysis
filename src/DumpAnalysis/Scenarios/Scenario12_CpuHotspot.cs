using System.Diagnostics;

namespace DumpAnalysis;

/// <summary>
/// A "record" that gets formatted into a string. In a real app this is a log
/// line, a serialized entity, or a report row being written every iteration.
/// </summary>
public sealed class CpuRecord
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>
/// Scenario 12: a CPU-bound workload with a hidden bottleneck. Two features run
/// together every loop iteration:
///   - ComputeAggregate: pure numeric work over an array (cheap).
///   - BuildRecords: builds N records and string-formats each one with string
///     interpolation + DateTime formatting + Guid allocation (expensive).
/// The numbers loop looks "busy", but the real CPU sink is string/format/allocation
/// in BuildRecords. Profile with `dotnet-trace --profile cpu-sampling` and let
/// `dotnet-trace report ... topN` reveal the hotspot.
/// </summary>
public static class Scenario12_CpuHotspot
{
    public static int Run(string[] args)
    {
        int seconds       = DumpUtil.Arg(args, 0, 30);     // how long to run (s)
        int recordsPerIter = DumpUtil.Arg(args, 1, 2000);  // records formatted per iteration

        DumpUtil.PrintHeader("12", "CPU hotspot (hidden bottleneck)");
        Console.WriteLine($"  seconds={seconds}  recordsPerIter={recordsPerIter}");
        Console.WriteLine();
        Console.WriteLine("Busy loop: numeric aggregate + per-record string formatting.");
        Console.WriteLine("Profile with: dotnet-trace collect -p <PID> --profile cpu-sampling");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        var numbers = Enumerable.Range(0, 10_000).ToArray();
        long total = 0;
        int iter = 0;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            iter++;
            total += ComputeAggregate(numbers);   // cheap-ish numeric work
            total += BuildRecords(recordsPerIter).Count; // expensive per-record formatting

            if (iter % 5 == 0)
                Console.WriteLine($"  iter={iter,5}  total={total,12}  " +
                                  $"elapsed={sw.Elapsed.TotalSeconds,5:F1}s");
        }

        Console.WriteLine($"Stopped after {iter} iterations.");
        return 0;
    }

    /// <summary>Pure numeric aggregate - the "cheap" feature.</summary>
    private static long ComputeAggregate(int[] nums)
    {
        long sum = 0;
        foreach (var n in nums)
            sum += (long)n * n;
        return sum;
    }

    /// <summary>The hidden bottleneck: allocates N records and formats each one
    /// into a string via interpolation + DateTime 'O' + Guid. Each format call
    /// builds a DefaultInterpolatedStringHandler, formats a DateTime, formats a
    /// Guid and allocates a string - CPU + allocation heavy.</summary>
    private static List<string> BuildRecords(int n)
    {
        var now = DateTime.UtcNow;
        var list = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            var rec = new CpuRecord { Id = i, Name = $"item-{i}" };
            // The hotspot: per-record string interpolation with date + guid.
            list.Add($"{rec.Id:D6}|{rec.Name}|{now:O}|{Guid.NewGuid():N}");
        }
        return list;
    }
}
