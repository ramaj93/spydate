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

        class Box<T>
        {
            public T Item;
            public T Current { get { return Item; } }
        }

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
            public bool Ready = false;
            public Box<int> Wrapped = new Box<int>();
            public string Name { get; set; }

            public Holder() { Name = "auto"; Next = null; Wrapped.Item = 9; }

            // A plain getter, an expression getter, an enum getter, a static getter, and one that
            // throws — the range the property evaluator has to handle.
            public int Doubled { get { return Count * 2; } }
            public string Greeting { get { return "hi " + Label; } }
            public Mood CurrentMood { get { return Mood; } }
            public static int Answer { get { return 42; } }
            public int Bang { get { throw new InvalidOperationException("no"); } }
            public Point Corner { get { return Origin; } }

            // Statics belong to the type rather than to any object.
            public static int Total = 99;
            public static string Where { get { return "static"; } }

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

        // An auto-property shows as a property, under the name it was written with, and its hidden
        // backing field is not listed separately.
        Assert.Contains(fields, f => f.Name == "Name" && f.Getter is not null);
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

        // MaxFunctions is an auto-property, so it opens as a property and its value comes from
        // running the getter — the same evaluation path as .NET Framework, on CoreCLR.
        var maxFunctions = Assert.Single(session.Children(self.Path), f => f.Name == "MaxFunctions");
        Assert.NotNull(maxFunctions.Getter);
        Assert.Equal("20000", session.EvaluateProperty(maxFunctions.Path, maxFunctions.Getter!, maxFunctions.Name, maxFunctions.Type)?.Value);
    }

    // ------------------------------------------------------------------
    // Call stack
    // ------------------------------------------------------------------

    [SkippableFact]
    public void TheCallStackRunsFromTheMethodThatStoppedToItsCallers()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var frames = session.Frames();

        // Look was called from Main. Innermost first.
        Assert.True(frames.Count >= 2, "only " + frames.Count + " frames");
        Assert.Contains("Holder.Look", frames[0].Display, StringComparison.Ordinal);
        Assert.True(frames[0].IsCurrent);
        Assert.Contains(frames, f => f.Display.Contains("Program.Main", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void PickingACallerShowsItsLocalsAndMovesTheArrow()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var main = session.Frames().First(f => f.Display.Contains("Program.Main", StringComparison.Ordinal));
        Assert.Null(session.SelectFrame(main.Index));

        // The arrow is in Main now, and the locals are Main's — the two Thread variables it holds,
        // not Look's factor/why.
        Assert.Equal(main.MethodToken, session.StoppedAt?.MethodToken);
        var locals = session.Variables();
        Assert.DoesNotContain(locals, v => v.Name == "factor");
        Assert.Contains(locals, v => v.Type.Contains("Thread", StringComparison.Ordinal));

        // And the current marker moved with it.
        Assert.True(session.Frames().Single(f => f.Index == main.Index).IsCurrent);
    }

    // ------------------------------------------------------------------
    // Properties, by running their getters
    // ------------------------------------------------------------------

    [SkippableFact]
    public void APropertyIsListedWithAGetterAndNoValueUntilItIsRun()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var doubled = Assert.Single(Fields(session), f => f.Name == "Doubled");

        Assert.NotNull(doubled.Getter);
        Assert.Equal(ManagedValueKind.Property, doubled.Kind);
        Assert.Equal("…", doubled.Value);

        // No plain field row for an auto-property's backing store — the property row stands for it.
        Assert.DoesNotContain(Fields(session), f => f.Name.Contains("k__BackingField", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void RunningAGetterGivesItsValue()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var doubled = Assert.Single(Fields(session), f => f.Name == "Doubled");
        var value = session.EvaluateProperty(doubled.Path, doubled.Getter!, doubled.Name, doubled.Type);

        // Count is 7, so Doubled runs to 14 — a value only a getter, not a field read, could give.
        Assert.Equal("14", value?.Value);
        Assert.Equal(ManagedValueKind.Number, value?.Kind);
    }

    [SkippableFact]
    public void AnAutoPropertyRunsLikeAnyOther()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var name = Assert.Single(Fields(session), f => f.Name == "Name");
        Assert.Equal("\"auto\"", session.EvaluateProperty(name.Path, name.Getter!, name.Name, name.Type)?.Value);
    }

    [SkippableFact]
    public void AGetterThatThrowsSaysSoRatherThanAValue()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var bang = Assert.Single(Fields(session), f => f.Name == "Bang");
        var value = session.EvaluateProperty(bang.Path, bang.Getter!, bang.Name, bang.Type);

        Assert.Contains("threw", value?.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", value?.Value ?? string.Empty, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void RunningAGetterLeavesTheProgramWhereItWas()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");
        var where = session.StoppedAt;

        var doubled = Assert.Single(Fields(session), f => f.Name == "Doubled");
        _ = session.EvaluateProperty(doubled.Path, doubled.Getter!, doubled.Name, doubled.Type);

        // The evaluation ran the process; it must have come back to rest exactly where it was, with
        // its locals still readable, or stepping through a program by reading its properties would
        // move it.
        Assert.Equal(where?.MethodToken, session.StoppedAt?.MethodToken);
        Assert.Equal(where?.Offset, session.StoppedAt?.Offset);
        Assert.Contains(session.Variables(), v => v.Name == "this");
    }

    // ------------------------------------------------------------------
    // Opening what a getter returned, generic types, statics, writing
    // ------------------------------------------------------------------

    [SkippableFact]
    public void AnObjectAGetterReturnedCanBeOpened()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var corner = Assert.Single(Fields(session), f => f.Name == "Corner");
        var value = session.EvaluateProperty(corner.Path, corner.Getter!, corner.Name, corner.Type);

        // An evaluated value is reachable from no frame, so opening it at all means the session kept
        // a handle on it — and the path now names that handle rather than a slot.
        Assert.NotNull(value);
        Assert.True(value!.Expandable, "an object a getter returned could not be opened");
        Assert.Equal(ManagedValueRoot.Evaluated, value.Path.Root);
        Assert.Contains(session.Children(value.Path), f => f.Name == "X" && f.Value == "3");
    }

    [SkippableFact]
    public void AGetterOnAGenericTypeRuns()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var wrapped = Assert.Single(Fields(session), f => f.Name == "Wrapped");
        var current = Assert.Single(session.Children(wrapped.Path), f => f.Name == "Current");

        // Box<int>.Current cannot be called without int: a method on a generic type takes its type
        // arguments alongside, which is what the parameterized call is for. It read "(cannot
        // evaluate)" until it did.
        Assert.Equal("9", session.EvaluateProperty(current.Path, current.Getter!, current.Name, current.Type)?.Value);
    }

    [SkippableFact]
    public void StaticsAreUnderOneRowOfTheirOwn()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var group = Assert.Single(Fields(session), f => f.Name == "Static members");
        Assert.True(group.Expandable);

        var statics = session.Children(group.Path);

        // A static is read through the class and a frame, not out of the object.
        Assert.Contains(statics, f => f.Name == "Total" && f.Value == "99");

        var where = Assert.Single(statics, f => f.Name == "Where");
        Assert.Equal("\"static\"", session.EvaluateProperty(where.Path, where.Getter!, where.Name, where.Type)?.Value);

        var answer = Assert.Single(statics, f => f.Name == "Answer");
        Assert.Equal("42", session.EvaluateProperty(answer.Path, answer.Getter!, answer.Name, answer.Type)?.Value);

        // And they are not mixed in with what the object itself holds.
        Assert.DoesNotContain(Fields(session), f => f.Name == "Total");
        Assert.DoesNotContain(Fields(session), f => f.Name == "Answer");
    }

    [SkippableFact]
    public void AValueCanBeWrittenBackIntoTheProgram()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        var count = Find(session, "Count");
        Assert.True(count.CanSet);
        Assert.Null(session.SetValue(count.Path, "11"));
        Assert.Equal("11", Find(session, "Count").Value);

        Assert.Null(session.SetValue(Find(session, "Ready").Path, "true"));
        Assert.Equal("true", Find(session, "Ready").Value);

        // An enum by the name of one of its members.
        Assert.Null(session.SetValue(Find(session, "Mood").Path, "Calm"));
        Assert.Equal("Calm", Find(session, "Mood").Value);

        // A string has to be made in the debuggee before a reference can point at it.
        Assert.Null(session.SetValue(Find(session, "Label").Path, "\"changed\""));
        Assert.Equal("\"changed\"", Find(session, "Label").Value);

        // And a reference can be emptied.
        Assert.Null(session.SetValue(Find(session, "Origin").Path, "null"));
        Assert.Equal("null", Find(session, "Origin").Value);
    }

    [SkippableFact]
    public void ALocalIsWrittenBackJustAsAFieldIs()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        // The one local of Look, slot 0 — the `int doubled` the decompiler names. A local is reached
        // straight off the frame with no field step, so its write is the path a field's never exercises.
        var local = Assert.Single(session.Variables(), r => !r.Path.Argument);
        Assert.True(local.CanSet, "a local int should be writable");
        Assert.Null(session.SetValue(local.Path, "42"));
        Assert.Equal("42", Assert.Single(session.Variables(), r => !r.Path.Argument).Value);

        // And an argument, slot 1 of the instance method: factor, which came in as 3.
        var factor = Assert.Single(session.Variables(), r => r.Name == "factor");
        Assert.True(factor.CanSet);
        Assert.Null(session.SetValue(factor.Path, "7"));
        Assert.Equal("7", Assert.Single(session.Variables(), r => r.Name == "factor").Value);
    }

    [SkippableFact]
    public void WhatCannotBeWrittenIsRefusedRatherThanGuessedAt()
    {
        Skip.If(Program is null, NoCompiler);
        using var session = StopIn("Holder", "Look");

        // A property is a method; writing one means calling a setter, which is not built.
        Assert.False(Find(session, "Doubled").CanSet);

        // And nonsense is refused with the value left as it was, rather than written as zero.
        Assert.NotNull(session.SetValue(Find(session, "Count").Path, "not a number"));
        Assert.Equal("7", Find(session, "Count").Value);
    }

    // ------------------------------------------------------------------
    // A patch reaching the running image
    // ------------------------------------------------------------------

    [SkippableFact]
    public void APatchIsWrittenIntoTheModuleBeforeItsCodeRuns()
    {
        Skip.If(Program is null, NoCompiler);

        // Rewrite the Doubled getter — "return Count * 2", which on Count == 7 is 14 — to "return 99".
        // ldc.i4.s 99 (1F 63) at the start, ret (2A) at the very end, nop between: a method has to end
        // on a terminator, so the ret goes last rather than leaving control to fall off into the nops.
        uint token = Token("Holder", "get_Doubled");
        var il = MethodIl(Program!, "Holder", "get_Doubled");
        var patched = new byte[il.Length];
        Array.Fill(patched, (byte)0x00);
        patched[0] = 0x1F;
        patched[1] = 0x63;
        patched[^1] = 0x2A;

        using var session = new ManagedDebugSession { ShowConsole = false };
        try
        {
            // Queued before anything runs. The getter is never called until the eval below, so it is
            // still uncompiled when the module loads and the write lands — which is exactly the window
            // a managed patch has to hit, since after the JIT the IL is no longer what runs.
            session.ApplyOnLoad(new ManagedPatch("Shapes.exe", token, 0, [.. patched], [.. il]));

            Assert.Null(session.Start(Program!, holdAtStart: true, timeout: TimeSpan.FromSeconds(30)));
            Assert.Null(session.SetBreakpoint("Shapes.exe", Token("Holder", "Look")));
            session.Continue();
            Assert.True(
                session.WaitUntilStopped(TimeSpan.FromSeconds(30)),
                "it never stopped:\n" + string.Join("\n", session.Recent));

            var doubled = Find(session, "Doubled");

            // Running the getter now compiles it from the patched IL. Unpatched this is 14; the patch
            // reached the image if and only if it is 99.
            Assert.Equal("99", session.EvaluateProperty(doubled.Path, doubled.Getter!, doubled.Name, doubled.Type)?.Value);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    [SkippableFact]
    public void APatchWhoseExpectedBytesAreWrongIsRefusedNotWritten()
    {
        Skip.If(Program is null, NoCompiler);

        uint token = Token("Holder", "get_Doubled");
        var wrong = new byte[3];
        Array.Fill(wrong, (byte)0xDD);   // nothing the getter's first IL bytes could be

        using var session = new ManagedDebugSession { ShowConsole = false };
        try
        {
            session.ApplyOnLoad(new ManagedPatch("Shapes.exe", token, 0, [0x1F, 0x63, 0x2A], [.. wrong]));

            Assert.Null(session.Start(Program!, holdAtStart: true, timeout: TimeSpan.FromSeconds(30)));
            Assert.Null(session.SetBreakpoint("Shapes.exe", Token("Holder", "Look")));
            session.Continue();
            Assert.True(session.WaitUntilStopped(TimeSpan.FromSeconds(30)), "it never stopped");

            // The check caught that the running bytes were not what the patch was cut against, said so,
            // and left the getter alone — so it still returns the real 14.
            Assert.Contains(session.Recent, r => r.Contains("not applied") && r.Contains("expected"));
            var doubled = Find(session, "Doubled");
            Assert.Equal("14", session.EvaluateProperty(doubled.Path, doubled.Getter!, doubled.Name, doubled.Type)?.Value);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    [SkippableFact]
    public void ABreakpointResolvesIntoAnotherAssemblyByName()
    {
        Skip.If(Program is null, NoCompiler);

        using var session = new ManagedDebugSession { ShowConsole = false };
        try
        {
            Assert.Null(session.Start(Program!, holdAtStart: true, timeout: TimeSpan.FromSeconds(30)));

            // System.Console lives in mscorlib, not in Shapes.exe — a method in an assembly other than
            // the one opened. At the initial hold no module is loaded yet, so it is recorded and waits;
            // as the run brings mscorlib in, it resolves against it and plants. The fixture then calls
            // Console.WriteLine, which is the breakpoint.
            string result = session.SetBreakpointByName("System.Console", "WriteLine");
            Assert.Contains("recorded", result, StringComparison.OrdinalIgnoreCase);

            session.Continue();
            Assert.True(
                session.WaitUntilStopped(TimeSpan.FromSeconds(30)),
                "it never stopped:\n" + string.Join("\n", session.Recent));

            // The only breakpoints set were the WriteLine overloads, so a stop in mscorlib is that hit.
            Assert.NotNull(session.StoppedAt);
            Assert.Contains("mscorlib", session.StoppedAt!.Module, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------

    /// <summary>One row of what <c>this</c> holds, by name.</summary>
    private static ManagedVariable Find(ManagedDebugSession session, string name)
        => Assert.Single(Fields(session), f => f.Name == name);

    /// <summary>The IL bytes of a method's body.</summary>
    private static byte[] MethodIl(string file, string type, string method)
    {
        using var pe = new PEReader(File.OpenRead(file));
        var metadata = pe.GetMetadataReader();
        var handle = metadata.MethodDefinitions.First(h =>
        {
            var definition = metadata.GetMethodDefinition(h);
            return metadata.GetString(definition.Name) == method
                   && metadata.GetString(metadata.GetTypeDefinition(definition.GetDeclaringType()).Name) == type;
        });

        int bodyRva = metadata.GetMethodDefinition(handle).RelativeVirtualAddress;
        return pe.GetMethodBody(bodyRva).GetILBytes()!;
    }

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
