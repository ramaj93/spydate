using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Spydate.Debugger.Managed;
using Xunit;

namespace Spydate.Tests;

/// <summary>
/// A stopped frame's values as a tree, and the threads of a stopped process.
///
/// Against a program written for the purpose, so every kind of value the tree has to read is there
/// on purpose: an instance method with named parameters, a base class, an auto-property, enums plain
/// and [Flags], a boxed number, a nullable, a pointer, an array, a nested object, and a second thread
/// with a name. Each of those read wrongly at some point, and the wrong readings — "arg 1",
/// "{ value__ = 2 }", "not available" — were all things a reader could mistake for the truth.
/// </summary>
[Collection(Debugging.Name)]
public class ManagedVariablesTests
{
    private const string NoCompiler = "no .NET Framework compiler on this machine";

    private const string Source = """
        using System;
        using System.Threading;

        enum Mood { Calm = 1, Angry = 2 }

        [Flags]
        enum Access { None = 0, Read = 1, Write = 2 }

        class Point { public int X = 3; public int Y = 4; }

        class Base { protected string baseName = "base"; }

        class Holder : Base
        {
            public int Count = 7;
            public string Label = "hello";
            public int[] Numbers = { 10, 20, 30 };
            public Point Origin = new Point();
            public Mood Mood = Mood.Angry;
            public Access Rights = Access.Read | Access.Write;
            public object Boxed = 42;
            public int? Maybe = 5;
            public Holder Next;
            public IntPtr Handle = IntPtr.Zero;
            public string Name { get; set; }

            public Holder() { Name = "auto"; Next = null; }

            public void Look(int factor, string why)
            {
                int doubled = factor * 2;
                Console.WriteLine(doubled + why);
                Console.WriteLine(doubled);
            }
        }

        static class Program
        {
            static void Box(object boxed)
            {
                Console.WriteLine(boxed);
            }

            static void Main()
            {
                var sleeper = new Thread(() => Thread.Sleep(60000));
                sleeper.IsBackground = true;
                sleeper.Name = "sleeper";
                sleeper.Start();
                Thread.Sleep(300);
                Box(42);
                new Holder().Look(3, "why");
            }
        }
        """;

    private static string? Program => FrameworkFixture.Build("Shapes", Source);

    // ------------------------------------------------------------------
    // Names
    // ------------------------------------------------------------------

    [SkippableFact]
    public void AnInstanceMethodsFirstArgumentIsThisAndTheRestHaveTheirNames()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var roots = session.Variables();

        // "arg 0, arg 1, arg 2" was what this said. Slot zero of an instance method is `this`, which has
        // no row in the parameter table, so the names have to be counted one slot along from it.
        Assert.Equal(["this", "factor", "why"], roots.Where(r => r.Path.Argument).Select(r => r.Name));

