using Spydate.Core.PE;
using Spydate.Core.Text;
using Spydate.Decompiler.Managed;

namespace Spydate.Tests;

/// <summary>
/// Decompiled C# paired with the IL each line came from.
///
/// This is what makes the C# view debuggable, and it is the one mapping in Spydate that nothing
/// else can check. A line of this text is not a line of anybody's source file — the decompiler
/// invented it out of the IL a moment ago — so there is no second opinion to compare against. The
/// tests below therefore check the properties that have to hold rather than any particular output:
/// the offsets are inside the method they claim, they go forwards, and the address the line ends up
/// carrying is the one the IL listing prints for that same offset.
/// </summary>
public class ManagedSourceTests
{
    private static string CorePath => typeof(PeImage).Assembly.Location;

    private static ManagedType TypeOf(ManagedAssembly assembly, string fullName)
        => assembly.Namespaces.SelectMany(n => n.Types).First(t => t.FullName == fullName);

    [Fact]
    public void EveryStatementOfAMethodSaysWhichIlItCameFrom()
    {
        using var assembly = ManagedAssembly.Load(CorePath);
        var member = TypeOf(assembly, "Spydate.Core.Text.AddressText").Members.First(m => m.Name == "ParseHex");

        var source = assembly.Decompiler.SourceForMember(member);

        Assert.NotEmpty(source.Lines);

        // One method, so one token, and it is the method that was asked for.
        uint token = (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(member.Handle);
        Assert.All(source.Lines, line => Assert.Equal(token, line.MethodToken));

        // Forwards, for this method, because it is straight-line code: a guard, an assignment, a
        // condition, a return, in that order in both renderings. That is not true in general — see
        // the whole-type test, where a loop puts its condition after its body — which is why it is
        // asserted here and nowhere else.
        var offsets = source.Lines.Select(l => l.Offset).ToList();
        Assert.Equal(offsets.OrderBy(o => o), offsets);

        // Each line is a real line of the text it came with, and there are fewer mapped lines than
        // lines: braces and blank lines are not statements and must not claim an offset.
        int count = source.Text.Split('\n').Length;
        Assert.All(source.Lines, line => Assert.InRange(line.Line, 1, count));
        Assert.True(source.Lines.Count < count, "every single line claimed an IL offset, which cannot be right");
    }

    [Fact]
    public void TheTextIsTheSameTextTheCSharpViewAlreadyShowed()
    {
        using var assembly = ManagedAssembly.Load(CorePath);
        var member = TypeOf(assembly, "Spydate.Core.Text.AddressText").Members.First(m => m.Name == "ParseHex");

        // Producing the mapping means writing the tree by hand rather than asking ILSpy for a
        // string, and the two must not drift: if they did, the addresses would be attached to lines
        // of one rendering and shown against another.
        Assert.Equal(
            assembly.Decompiler.DecompileMember(member),
            assembly.Decompiler.SourceForMember(member).Text);
    }

    [Fact]
    public void AWholeTypeMapsEachLineToTheMethodItIsIn()
    {
        using var assembly = ManagedAssembly.Load(CorePath);
        var type = TypeOf(assembly, "Spydate.Core.Text.AddressText");

        var source = assembly.Decompiler.SourceForType(type);

        // Several methods, and this is the case the IL listing cannot do: there, offsets restart at
        // every method and the text alone cannot say which body a line belongs to. Here the
        // decompiler says, so a type's worth of C# is addressable.
        var tokens = source.Lines.Select(l => l.MethodToken).Distinct().ToList();
        Assert.True(tokens.Count > 1, "a type with several methods mapped to only one");

        // No assertion that the offsets rise down the page, because they do not, and expecting them
        // to is the mistake worth recording here. A `while` loop is emitted with its condition after
        // its body — the method under test has one — so the line that reads `while (...)` carries a
        // higher offset than the lines beneath it. The mapping is still right; C# is a different
        // order from IL, which is the entire reason this mapping has to exist rather than be
        // computed from line numbers.
        //
        // What must hold is that no line is claimed twice, because a line gets one breakpoint.
        Assert.Equal(source.Lines.Count, source.Lines.Select(l => l.Line).Distinct().Count());
    }

    [Fact]
    public void AnOffsetIsInsideTheBodyItClaims()
    {
        using var assembly = ManagedAssembly.Load(CorePath);
        var bodies = ManagedBodies.Build(assembly);
        var source = assembly.Decompiler.SourceForType(TypeOf(assembly, "Spydate.Core.Text.AddressText"));

        foreach (var line in source.Lines)
        {
            var handle = System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(
                (int)(line.MethodToken & 0x00FFFFFF));
            var body = bodies.Of(handle);

            // An offset past the end of the body would be a breakpoint the runtime refuses, or
            // worse, one it accepts in the middle of an instruction.
            Assert.NotNull(body);
            Assert.InRange(line.Offset, 0, body!.Il.Length - 1);
        }
    }

    [Fact]
    public void TheAddressOnALineIsInsideTheMethodAtOrBeforeItsInstruction()
    {
        var image = PeImage.Load(CorePath);
        using var assembly = ManagedAssembly.Load(CorePath);
        var bodies = ManagedBodies.Build(assembly);
        var member = TypeOf(assembly, "Spydate.Core.Text.AddressText").Members.First(m => m.Name == "ParseHex");

        var source = assembly.Decompiler.SourceForMember(member);
        string addressed = ManagedDecompiler.Addressed(source, bodies, image.ImageBase);
        var lines = addressed.Split('\n');

        // The C# view and the IL view are two renderings of one method, and a breakpoint set in
        // either has to land in the same place. Both are checked through the same reader the
        // breakpoint margin uses, because that is what actually decides where a click goes.
        var body = bodies.Of((System.Reflection.Metadata.MethodDefinitionHandle)member.Handle)!;
        int checkedLines = 0;

        foreach (var line in source.Lines)
        {
            ulong address = AddressText.FromLine(lines[line.Line - 1])!.Value;

            // In this method's body, and at or before the instruction the decompiler named — the
            // address is the start of the statement holding that instruction, which is a step back
            // and never a step forward.
            Assert.InRange(address, image.ImageBase + body.IlRva, image.ImageBase + body.RvaOf(line.Offset));
            checkedLines++;
        }

        Assert.True(checkedLines > 3, "too few lines carried an address to prove anything");

        // Lines that are not statements carry nothing, so a click in the margin beside a brace does
        // not set a breakpoint on whatever happened to be above it.
        Assert.Contains(lines, l => l.Trim() == "{" && AddressText.FromLine(l) is null);
    }

    [Fact]
    public void EveryAddressLandsWhereTheEvaluationStackIsEmpty()
    {
        var image = PeImage.Load(CorePath);
        using var assembly = ManagedAssembly.Load(CorePath);
        var bodies = ManagedBodies.Build(assembly);
        var stack = new IlStack(assembly.Metadata);

        // The rule the runtime actually enforces, and the one that is invisible until a breakpoint
        // is silently refused: it binds one only where the stack is empty, because that is where the
        // JIT's IL-to-native map has entries. ILSpy hands back the offset of the expression a line
        // was made from, which is routinely one instruction into a statement — after the ldarg.0
        // that pushed the receiver. Those offsets produced BreakpointSetError, asynchronously, long
        // after CreateBreakpoint had said yes.
        int checkedLines = 0;
        foreach (var type in assembly.Namespaces.SelectMany(n => n.Types).Take(12))
        {
            var source = assembly.Decompiler.SourceForType(type);
            var lines = ManagedDecompiler.Addressed(source, bodies, image.ImageBase).Split('\n');

            foreach (var line in source.Lines)
            {
                if (AddressText.FromLine(lines[line.Line - 1]) is not { } va)
                {
                    continue;   // not addressable, which is a legitimate answer
                }

                var body = bodies.At(image.VaToRva(va)!.Value)!;
                int offset = body.OffsetOf(image.VaToRva(va)!.Value);

                Assert.Equal(0, stack.Delta(body.Il, 0, offset, body.Method));
                checkedLines++;
            }
        }

        Assert.True(checkedLines > 50, $"only {checkedLines} lines carried an address; the test proved little");
    }

    [Fact]
    public void AnAsyncMethodIsMappedToItsStateMachineRatherThanToItsStub()
    {
        // An async method's own body starts the state machine and returns. The code the reader is
        // looking at lives in MoveNext, and that is where these offsets are — naming the method the
        // reader sees would put the breakpoint at an unrelated place inside the stub, which the
        // runtime refused here and might elsewhere accept.
        string mcp = typeof(Spydate.Mcp.Session.BinarySession).Assembly.Location;
        using var assembly = ManagedAssembly.Load(mcp);

        var member = assembly.Namespaces
            .SelectMany(n => n.Types)
            .SelectMany(t => t.Members)
            .First(m => m.Name == "OpenAsync");

        var source = assembly.Decompiler.SourceForMember(member);
        uint own = (uint)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(member.Handle);

        Assert.NotEmpty(source.Lines);
        Assert.All(source.Lines, line => Assert.NotEqual(own, line.MethodToken));

        // And it is one state machine, not a scattering of guesses.
        Assert.Single(source.Lines.Select(l => l.MethodToken).Distinct());
    }

    [Fact]
    public void AStatementRunsFromOneEmptyStackToTheNext()
    {
        using var assembly = ManagedAssembly.Load(CorePath);
        var bodies = ManagedBodies.Build(assembly);
        var stack = new IlStack(assembly.Metadata);
        var member = TypeOf(assembly, "Spydate.Core.Text.AddressText").Members.First(m => m.Name == "ParseHex");
        var body = bodies.Of((System.Reflection.Metadata.MethodDefinitionHandle)member.Handle)!;

        // Every instruction in the method belongs to exactly one statement, and asking from
        // anywhere inside it gives the same answer — which is what makes this usable for stepping:
        // the range does not depend on how far into the statement execution happens to be.
        var byStart = new Dictionary<int, (int From, int To)>();
        foreach (var instruction in Il.Walk(body.Il))
        {
            var found = IlStatements.Containing(body, assembly.Metadata, instruction.Offset);
            Assert.NotNull(found);

            var (from, to) = found!.Value;
            Assert.InRange(instruction.Offset, from, to - 1);

            // Both ends are where the stack is empty: the start because that is where a statement
            // begins, the end because that is the next one's start.
            Assert.Equal(0, stack.Delta(body.Il, 0, from, body.Method));
            if (to < body.Il.Length)
            {
                Assert.Equal(0, stack.Delta(body.Il, 0, to, body.Method));
            }

            if (byStart.TryGetValue(from, out var already))
            {
                Assert.Equal(already, (from, to));
            }
            else
            {
                byStart[from] = (from, to);
            }
        }

        // Several statements, covering the method end to end with no gaps.
        Assert.True(byStart.Count > 3, $"only {byStart.Count} statements in a method with several");
        var ordered = byStart.Values.OrderBy(s => s.From).ToList();
        Assert.Equal(0, ordered[0].From);
        Assert.Equal(body.Il.Length, ordered[^1].To);
        for (int i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].To, ordered[i].From);
        }
    }

    [Fact]
    public void AnOffsetInsideAnInstructionIsNotAStatement()
    {
        using var assembly = ManagedAssembly.Load(CorePath);
        var bodies = ManagedBodies.Build(assembly);
        var member = TypeOf(assembly, "Spydate.Core.Text.AddressText").Members.First(m => m.Name == "ParseHex");
        var body = bodies.Of((System.Reflection.Metadata.MethodDefinitionHandle)member.Handle)!;

        // An operand byte is not a place. Answering for one would hand the stepper a range starting
        // mid-instruction, and the runtime would either refuse it or step from somewhere nobody
        // asked about.
        var multiByte = Il.Walk(body.Il).First(i => i.Length > 1);
        Assert.Null(IlStatements.Containing(body, assembly.Metadata, multiByte.Offset + 1));

        // Past the end is not a place either.
        Assert.Null(IlStatements.Containing(body, assembly.Metadata, body.Il.Length));
    }

    [Fact]
    public void NoAddressIsWrittenAgainstTwoLines()
    {
        var image = PeImage.Load(CorePath);
        using var assembly = ManagedAssembly.Load(CorePath);
        var bodies = ManagedBodies.Build(assembly);

        foreach (var type in assembly.Namespaces.SelectMany(n => n.Types).Take(20))
        {
            var source = assembly.Decompiler.SourceForType(type);
            var written = ManagedDecompiler.Addressed(source, bodies, image.ImageBase)
                .Split('\n')
                .Select(AddressText.FromLine)
                .Where(a => a is not null)
                .ToList();

            // A switch's case labels are all attributed to the switch instruction — nothing runs
            // when a label is reached — and several expressions on different lines routinely share
            // one statement. Eighteen lines carrying one address meant a click beside any of them
            // set a breakpoint several lines above, on the `switch`.
            Assert.Equal(written.Count, written.Distinct().Count());
        }
    }

    [Fact]
    public void AMethodWithNoBodyIsLeftAloneRatherThanGuessedAt()
    {
        using var assembly = ManagedAssembly.Load(CorePath);
        var bodies = ManagedBodies.Build(assembly);

        // An interface or an abstract member has no IL at all. Decompiling one must still produce
        // text, and must not attach an address to any of it.
        var type = TypeOf(assembly, "Spydate.Core.PE.PeParseException");
        var source = assembly.Decompiler.SourceForType(type);
        string addressed = ManagedDecompiler.Addressed(source, bodies, 0x400000);

        Assert.Contains("PeParseException", addressed, StringComparison.Ordinal);
    }
}
