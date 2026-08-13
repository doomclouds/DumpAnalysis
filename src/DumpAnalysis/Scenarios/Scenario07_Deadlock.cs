using System.Diagnostics;

namespace DumpAnalysis;

/// <summary>
/// Scenario 07: classic thread deadlock. Two worker threads grab a pair of locks
/// in opposite orders (A then B vs B then A), so they can permanently block on
/// each other. In a dump this is invisible to SOS heap commands; you must use
/// `!threads` + `!clrstack` to see both threads sleeping in a monitor wait, then
/// `!syncblk` to connect the two owners.
/// </summary>
public static class Scenario07_Deadlock
{
    private static readonly object LockA = new();
    private static readonly object LockB = new();

    // Set right before each thread acquires its first lock, read by the main
    // thread to know when the deadlock has set in (and the dump can be taken).
    private static int _worker1HoldingA;
    private static int _worker2HoldingB;

    public static int Run(string[] args)
    {
        DumpUtil.PrintHeader("07", "thread deadlock (crossed lock ordering)");
        Console.WriteLine("Thread 1: lock(A) -> lock(B)   Thread 2: lock(B) -> lock(A)");
        Console.WriteLine("Both threads will block forever. Capture a full dump now.");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        var t1 = new Thread(() =>
        {
            lock (LockA)
            {
                Volatile.Write(ref _worker1HoldingA, 1);
                // Wait until thread 2 has taken LockB (guarantees the crossed
                // ordering), then try to take LockB -> blocks forever.
                SpinWait.SpinUntil(() => Volatile.Read(ref _worker2HoldingB) == 1);
                Console.WriteLine("  [t1] holding A, waiting on B ...");
                lock (LockB)
                {
                    Console.WriteLine("  [t1] got B (should never print)");
                }
            }
        }) { Name = "DeadlockT1" };

        var t2 = new Thread(() =>
        {
            lock (LockB)
            {
                Volatile.Write(ref _worker2HoldingB, 1);
                SpinWait.SpinUntil(() => Volatile.Read(ref _worker1HoldingA) == 1);
                Console.WriteLine("  [t2] holding B, waiting on A ...");
                lock (LockA)
                {
                    Console.WriteLine("  [t2] got A (should never print)");
                }
            }
        }) { Name = "DeadlockT2" };

        t1.Start();
        t2.Start();

        // Wait until both threads are stuck, then report forever (dumping time).
        while (Volatile.Read(ref _worker1HoldingA) == 0 || Volatile.Read(ref _worker2HoldingB) == 0)
            Thread.Sleep(10);

        Console.WriteLine("  >>> Both threads are deadlocked (t1 holds A waits B; t2 holds B waits A).");
        Console.WriteLine("  >>> Capture the dump now, then Ctrl+C to exit.\n");

        var sw = Stopwatch.StartNew();
        while (true)
        {
            Console.WriteLine($"  deadlocked for {sw.Elapsed.TotalSeconds,5:F1}s  " +
                              $"threads={Process.GetCurrentProcess().Threads.Count}  " +
                              $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1}MB");
            Thread.Sleep(1000);
        }
    }
}
