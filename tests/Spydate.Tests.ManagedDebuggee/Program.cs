using System.Runtime.CompilerServices;

namespace ManagedDebuggee;

/// <summary>
/// A deliberately dull managed target for the ManagedOverlay and managed-breakpoint tests.
///
/// <see cref="Spin"/> never returns — the test kills it — so a native pause always lands with managed
/// frames on the stack: Wait &lt;- Spin &lt;- Main. Each turn of the loop calls <see cref="Step"/>,
/// which is shaped to have interior IL offsets and is called over and over, so a breakpoint at an
/// interior offset of it fires repeatedly — which is how "hit it twice" is checked. <see cref="Cold"/>
/// is never called, so it is never JITted: the case a managed breakpoint must refuse and say why.
///
/// The static <see cref="Label"/> holds a known value for the static-read test.
/// </summary>
internal static class Program
{
    /// <summary>Read by the overlay's static-field path; its value is the assertion.</summary>
    public static string Label = "spydate-overlay";

    /// <summary>Bumped every loop, so a static int read has a plausibly non-zero value too.</summary>
    public static int Ticks;

    /// <summary>Accumulates <see cref="Step"/>'s result, so its statements are not optimised away.</summary>
    public static long Sum;

    /// <summary>Accumulates <see cref="LateJit"/>'s result, so its JIT is not elided.</summary>
    public static long LateSum;

    private static void Main()
    {
        Console.WriteLine($"managed debuggee up, pid {Environment.ProcessId}");
        Spin();
    }

    /// <summary>
    /// Cold at startup: never called until the loop has turned enough times, then called every turn.
    /// So it JITs on a late first call — the pending-breakpoint case — and is called again after, so a
    /// breakpoint planted once it has native code catches a subsequent call. Shaped like Step so a
    /// breakpoint has interior offsets to aim at.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int LateJit(int value)
    {
        int doubled = value * 2;
        int plusOne = doubled + 1;
        int mixed = plusOne ^ value;
        return mixed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Spin()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Ticks++;
            Sum += Step(Ticks);

            // LateJit stays cold for a few seconds, then is called every turn — so its first JIT is
            // late (long after a debugger has had time to see the CLR and set a breakpoint on it), and
            // it is called again after, so a breakpoint planted once it has native code catches a later
            // call. Wall-clock, not a tick count, so the cold window does not shrink on a slow machine.
            if (clock.Elapsed.TotalSeconds > 4)
            {
                LateSum += LateJit(Ticks);
            }

            Wait();
        }
    }

    /// <summary>
    /// Called every loop, with a few statements so there are interior IL offsets to aim a breakpoint
    /// at. Kept from the inliner so it really exists as its own JITted method with its own IL map.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Step(int value)
    {
        int doubled = value * 2;
        int plusOne = doubled + 1;
        int mixed = plusOne ^ value;
        return mixed;
    }

    /// <summary>
    /// Referenced so the runtime creates a method descriptor for it, but never invoked, so it is never
    /// JITted — the cold-method case a managed breakpoint must refuse: a real method with no native
    /// code yet. The delegate is what gives it a descriptor without a call; nothing ever runs it.
    /// </summary>
    public static readonly Func<int, int> ColdRef = Cold;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Cold(int value) => value * 7 + 3;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Wait() => Thread.Sleep(50);
}
