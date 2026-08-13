using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DumpAnalysis;

/// <summary>
/// A managed object that "interop" code keeps alive through a strong GCHandle.
/// The bug: the handle is allocated but never freed, so the GC handle table
/// keeps a strong root on it for the whole process lifetime.
/// </summary>
public sealed class HandleLeakedItem
{
    public readonly int Id;
    public readonly byte[] Payload; // kept alive together with the object

    public HandleLeakedItem(int id)
    {
        Id = id;
        Payload = new byte[64 * 1024]; // below the 85KB LOH threshold -> stays in gen0/gen1/gen2
        Payload.AsSpan().Fill(0x4D); // 'M' - prove the buffer is committed
    }
}

/// <summary>
/// Scenario 06: GC handle leak. GCHandle.Alloc registers a strong handle in the
/// GC handle table; never calling Free() keeps the target alive forever.
/// Signature: `!gchandles -stat` shows the Strong-handle count == object count;
/// `!gcroot` shows a handle pointing DIRECTLY at the object (no intermediate).
/// </summary>
public static class Scenario06_GCHandleLeak
{
    public static int Run(string[] args)
    {
        int intervalMs = DumpUtil.Arg(args, 0, 50);  // handle allocation interval (ms)
        int maxHandles = DumpUtil.Arg(args, 1, 0);   // 0 = run forever

        DumpUtil.PrintHeader("06", "GC handle leak (GCHandle never freed)");
        Console.WriteLine($"  intervalMs : {intervalMs}");
        Console.WriteLine($"  maxHandles : {(maxHandles == 0 ? "unlimited" : maxHandles.ToString())}");
        Console.WriteLine();
        Console.WriteLine("Allocating strong GCHandles WITHOUT freeing ...");
        Console.WriteLine("Capture a full dump now (dotnet-dump / procdump / Task Manager).");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        // The "interop registry": keeps the handles referenced, as a real app
        // would. The BUG is that GCHandle.Free() is never called.
        var handles = new List<GCHandle>();

        int count = 0;
        var sw = Stopwatch.StartNew();

        while (maxHandles == 0 || count < maxHandles)
        {
            // BUG: Alloc a strong handle and never Free it.
            handles.Add(GCHandle.Alloc(new HandleLeakedItem(count), GCHandleType.Normal));
            count++;

            if (count % 200 == 0)
            {
                Console.WriteLine(
                    $"  handles={count,6}  managedHeap={DumpUtil.Mb(GC.GetTotalMemory(false)),6:F1}MB  " +
                    $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1}MB  elapsed={sw.Elapsed.TotalSeconds,6:F1}s");
            }

            Thread.Sleep(intervalMs);
        }

        Console.WriteLine($"Stopped after {count} handles. Press Enter to exit.");
        Console.ReadLine();
        return 0;
    }
}