        var self = roots[0];
        Assert.Equal("Holder", self.Type);
        Assert.Equal("{Holder}", self.Value);
        Assert.True(self.Expandable);
        Assert.Equal("3", roots[1].Value);
        Assert.Equal("\"why\"", roots[2].Value);
    }

    [SkippableFact]
    public void ALocalHasTheTypeItWasDeclaredWith()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var local = Assert.Single(session.Variables(), r => !r.Path.Argument);

        Assert.Equal("int", local.Type);
    }

    [SkippableFact]
    public void TheDecompilerNamesALocalTheRuntimeOnlyNumbers()
    {
        Skip.If(Program is null, NoCompiler);

        // No process: the names come from decompiling the method, which is the text the reader sees
        // beside the Locals pane. The runtime can only say V_0.
        using var assembly = Spydate.Decompiler.Managed.ManagedAssembly.Load(Program!);
        var handle = MetadataTokens.MethodDefinitionHandle((int)(Token("Holder", "Look") & 0x00FFFFFF));

        var names = assembly.Decompiler.LocalNamesFor(handle);

        Assert.True(names.TryGetValue(0, out string? name), "slot 0 has no name: " + string.Join(", ", names));
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain("V_", name, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // What an object opens to
    // ------------------------------------------------------------------

    [SkippableFact]
    public void AnObjectOpensToItsFieldsIncludingTheOnesItsBaseClassDeclared()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var fields = Fields(session);

        // Declared by Base. Reading only the most derived class's fields is what the class alone
        // allows; the base chain comes from the value's exact type.
        Assert.Contains(fields, f => f.Name == "baseName" && f.Value == "\"base\"");
        Assert.Contains(fields, f => f.Name == "Count" && f.Value == "7" && f.Type == "int");

        // An auto-property's backing field, under the name the property was written with.
        Assert.Contains(fields, f => f.Name == "Name" && f.Value == "\"auto\"");
        Assert.DoesNotContain(fields, f => f.Name.Contains("k__BackingField", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void ANullHasTheTypeItWasDeclaredWith()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var next = Assert.Single(Fields(session), f => f.Name == "Next");

        // A null has no runtime type to ask, so this comes from the field's signature. Before, every
        // null field was "object".
        Assert.Equal(ManagedValueKind.Null, next.Kind);
        Assert.Equal("null", next.Value);
        Assert.Equal("Holder", next.Type);
        Assert.False(next.Expandable);
    }

    [SkippableFact]
    public void AnEnumReadsAsItsMemberAndAFlagsEnumAsTheMembersItCombines()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var fields = Fields(session);

        var mood = Assert.Single(fields, f => f.Name == "Mood");
        Assert.Equal("Angry", mood.Value);
        Assert.Equal("Mood", mood.Type);
        Assert.Equal(ManagedValueKind.Enum, mood.Kind);

        Assert.Equal("Read | Write", Assert.Single(fields, f => f.Name == "Rights").Value);
    }

    [SkippableFact]
    public void ABoxedNumberReadsAsTheNumberAndSaysWhatIsInTheBox()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var boxed = Assert.Single(Fields(session), f => f.Name == "Boxed");

        // "not available", until the interface id for a box was asked of a real one: the constant was
        // ICorDebugEval's, so nothing was ever unboxed.
        Assert.Equal("42", boxed.Value);
        Assert.Equal("object {int}", boxed.Type);
    }

    [SkippableFact]
    public void ANullableReadsAsWhatItHoldsAndAPointerIsWrittenAsOne()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var fields = Fields(session);

        var maybe = Assert.Single(fields, f => f.Name == "Maybe");
        Assert.Equal("5", maybe.Value);
        Assert.Equal("int?", maybe.Type);

        var handle = Assert.Single(fields, f => f.Name == "Handle");
        Assert.Equal("0x0000000000000000", handle.Value);
        Assert.Equal("System.IntPtr", handle.Type);
    }

    [SkippableFact]
    public void AnArrayOpensToItsElementsAndAnObjectInsideOneOpensToo()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var fields = Fields(session);

        var numbers = Assert.Single(fields, f => f.Name == "Numbers");
        Assert.Equal("{int[3]}", numbers.Value);
        Assert.Equal(["10", "20", "30"], session.Children(numbers.Path).Select(e => e.Value));
        Assert.Equal(["[0]", "[1]", "[2]"], session.Children(numbers.Path).Select(e => e.Name));

        var origin = Assert.Single(fields, f => f.Name == "Origin");
        Assert.Equal("{Point}", origin.Value);
        Assert.Contains(session.Children(origin.Path), f => f.Name == "Y" && f.Value == "4");
    }

    [SkippableFact]
    public void APathShownBeforeAStepStillOpensAfterIt()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var origin = Assert.Single(Fields(session), f => f.Name == "Origin");

        Assert.Null(session.Step(into: false));
        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(20)), "the step never landed");

        // The runtime's value objects are gone after a step. The path is walked again from the new
        // frame, which is what keeps an open row open.
        Assert.Contains(session.Children(origin.Path), f => f.Name == "X" && f.Value == "3");
    }

    [SkippableFact]
    public void ABoxAnswersToTheInterfaceIdTheDebuggerUnboxesThrough()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Program", "Box");

        var supported = session.ProbeArgumentInterfaces(0, CorDebugIidTests.Supported).ToList();
        int split = supported.IndexOf("--- dereferenced ---");
        Assert.True(split > 0, "the argument could not be dereferenced: " + string.Join(", ", supported));

        var box = supported.Skip(split + 1).ToList();

        // Written out here, independently of the constant the code uses.
        Assert.Contains("CC7BCAFC-8A68-11D2-983C-0000F808342D", box);
        Assert.DoesNotContain("CC7BCAF6-8A68-11D2-983C-0000F808342D", box);
    }

    // ------------------------------------------------------------------
    // Threads
    // ------------------------------------------------------------------

    [SkippableFact]
    public void EveryThreadIsListedWithWhereItIsAndTheOneThatStoppedMarked()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var threads = session.Threads();

        var main = Assert.Single(threads, t => t.IsStopped);
        Assert.True(main.IsSelected);
        Assert.Equal(1, main.ManagedId);
        Assert.Equal("Main Thread", main.Category);
        Assert.Contains("Shapes.exe!Holder.Look(int, string)", main.Location, StringComparison.Ordinal);

        var sleeper = Assert.Single(threads, t => t.Name == "sleeper");
        Assert.False(sleeper.IsSelected);
        Assert.Contains("Background", sleeper.State, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep", sleeper.Location, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void LookingAtAnotherThreadMovesTheValuesAndThePlaceToIt()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var sleeper = session.Threads().Single(t => t.Name == "sleeper");
        Assert.Null(session.SelectThread(sleeper.Id));

        Assert.Equal("mscorlib.dll", session.StoppedAt?.Module);
        Assert.True(session.Threads().Single(t => t.Name == "sleeper").IsSelected);

        // Thread.Sleep(int millisecondsTimeout) is static: no `this`, and its parameter by its name.
        var there = session.Variables();
        Assert.DoesNotContain(there, v => v.Name == "this");
        Assert.Contains(there, v => v.Name == "millisecondsTimeout");

        var main = session.Threads().Single(t => t.IsStopped);
        Assert.Null(session.SelectThread(main.Id));

        Assert.Equal("Shapes.exe", session.StoppedAt?.Module);
        Assert.Contains(session.Variables(), v => v.Name == "this");
    }

    // ------------------------------------------------------------------
    // CoreCLR
    // ------------------------------------------------------------------

    [SkippableFact]
    public void OnDotNetThisIsTheObjectAndOpensToItsFields()
    {
        Skip.If(Probe.Target is null, "this build produced no .NET program");

        string target = Probe.Target!;
        uint token = TokenIn(Path.ChangeExtension(target, ".dll"), "McpOptions", "Allows");

        using var session = new ManagedDebugSession { ShowConsole = false };
        Assert.Null(session.Start(target, arguments: @"C:\Windows\System32\where.exe", holdAtStart: true));
        Assert.Null(session.SetBreakpoint("spydate-mcp.dll", token));
        session.Continue();
        Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(40)), string.Join("\n", session.Recent.TakeLast(8)));

        var roots = session.Variables();

        var self = Assert.Single(roots, r => r.Name == "this");
        Assert.Equal("Spydate.Mcp.McpOptions", self.Type);
        Assert.Contains(roots, r => r.Name == "path" && r.Value.Contains("where.exe", StringComparison.Ordinal));
        Assert.Contains(session.Children(self.Path), f => f.Name == "MaxFunctions" && f.Value == "20000");
    }

    // ------------------------------------------------------------------

    /// <summary>A session stopped at the first instruction of a method in the fixture.</summary>
    private static ManagedDebugSession StopIn(string type, string method)
    {
        var session = new ManagedDebugSession { ShowConsole = false };
        try
        {
            Assert.Null(session.Start(Program!, holdAtStart: true, timeout: TimeSpan.FromSeconds(30)));
            Assert.Null(session.SetBreakpoint("Shapes.exe", Token(type, method)));
            session.Continue();
            Assert.True(
                session.WaitUntilStopped(TimeSpan.FromSeconds(30)),
                "it never stopped:\n" + string.Join("\n", session.Recent));
            return session;
        }
        catch
        {
            // A failed start still started something, and a test that throws here would otherwise
            // leave it running behind the suite.
            session.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<ManagedVariable> Fields(ManagedDebugSession session)
        => session.Children(session.Variables().Single(r => r.Name == "this").Path);

    private static uint Token(string type, string method) => TokenIn(Program!, type, method);

    private static uint TokenIn(string file, string type, string method)
    {
        using var pe = new PEReader(File.OpenRead(file));
        var metadata = pe.GetMetadataReader();
        var handle = metadata.MethodDefinitions.First(h =>
        {
            var definition = metadata.GetMethodDefinition(h);
            return metadata.GetString(definition.Name) == method
                   && metadata.GetString(metadata.GetTypeDefinition(definition.GetDeclaringType()).Name) == type;
        });

        return (uint)MetadataTokens.GetToken(handle);
    }
}
