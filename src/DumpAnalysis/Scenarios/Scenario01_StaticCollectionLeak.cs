using System.Diagnostics;

namespace DumpAnalysis;

/// <summary>
/// A cached domain object with a large buffer. In a real app this could be a
/// cached entity, an uploaded image, a parsed document, etc.
/// </summary>
public sealed class StaticLeakItem
{
    public readonly int Id;
    public readonly string Description;
    public readonly DateTime CreatedAt;
    public readonly byte[] Data; // large buffer -> lands on the Large Object Heap

    public StaticLeakItem(int id, int dataSizeBytes)
    {
        Id = id;
        Data = new byte[dataSizeBytes];

        // Touch every page so the OS actually commits physical memory.
        // Without this, the managed heap grows but the working set does not.
        Data.AsSpan().Fill(0x5A);

        Description = $"Item #{id} @ {DateTime.UtcNow:O}";
        CreatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Scenario 01: managed heap leak. A static field is a GC root, so every item
/// appended to <see cref="Cache"/> stays reachable for the whole process life.
/// Signature: `!dumpheap -stat` shows StaticLeakItem / byte[] dominating;
/// `!gcroot` ends at the static Cache.
/// </summary>
public static class Scenario01_StaticCollectionLeak
{
    // THE LEAK: a static field is a GC root.
    private static readonly List<StaticLeakItem> Cache = [];

    public static int Run(string[] args)
    {
        int dataSizeKb = DumpUtil.Arg(args, 0, 512); // per-item buffer size (KB)
        int intervalMs = DumpUtil.Arg(args, 1, 100); // allocation interval (ms)
        int maxItems   = DumpUtil.Arg(args, 2, 0);   // 0 = leak forever

        DumpUtil.PrintHeader("01", "managed heap leak (static collection)");
        Console.WriteLine($"  dataSizeKb : {dataSizeKb}");
        Console.WriteLine($"  intervalMs : {intervalMs}");
        Console.WriteLine($"  maxItems   : {(maxItems == 0 ? "unlimited" : maxItems.ToString())}");
        Console.WriteLine();
        Console.WriteLine("Allocating ... capture a full dump now (dotnet-dump / procdump / Task Manager).");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        int count = 0;
        long lastReport = 0;
        var sw = Stopwatch.StartNew();

        while (maxItems == 0 || count < maxItems)
        {
            Cache.Add(new StaticLeakItem(count++, dataSizeKb * 1024));

            if (count - lastReport >= 100)
            {
                lastReport = count;
                Console.WriteLine(
                    $"  items={count,7}  managedHeap={DumpUtil.Mb(GC.GetTotalMemory(false)),6:F1} MB  " +
                    $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1} MB  elapsed={sw.Elapsed.TotalSeconds,6:F1}s");
            }

            Thread.Sleep(intervalMs);
        }

        Console.WriteLine($"Stopped after {count} items. Press Enter to exit.");
        Console.ReadLine();
        return 0;
    }
}
