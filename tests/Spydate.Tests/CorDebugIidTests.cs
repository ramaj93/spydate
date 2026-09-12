using System.Runtime.InteropServices;
using Spydate.Debugger.Managed;

namespace Spydate.Tests;

/// <summary>
/// Pins the interface ids the managed debugger depends on to what the runtime actually answers to.
///
/// Three were written from memory and three were wrong, and a wrong one fails in the worst way
/// available: QueryInterface returns E_NOINTERFACE for an interface the object really does
/// implement, the wrapper comes back null, and the caller reports "not available" — which is
/// indistinguishable from a value the runtime genuinely would not produce. Nothing crashes and
/// nothing says why.
///
/// So this asks a live value what it supports rather than trusting the constants, and writes the
/// expected ids out independently of the ones the code uses. A drift in either shows up here as a
/// failing test rather than as a debugger that has quietly stopped reading strings.
/// </summary>
[Collection(Debugging.Name)]
public class CorDebugIidTests
{
    private const string Value = "CC7BCAF7-8A68-11D2-983C-0000F808342D";
    private const string GenericValue = "CC7BCAF8-8A68-11D2-983C-0000F808342D";
    private const string ReferenceValue = "CC7BCAF9-8A68-11D2-983C-0000F808342D";
    private const string StringValue = "CC7BCAFD-8A68-11D2-983C-0000F808342D";

    private const string Dereferenced = "--- dereferenced ---";

    /// <summary>A session whose debuggee gets no console window. See the debugger tests for why.</summary>
    private static ManagedDebugSession Headless() => new() { ShowConsole = false };

    [Fact]
    public void AStringArgumentAnswersToTheIdsThisReliesOn()
    {
        if (Probe.Target is not { } target)
        {
            return;
        }

        uint token = Probe.Token("Spydate.Core.PE.PeImage", "Load");

        using var session = Headless();
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));
        Assert.Null(session.SetBreakpoint("Spydate.Core.dll", token));
        session.Continue();
        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)));

        var supported = session.ProbeArgumentInterfaces(0, Supported).ToList();
        int split = supported.IndexOf(Dereferenced);
        Assert.True(split > 0, "the argument could not be dereferenced: " + string.Join(", ", supported));

        var reference = supported.Take(split).ToList();
        var pointed = supported.Skip(split + 1).ToList();

        // The argument slot holds a reference, and is a value and a reference and nothing else here.
        Assert.Contains(Value, reference);
        Assert.Contains(ReferenceValue, reference);

        // What it points at is the string object, which is where the text is actually read from.
        Assert.Contains(StringValue, pointed);
        Assert.Contains(GenericValue, pointed);
    }

    /// <summary>Every id in the family this pointer answers to. They occupy one contiguous range.</summary>
    internal static IReadOnlyList<string> Supported(IntPtr unknown)
    {
        var found = new List<string>();
        for (int low = 0; low <= 0xFF; low++)
        {
            Guid iid = new($"CC7BCA{low:X2}-8A68-11d2-983C-0000F808342D");
            if (Marshal.QueryInterface(unknown, in iid, out IntPtr answer) == 0 && answer != IntPtr.Zero)
            {
                found.Add(iid.ToString().ToUpperInvariant());
                Marshal.Release(answer);
            }
        }

        return found;
    }
}

/// <summary>What the probe needs to launch something worth asking.</summary>
internal static class Probe
{
    internal static string? Target
    {
        get
        {
            string guess = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "Spydate.Mcp", "bin", "Debug", "net10.0", "spydate-mcp.exe"));
            return File.Exists(guess) ? guess : null;
        }
    }

    internal static uint Token(string type, string method)
    {
        using var assembly = Spydate.Decompiler.Managed.ManagedAssembly.Load(typeof(Spydate.Core.PE.PeImage).Assembly.Location);
        var member = assembly.Namespaces
            .SelectMany(n => n.Types)
            .First(t => t.FullName == type)
            .Members.First(m => m.Name == method);

        return (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(member.Handle);
    }
}
