namespace Spydate.Tests;

/// <summary>
/// The tests that run programs, kept out of each other's way.
///
/// xunit parallelises test classes, and these are not classes that can be parallelised: each one
/// launches a process and watches it, several wait tens of seconds for something to happen, and a
/// machine running four debuggers at once is a machine where those waits start expiring for reasons
/// that have nothing to do with the code. It showed up exactly that way — a test that passes alone
/// and fails in a full run, which is the shape of a flake that gets explained away rather than fixed.
///
/// One collection means one at a time. It costs a few seconds of wall clock and buys a suite whose
/// failures mean something.
/// </summary>
[CollectionDefinition(Debugging.Name, DisableParallelization = true)]
public sealed class DebuggingCollection
{
}

/// <summary>The name, in one place, so a class joining the collection cannot mistype it.</summary>
public static class Debugging
{
    public const string Name = "runs a program";
}
