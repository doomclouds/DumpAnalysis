using System.Diagnostics;

namespace DumpAnalysis;

/// <summary>
/// A long-lived event publisher. Held by a static field, so it is a GC root for
/// its whole lifetime. Every subscriber that subscribes to its event is kept
/// alive by the publisher's delegate chain.
/// </summary>
public sealed class EventPublisher
{
    // Compiler-generated backing field: an EventHandler, which is a MulticastDelegate.
    public event EventHandler? DataReady;

    public void Raise() => DataReady?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A subscriber that attaches to the publisher's event and NEVER detaches.
/// Each instance carries a payload, so leaked subscribers pin their payload too.
/// </summary>
public sealed class EventSubscriber
{
    public readonly int Id;
    public readonly string Name;
    public readonly byte[] Payload;

    public EventSubscriber(int id)
    {
        Id = id;
        Payload = new byte[64 * 1024];
        Payload.AsSpan().Fill(0x23); // '#' - prove the buffer is committed
        Name = $"Subscriber #{id} @ {DateTime.UtcNow:O}";
    }

    public void OnDataReady(object? sender, EventArgs e) { }
}

/// <summary>
/// Scenario 03: event subscription leak. Subscribers `+=` onto a static
/// publisher's event and never unsubscribe, so the delegate chain keeps them
/// alive forever. Signature: `!dumpheap -stat` shows EventSubscriber AND
/// System.EventHandler counts growing together; the publisher's combined
/// delegate has `_invocationCount` == subscriber count.
/// </summary>
public static class Scenario03_EventSubscriptionLeak
{
    // THE LEAK: static publisher = GC root. Subscribers never unsubscribe.
    private static readonly EventPublisher Publisher = new();

    public static int Run(string[] args)
    {
        int intervalMs = DumpUtil.Arg(args, 0, 50); // subscribe interval (ms)
        int maxItems   = DumpUtil.Arg(args, 1, 0);  // 0 = run forever

        DumpUtil.PrintHeader("03", "event subscription leak");
        Console.WriteLine($"  intervalMs : {intervalMs}");
        Console.WriteLine($"  maxItems   : {(maxItems == 0 ? "unlimited" : maxItems.ToString())}");
        Console.WriteLine();
        Console.WriteLine("Subscribing subscribers to a static publisher WITHOUT unsubscribing ...");
        Console.WriteLine("Capture a full dump now (dotnet-dump / procdump / Task Manager).");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        int count = 0;
        var sw = Stopwatch.StartNew();

        while (maxItems == 0 || count < maxItems)
        {
            var subscriber = new EventSubscriber(count);
            Publisher.DataReady += subscriber.OnDataReady; // SUBSCRIBE, never unsubscribe
            count++;

            // Every 100 subscribers, raise the event once (proves the handlers
            // are really in the invocation list) and force a gen2 GC so the
            // surviving subscribers get promoted and the leak is easy to see.
            if (count % 100 == 0)
            {
                Publisher.Raise();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            }

            if (count % 200 == 0)
            {
                Console.WriteLine(
                    $"  subscribers={count,7}  heap={DumpUtil.Mb(GC.GetTotalMemory(false)),6:F1} MB  " +
                    $"ws={DumpUtil.Mb(Environment.WorkingSet),6:F1} MB  elapsed={sw.Elapsed.TotalSeconds,6:F1}s");
            }

            Thread.Sleep(intervalMs);
        }

        Console.WriteLine($"Stopped after {count} subscribers. Press Enter to exit.");
        Console.ReadLine();
        return 0;
    }
}
