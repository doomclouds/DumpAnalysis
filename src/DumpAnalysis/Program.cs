namespace DumpAnalysis;

/// <summary>
/// Entry point for the DumpAnalysis scenario runner.
///
/// Usage:  dotnet run -c Release -- &lt;scenario&gt; [scenario args...]
///
/// The first argument selects a scenario (by id "01".."06" or an alias); the
/// rest are forwarded to that scenario's <c>Run</c> method. Run with no
/// arguments (or <c>--help</c>) to list the available scenarios.
/// </summary>
public static class Program
{
    private static readonly (string Id, string Alias, string Description, Func<string[], int> Run)[] Scenarios =
    {
        ("01", "static-leak", "managed heap leak via a static collection", Scenario01_StaticCollectionLeak.Run),
        ("02", "finalizer",   "finalizer backlog (fReachable queue)",       Scenario02_FinalizerBacklog.Run),
        ("03", "event-leak",  "event subscription leak (static publisher)", Scenario03_EventSubscriptionLeak.Run),
        ("04", "loh-frag",    "LOH fragmentation (Free holes)",             Scenario04_LohFragmentation.Run),
        ("05", "native-leak", "unmanaged memory leak (AllocHGlobal)",       Scenario05_NativeMemoryLeak.Run),
        ("06", "gchandle",    "GC handle leak (GCHandle never freed)",      Scenario06_GCHandleLeak.Run),
        ("07", "deadlock",    "thread deadlock (crossed lock ordering)",    Scenario07_Deadlock.Run),
        ("08", "stack-overflow", "stack overflow (unbounded recursion)",    Scenario08_StackOverflow.Run),
        ("09", "access-violation", "access violation (null pointer AV)",    Scenario09_AccessViolation.Run),
        ("10", "sync-over-async",  "thread-pool starvation (sync-over-async)", Scenario10_SyncOverAsync.Run),
        ("11", "timer-leak",       "Timer leak (System.Threading.Timer never disposed)", Scenario11_TimerLeak.Run),
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var key = args[0].ToLowerInvariant();
        var scenario = Scenarios.FirstOrDefault(s => s.Id == key || s.Alias == key);
        if (scenario.Run is null)
        {
            Console.Error.WriteLine($"Unknown scenario: '{args[0]}'");
            PrintUsage();
            return 2;
        }

        Console.WriteLine($">>> Running scenario {scenario.Id} ({scenario.Description})");
        return scenario.Run(args[1..]);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DumpAnalysis - classic .NET memory-problem scenarios for WinDbg MCP dump analysis");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet run -c Release -- <scenario> [args...]");
        Console.WriteLine();
        Console.WriteLine("Scenarios:");
        foreach (var (id, alias, description, _) in Scenarios)
            Console.WriteLine($"  {id} / {alias,-12} - {description}");
        Console.WriteLine();
        Console.WriteLine("Example:  dotnet run -c Release -- 01 512 100 0");
        Console.WriteLine("          dotnet run -c Release -- native-leak 4 400 0");
        Console.WriteLine();
        Console.WriteLine("While a scenario runs, capture a full dump and analyze it with WinDbg MCP:");
        Console.WriteLine("  dotnet-dump collect -p <PID> -o leak.dmp");
        Console.WriteLine("  see docs/WINDBG-MCP-GUIDE.md");
    }
}
