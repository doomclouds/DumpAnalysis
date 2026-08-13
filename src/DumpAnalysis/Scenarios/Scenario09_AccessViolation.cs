using System.Runtime.InteropServices;

namespace DumpAnalysis;

/// <summary>
/// Scenario 09: access violation (native AV). The classic interop failure: a
/// P/Invoke call site passes a bad pointer to native code. We pass a NON-NULL,
/// unmapped destination to RtlZeroMemory, so the write faults inside native and
/// raises STATUS_ACCESS_VIOLATION (0xC0000005). (A null pointer would be
/// translated into a managed NullReferenceException by the JIT, which is not a
/// true AV.) In the dump, !analyze -v shows the faulting address and the native
/// stack (kernel32!RtlZeroMemory) where the fault happened.
/// </summary>
public static class Scenario09_AccessViolation
{
    // Stand-in for "native code the app calls". In real life this is a P/Invoke
    // or a native library callback writing through a pointer it was given.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void RtlZeroMemory(IntPtr destination, UIntPtr length);

    public static int Run(string[] args)
    {
        DumpUtil.PrintHeader("09", "access violation (native AV)");
        Console.WriteLine("Reproducing a native access violation ...");
        Console.WriteLine("Native RtlZeroMemory writes through a bad (unmapped) pointer.");
        Console.WriteLine("The process will crash with STATUS_ACCESS_VIOLATION.\n");

        // The buggy pattern: a P/Invoke call passes a BAD destination pointer to
        // native code. We deliberately use a NON-NULL, unmapped address: a null
        // dereference would be turned into a managed NullReferenceException by
        // the JIT, but a non-null unmapped address goes straight to the hardware
        // and raises a real STATUS_ACCESS_VIOLATION (0xC0000005) inside native.
        IntPtr badPtr = new IntPtr(0x00000001_00000000L); // 4 GB - non-null, not mapped
        Console.WriteLine($"  [info] calling RtlZeroMemory(dest=0x{badPtr.ToInt64():X}, 4 bytes) ...");
        RtlZeroMemory(badPtr, (UIntPtr)4); // <-- access violation here

        // Never reached.
        Console.WriteLine("  (should never print)");
        return 0;
    }
}
