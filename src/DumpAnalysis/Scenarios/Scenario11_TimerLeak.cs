using System.Diagnostics;

namespace DumpAnalysis;

/// <summary>
/// A timer "job" that holds a payload. In a real app the payload could be a
/// buffer, a connection, or a reference into a larger object graph.
/// </summary>
public sealed class TimerJob
{
    public readonly int Id;
    public readonly byte[] Payload;

    public TimerJob(int id, int payloadSizeBytes)
    {
        Id = id;
        Payload = new byte[payloadSizeBytes];
        Payload.AsSpan().Fill(0x2A); // '*' - prove the buffer is committed
    }
}

/// <summary>
/// Scenario 11: Timer leak. Each iteration creates a System.Threading.Timer that
/// is NEVER disposed, and BOTH the job and the Timer are kept by static lists.
/// Rooting the Timer is the crucial part: an unrooted Timer would be finalized
/// (TimerHolder's finalizer disposes it and removes it from the queue), hiding the
/// leak. Here every Timer stays alive forever, sits in the timer queue, keeps
/// firing, and pins its state (the TimerJob + payload) via the queue. Signature:
/// `!dumpheap -stat` shows TimerJob + System.Threading.Timer/TimerQueueTimer
/// growing together.
/// </summary>
public static class Scenario11_TimerLeak
{
    // THE LEAK: rooting every Timer prevents its finalizer from running, so the
    // timer queue entry (TimerQueueTimer) stays forever and its state (the job +
    // payload) is pinned. The jobs are NOT held anywhere else - the ONLY path to
    // each job is through its Timer, which is exactly the timer-leak signature.
    private static readonly List<System.Threading.Timer> Timers = [];

    public static int Run(string[] args)
    {
        int payloadKb = DumpUtil.Arg(args, 0, 64);  // per-job payload size (KB)
        int intervalMs = DumpUtil.Arg(args, 1, 100); // job creation interval (ms)

        DumpUtil.PrintHeader("11", "Timer leak (System.Threading.Timer never disposed)");
        Console.WriteLine($"  payloadKb  : {payloadKb}");
        Console.WriteLine($"  intervalMs : {intervalMs}");
        Console.WriteLine();
        Console.WriteLine("Creating System.Threading.Timer objects WITHOUT disposing ...");
        Console.WriteLine("Capture a full dump now. Press Ctrl+C to stop.\n");

        int count = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            var job = new TimerJob(count++, payloadKb * 1024);

            // Each timer fires a no-op callback every second. The timer queue keeps
            // a strong reference to the callback+state (here `job`) while the timer
            // is scheduled, and the static list keeps the Timer alive so it is never
            // finalized/disposed. The Timer is never disposed -> its queue entry and
            // its state (job + payload) stay forever.
            Timers.Add(new System.Threading.Timer(_ => { }, job, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

            if (count % 200 == 0)
            {
                Console.WriteLine(
                    $"  jobs={count,7}  heap={DumpUtil.Mb(GC.GetTotalMemory(false)),6:F1} MB  " +
                    $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1} MB  elapsed={sw.Elapsed.TotalSeconds,6:F1}s");
            }

            Thread.Sleep(intervalMs);
        }
    }
}
