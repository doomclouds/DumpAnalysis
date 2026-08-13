namespace DumpAnalysis;

/// <summary>
/// Scenario 08: unbounded recursion -> StackOverflowException. This is a
/// **native** stack exhaustion: the runtime aborts the process immediately
/// (no catchable managed exception, no finalizers, no second chance). It is the
/// most brutal of the crash scenarios. Analysis relies on the native stack
/// (k / !analyze -v) rather than SOS heap commands.
/// </summary>
public static class Scenario08_StackOverflow
{
    // Grows the stack frame a bit so we hit the limit faster (and so the frames
    // are clearly recursive in the native stack).
    private static readonly byte[] Pad = new byte[1024];

    public static int Run(string[] args)
    {
        DumpUtil.PrintHeader("08", "stack overflow (unbounded recursion)");
        Console.WriteLine("Recursing forever ... the process will crash with");
        Console.WriteLine("StackOverflowException (fatal, not catchable).\n");

        // NOTE: in .NET Core the StackOverflowException is NOT catchable - the
        // runtime aborts the process immediately (native stack exhaustion).
        Recurse(0);
        return 0;
    }

    private static void Recurse(int depth)
    {
        Pad[0] = (byte)depth; // keep the buffer referenced so it isn't optimized away
        if (depth % 100_000 == 0)
            Console.WriteLine($"  depth={depth} (recursing)");
        Recurse(depth + 1);
    }
}
