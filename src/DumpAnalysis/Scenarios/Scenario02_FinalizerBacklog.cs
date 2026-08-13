using System.Diagnostics;

namespace DumpAnalysis;

/// <summary>
/// A disposable object whose finalizer is slow. The app allocates it and never
/// calls Dispose(), so every instance relies on finalization. Because there is a
/// single finalizer thread, a slow finalizer turns into a bottleneck: un-finalized
/// objects (and the buffers they reference) pile up in the fReachable queue and
/// get promoted generation by generation.
/// </summary>
public sealed class FinalizerBacklogItem : IDisposable
{
    private static int _finalizedCount;

    public readonly int Id;
    public readonly byte[] Data; // ~80 KB -> below the LOH threshold, stays in gen0/gen1

    public static int FinalizedCount => Volatile.Read(ref _finalizedCount);

    public FinalizerBacklogItem(int id, int dataSizeBytes)
    {
        Id = id;
        Data = new byte[dataSizeBytes];
        Data.AsSpan().Fill(0x3C); // '<' - prove the buffer is committed
    }

    ~FinalizerBacklogItem()
    {
        // The real-world bug: cleanup on the finalizer thread is slow (native
        // wait, lock, I/O...). One finalizer thread => the queue backs up.
        Thread.Sleep(1000);
        Interlocked.Increment(ref _finalizedCount);
    }

    public void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>
/// Scenario 02: finalizer backlog. Objects are allocated but never disposed, and
/// the slow finalizer backs up the "Ready for finalization" (fReachable) queue.
/// Signature: `!finalizequeue` shows a large backlog; `!gcroot` shows the root is
/// the finalizer queue (no business reference).
/// </summary>
public static class Scenario02_FinalizerBacklog
{
    public static int Run(string[] args)
    {
        int dataSizeKb = DumpUtil.Arg(args, 0, 80);  // per-item buffer size (KB)
        int intervalMs = DumpUtil.Arg(args, 1, 50);   // allocation interval (ms)
        int maxItems  = DumpUtil.Arg(args, 2, 0);     // 0 = run forever

        DumpUtil.PrintHeader("02", "finalizer backlog (fReachable queue)");
        Console.WriteLine($"  dataSizeKb : {dataSizeKb}");
        Console.WriteLine($"  intervalMs : {intervalMs}");
        Console.WriteLine($"  maxItems   : {(maxItems == 0 ? "unlimited" : maxItems.ToString())}");
        Console.WriteLine();
        Console.WriteLine("Allocating finalizable objects WITHOUT Dispose() ...");
        Console.WriteLine("Capture a full dump now (dotnet-dump / procdump / Task Manager).");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        int count = 0;
        var sw = Stopwatch.StartNew();

        while (maxItems == 0 || count < maxItems)
        {
            _ = new FinalizerBacklogItem(count++, dataSizeKb * 1024);

            // Force GCs so unreachable finalizable objects move to fReachable.
            // Because the finalizer is slow, the queue backs up and the objects
            // get promoted instead of being reclaimed.
            if (count % 50 == 0)
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

            if (count % 200 == 0)
            {
                int finalized = FinalizerBacklogItem.FinalizedCount;
                Console.WriteLine(
                    $"  allocated={count,6}  finalized={finalized,6}  backlog={count - finalized,6}  " +
                    $"heap={DumpUtil.Mb(GC.GetTotalMemory(false)),6:F1} MB  " +
                    $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1} MB  elapsed={sw.Elapsed.TotalSeconds,6:F1}s");
            }

            Thread.Sleep(intervalMs);
        }

        Console.WriteLine($"Stopped after {count} items. Press Enter to exit.");
        Console.ReadLine();
        return 0;
    }
}
