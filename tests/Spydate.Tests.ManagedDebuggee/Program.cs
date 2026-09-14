using System.Runtime.CompilerServices;

namespace ManagedDebuggee;

/// <summary>
/// A deliberately dull managed target for the ManagedOverlay tests.
///
/// It never returns from <see cref="Spin"/> — the test kills it — so a native pause always lands with
/// managed frames on the stack: Wait &lt;- Spin &lt;- Main. The static <see cref="Label"/> holds a
/// known value so a static field read straight out of memory has something to assert.
/// </summary>
internal static class Program
{
    /// <summary>Read by the overlay's static-field path; its value is the assertion.</summary>
    public static string Label = "spydate-overlay";

    /// <summary>Bumped every loop, so a static int read has a plausibly non-zero value too.</summary>
    public static int Ticks;

    private static void Main()
    {
        Console.WriteLine($"managed debuggee up, pid {Environment.ProcessId}");
        Spin();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Spin()
    {
        while (true)
        {
            Ticks++;
            Wait();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Wait() => Thread.Sleep(50);
}
