using System.Diagnostics;
using System.Runtime;

namespace DumpAnalysis;

/// <summary>
/// Scenario 04: LOH fragmentation. Objects >= 85KB go to the LOH, which is NOT
/// compacted by default. We pack the LOH with 2MB blocks and release every other
/// one, leaving 2MB "holes". Then we keep allocating 2.5MB blocks, which can
/// never fit into a 2MB hole, so the LOH keeps extending while the holes sit
/// there forever. Signature: `!dumpheap -stat` shows a huge `Free` row;
/// `!dumpheap -type Free` shows the ~2MB holes; `!gcroot` on any live buffer
/// shows a legitimate reference (it is NOT a reference leak).
/// </summary>
public static class Scenario04_LohFragmentation
{
    private const int MB = 1024 * 1024;
    private const int DELTA_KB = 512;   // fillers are holeSize + 512KB
    private const int PACK_COUNT = 60;  // blocks packed in phase 1

    public static int Run(string[] args)
    {
        int holeMb = DumpUtil.Arg(args, 0, 2);        // hole size in MB
        int intervalMs = DumpUtil.Arg(args, 1, 300);  // filler allocation interval (ms)
        int compactAfter = DumpUtil.Arg(args, 2, 0);  // compact LOH once after N fillers (0 = never)

        DumpUtil.PrintHeader("04", "LOH fragmentation");
        Console.WriteLine($"  holeMb      : {holeMb}");
        Console.WriteLine($"  intervalMs  : {intervalMs}");
        Console.WriteLine($"  compactAfter: {(compactAfter == 0 ? "never" : compactAfter.ToString())}");
        Console.WriteLine();

        int holeSize = holeMb * MB;
        var live = new List<byte[]>();

        // Phase 1: pack the LOH with `holeSize` blocks, then release every other
        // one. The released blocks become Free "holes" in the LOH.
        for (int i = 0; i < PACK_COUNT; i++)
            live.Add(new byte[holeSize]);
        for (int i = live.Count - 2; i >= live.Count - PACK_COUNT; i -= 2)
            live[i] = null!; // release every other block of this wave
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        Console.WriteLine($"  Packed {PACK_COUNT} x {holeMb}MB, released every other -> " +
                          $"{PACK_COUNT / 2} x {holeMb}MB holes (~{DumpUtil.Mb((long)holeSize * PACK_COUNT / 2):F0}MB Free).\n");

        // Phase 2: allocate fillers LARGER than the holes. They cannot fit into
        // the holes, so the LOH keeps extending while the holes stay as Free.
        Console.WriteLine($"Allocating {holeMb}MB+{DELTA_KB}KB fillers - they can't reuse the {holeMb}MB holes.");
        Console.WriteLine("Capture a full dump now (dotnet-dump / procdump / Task Manager).");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        int filler = 0;
        bool compacted = false;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            live.Add(new byte[holeSize + DELTA_KB * 1024]);
            filler++;

            if (filler % 20 == 0)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                var (size, frag) = LohStats();
                Console.WriteLine(
                    $"  fillers={filler,5}  lohSize={DumpUtil.Mb(size),6:F1}MB  lohFree={DumpUtil.Mb(frag),6:F1}MB  " +
                    $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1}MB  elapsed={sw.Elapsed.TotalSeconds,6:F1}s");
            }

            if (compactAfter > 0 && filler == compactAfter && !compacted)
            {
                compacted = true;
                var (s0, f0) = LohStats();
                Console.WriteLine($"\n>>> Before compact : lohSize={DumpUtil.Mb(s0):F1}MB  lohFree={DumpUtil.Mb(f0):F1}MB");
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                var (s1, f1) = LohStats();
                Console.WriteLine($">>> After  compact : lohSize={DumpUtil.Mb(s1):F1}MB  lohFree={DumpUtil.Mb(f1):F1}MB\n");
            }

            Thread.Sleep(intervalMs);
        }
    }

    /// <summary>LOH size and free (fragmentation) after the last GC.</summary>
    private static (long size, long frag) LohStats()
    {
        var mi = GC.GetGCMemoryInfo();
        var loh = mi.GenerationInfo[3]; // gen0..2, LOH, POH
        return (loh.SizeAfterBytes, loh.FragmentationAfterBytes);
    }
}
