using System.Diagnostics;

namespace DumpAnalysis;

/// <summary>
/// Scenario 10: thread-pool starvation from sync-over-async. A console app has no
/// SynchronizationContext, so async continuations run on the thread pool. When
/// pool threads synchronously block on async work whose continuation ALSO needs a
/// pool thread (.GetAwaiter().GetResult() on an async method that awaits), the
/// pool starves: every worker is blocked waiting for a worker that can never be
/// scheduled. The dump shows many threads stuck in Task.Wait / Task internals and
/// zero available pool threads.
/// </summary>
public static class Scenario10_SyncOverAsync
{
    public static int Run(string[] args)
    {
        int workers  = DumpUtil.Arg(args, 0, 16);  // number of blocking callers
        int interval = DumpUtil.Arg(args, 1, 50);  // start interval (ms)

        DumpUtil.PrintHeader("10", "thread-pool starvation (sync-over-async)");
        Console.WriteLine($"  workers={workers}  intervalMs={interval}");

        // Cap the pool so the starvation is deterministic and visible: with a
        // small max, the sync-over-async callers occupy every worker and the
        // continuations that need a worker can never run. (In a real app the max
        // is higher, but a burst of blocking calls can saturate it the same way -
        // the dump signature is identical: many threads blocked in Task.Wait,
        // pool-available drops to ~0.)
        // SetMaxThreads refuses a value below the current min (default min =
        // ProcessorCount), so lower min first, then cap the max.
        bool minOk = ThreadPool.SetMinThreads(1, 1);
        bool maxOk = ThreadPool.SetMaxThreads(4, 4);
        ThreadPool.GetMaxThreads(out int maxW, out int maxIo);
        Console.WriteLine($"  pool capped to 4 workers (SetMinThreads={minOk}, SetMaxThreads={maxOk}, " +
                          $"GetMaxThreads worker={maxW}, io={maxIo})");
        Console.WriteLine("Blocking pool threads on async work that needs the pool ...");
        Console.WriteLine("Capture a full dump now. Press Ctrl+C to stop.\n");

        for (int i = 0; i < workers; i++)
        {
            int idx = i;
            Task.Run(() => PoisonCaller(idx)); // each needs a pool thread
            Thread.Sleep(interval);
        }

        var sw = Stopwatch.StartNew();
        while (true)
        {
            ThreadPool.GetAvailableThreads(out int avail, out int _);
            Console.WriteLine($"  blocked-for {sw.Elapsed.TotalSeconds,5:F1}s  " +
                              $"pool-available={avail,4}  " +
                              $"threads={Process.GetCurrentProcess().Threads.Count}  " +
                              $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1}MB");
            Thread.Sleep(1000);
        }
    }

    private static void PoisonCaller(int idx)
    {
        // Classic sync-over-async: synchronously block a pool thread on an async
        // operation whose continuation ALSO needs a pool thread. Result: this
        // thread never unblocks unless a worker frees up.
        Console.WriteLine($"  [worker {idx,2}] blocking on async work ...");
        RunAsync(idx).GetAwaiter().GetResult();
        Console.WriteLine($"  [worker {idx,2}] resumed (should rarely print)");
    }

    private static async Task RunAsync(int idx)
    {
        // The continuation after this await is scheduled onto the thread pool.
        // With every worker blocked in GetAwaiter().GetResult(), no worker is
        // free to run it -> the task never completes -> starvation.
        await Task.Delay(2_000);
        Thread.Sleep(2_000); // hold the worker to keep the pool starved
        Console.WriteLine($"  [worker {idx,2}] got a pool thread (resumed)");
    }
}
