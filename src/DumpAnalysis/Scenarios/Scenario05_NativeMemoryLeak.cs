using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpAnalysis;

/// <summary>
/// Scenario 05: unmanaged memory leak. Marshal.AllocHGlobal allocates memory
/// OUTSIDE the managed heap. The GC cannot see it, cannot collect it, and it must
/// be released explicitly with Marshal.FreeHGlobal. Here we keep allocating and
/// NEVER free — the managed heap stays tiny while the native private commit grows
/// without bound. In a dump this is invisible to SOS heap commands; you must look
/// at native memory with !address / !heap.
/// </summary>
public static class Scenario05_NativeMemoryLeak
{
    private const int MB = 1024 * 1024;

    public static int Run(string[] args)
    {
        int blockMb = DumpUtil.Arg(args, 0, 4);      // native block size (MB)
        int intervalMs = DumpUtil.Arg(args, 1, 400); // allocation interval (ms)
        int maxBlocks = DumpUtil.Arg(args, 2, 0);    // 0 = run forever

        DumpUtil.PrintHeader("05", "unmanaged memory leak (AllocHGlobal)");
        Console.WriteLine($"  blockMb    : {blockMb}");
        Console.WriteLine($"  intervalMs : {intervalMs}");
        Console.WriteLine($"  maxBlocks  : {(maxBlocks == 0 ? "unlimited" : maxBlocks.ToString())}");
        Console.WriteLine();
        Console.WriteLine("Allocating NATIVE memory (outside the managed heap) WITHOUT freeing ...");
        Console.WriteLine("Capture a full dump now (dotnet-dump / procdump / Task Manager).");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        // Keep the pointers "in use" as a real app would. The bug: Marshal.FreeHGlobal
        // is never called, and the GC has no idea this memory exists.
        var pointers = new List<IntPtr>();

        int count = 0;
        var sw = Stopwatch.StartNew();

        while (maxBlocks == 0 || count < maxBlocks)
        {
            var p = Marshal.AllocHGlobal(blockMb * MB);
            pointers.Add(p);

            // Touch one byte per page so the OS physically commits the memory.
            // Without this, committed-but-untouched pages stay out of the working
            // set and the leak would be invisible to Task Manager's WS column.
            unsafe
            {
                byte* b = (byte*)p.ToPointer();
                for (int i = 0; i < blockMb * MB; i += 4096)
                    b[i] = 0xAB;
            }

            count++;

            if (count % 20 == 0)
            {
                long nativeMb = (long)count * blockMb * MB / MB;
                Console.WriteLine(
                    $"  blocks={count,5}  nativeMB≈{nativeMb,6}  " +
                    $"managedHeap={DumpUtil.Mb(GC.GetTotalMemory(false)),6:F1}MB  " +
                    $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1}MB  elapsed={sw.Elapsed.TotalSeconds,6:F1}s");
            }

            Thread.Sleep(intervalMs);
        }

        Console.WriteLine($"Stopped after {count} blocks. Press Enter to exit.");
        Console.ReadLine();
        return 0;
    }
}
