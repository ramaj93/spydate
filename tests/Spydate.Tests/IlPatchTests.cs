using System.Reflection.Emit;
using System.Reflection.Metadata;
using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Core.Text;
using Spydate.Decompiler.Managed;

namespace Spydate.Tests;

/// <summary>
/// Patching IL: naming an instruction by an address, assembling a replacement, and refusing the ones
/// that would produce a file the runtime will not load.
/// </summary>
public class IlPatchTests
{
    private static string Path => typeof(PeImage).Assembly.Location;

    private static readonly Lazy<ManagedAssembly> Loaded =
        new(() => ManagedAssembly.Load(Path), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ManagedBodies> Indexed =
        new(() => ManagedBodies.Build(Loaded.Value), LazyThreadSafetyMode.ExecutionAndPublication);

    private static ManagedAssembly Assembly => Loaded.Value;

    private static ManagedBodies Bodies => Indexed.Value;

    private static PeImage Image => Corpus.Image(Path);

    private static ManagedMember Member(string type, string name) => Assembly.Namespaces
        .SelectMany(n => n.Types)
        .First(t => t.FullName == type)
        .Members.First(m => m.Name == name);

    /// <summary>The first instruction anywhere in the assembly that a test accepts.</summary>
    private static (ManagedBody Body, IlInstruction At) Find(Func<ManagedBody, IlInstruction, bool> wanted)
    {
        foreach (var body in Bodies.All)
        {
            foreach (var instruction in Il.Walk(body.Il))
            {
                if (wanted(body, instruction))
                {
                    return (body, instruction);
                }
            }
        }

        throw new InvalidOperationException("no instruction in Spydate.Core matched");
    }

    /// <summary>
    /// Whether an instruction moves the stack, which is what makes replacing it with nop unsafe.
    ///
    /// A call that returns something leaves one value behind; a call taking arguments has consumed
    /// some. Either way nop leaves the depth different, and the runtime refuses the method.
    /// </summary>
    private static bool Moves(ManagedBody body, IlInstruction at)
        => new IlStack(Assembly.Metadata).Delta(at, body.Il, body.Method) is { } delta && delta != 0;

    private static ulong Va(ManagedBody body, int offset) => Image.ImageBase + body.RvaOf(offset);

    // ------------------------------------------------------------------
    // Finding the bytes
    // ------------------------------------------------------------------

    [Fact]
    public void AMethodsIlIsFoundAtAnAddressInTheFile()
    {
        var load = Member("Spydate.Core.PE.PeImage", "Load");
        var body = Bodies.Of(load.Handle);

        Assert.NotNull(body);
        Assert.True(body!.IlRva > body.BodyRva, "the IL must start after the body header, not at it");
        Assert.NotEmpty(body.Il);

        // The bytes at that address in the file are the bytes the metadata reader handed back. This
        // is the whole claim the patcher rests on: one byte out and every patch lands one byte out.
        var present = Image.ReadAtVa(Image.ImageBase + body.IlRva, body.Il.Length);
        Assert.Equal(body.Il.ToArray(), present.ToArray());
    }

    [Fact]
    public void AnAddressInsideABodyFindsItAndOnlyIt()
    {
        var load = Bodies.Of(Member("Spydate.Core.PE.PeImage", "Load").Handle)!;

        Assert.Equal(load, Bodies.At(load.IlRva));
        Assert.Equal(load, Bodies.At(load.IlEndRva - 1));
        Assert.NotEqual(load, Bodies.At(load.IlEndRva));
        Assert.Equal(7, load.OffsetOf(load.RvaOf(7)));
    }

    [Fact]
    public void EveryBodyIsReadable()
        => Assert.Equal(0, Bodies.Unreadable);

    // ------------------------------------------------------------------
    // The listing
    // ------------------------------------------------------------------

    [Fact]
    public void TheIlListingCarriesAnAddressTheRestOfTheProgramCanRead()
    {
        var load = Member("Spydate.Core.PE.PeImage", "Load");
        var body = Bodies.Of(load.Handle)!;

        string listing = ManagedDecompiler.Addressed(
            Assembly.Decompiler.DisassembleMember(load), body, Image.ImageBase, Image.Is64Bit);

        var addressed = listing.Split('\n').Where(l => l.Contains("IL_", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(addressed);

        foreach (string line in addressed)
        {
            // AddressText.FromLine is what the breakpoint gutter and the caret both use. If it
            // cannot read this, the address column is decoration.
            Assert.NotNull(AddressText.FromLine(line));
        }

        Assert.Equal(Image.ImageBase + body.IlRva, AddressText.FromLine(addressed[0]));
    }

    // ------------------------------------------------------------------
    // Assembling
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("nop", "00")]
    [InlineData("ret", "2A")]
    [InlineData("pop", "26")]
    [InlineData("ldnull", "14")]
    [InlineData("ldc.i4.1", "17")]
    [InlineData("ldc.i4.s 42", "1F2A")]
    [InlineData("nop; nop; ret", "00002A")]
    [InlineData("bytes: 00 00 2A", "00002A")]
    public void TheSubsetAssemblesToTheBytesItShould(string text, string expected)
    {
        var result = IlAssembler.Encode(text, 0);

        Assert.True(result.Ok, result.Problem);
        Assert.Equal(expected, Convert.ToHexString(result.Bytes));
    }

    [Fact]
    public void ABranchIsEncodedAsADistanceFromTheEndOfItself()
    {
        // brfalse.s at IL_0006 is two bytes, so a target of IL_0010 is eight away from IL_0008.
        var result = IlAssembler.Encode("brfalse.s IL_0010", 6);

        Assert.True(result.Ok, result.Problem);
        Assert.Equal("2C08", Convert.ToHexString(result.Bytes));
    }

    [Fact]
    public void AnInstructionNamingAMetadataTokenIsRefusedByName()
    {
        // The line this draws is not effort. A token is a row index in this assembly's tables, and
        // writing one that is not already there means adding a row and moving everything after it.
        var result = IlAssembler.Encode("call Foo::Bar", 0);

        Assert.False(result.Ok);
        Assert.Contains("metadata token", result.Problem!, StringComparison.Ordinal);
        Assert.Contains("bytes:", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAnInstructionIsRefusedRatherThanGuessedAt()
    {
        Assert.Contains("is not an IL instruction", IlAssembler.Encode("frobnicate", 0).Problem!, StringComparison.Ordinal);
        Assert.Contains("takes no operand", IlAssembler.Encode("nop 4", 0).Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetTooFarForAShortBranchNamesTheLongForm()
    {
        string problem = IlAssembler.Encode("br.s IL_0400", 0).Problem!;

        Assert.Contains("cannot reach", problem, StringComparison.Ordinal);
        Assert.Contains("br,", problem, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Fitting it over what is there
    // ------------------------------------------------------------------

    [Fact]
    public void AReplacementOfTheSameStackDepthIsAccepted()
    {
        // ldarg.0 and ldnull both leave one value behind, so swapping them keeps the method
        // verifiable - which is the only kind of IL patch worth writing.
        var (body, at) = Find((_, i) => i.Op.Value == OpCodes.Ldarg_0.Value);

        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Va(body, at.Offset), "ldnull");

        Assert.True(proposal.Ok, proposal.Problem);
        Assert.Equal(at.Length, proposal.Patch!.Length);
        Assert.Equal("14", proposal.Patch.Hex);
    }

    [Fact]
    public void TakingOutACallThatReturnsSomethingIsRefusedWithTheCount()
    {
        // The failure this prevents is the whole reason the stack is computed. NOPping a call in
        // x86 leaves a wrong value in a register and the program runs on being wrong; in IL it
        // leaves the stack a different depth and the runtime refuses the method outright, at the
        // moment it is first called, nowhere near the patch.
        var (body, call) = Find((b, i) => i.Op.Value == OpCodes.Call.Value && Moves(b, i));

        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Va(body, call.Offset), "nop");

        Assert.False(proposal.Ok);
        Assert.Contains("will not verify", proposal.Problem!, StringComparison.Ordinal);
        Assert.Contains("InvalidProgramException", proposal.Problem!, StringComparison.Ordinal);
        Assert.Contains("force", proposal.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceWritesItAnyway()
    {
        var (body, call) = Find((b, i) => i.Op.Value == OpCodes.Call.Value && Moves(b, i));

        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Va(body, call.Offset), "nop", force: true);

        Assert.True(proposal.Ok, proposal.Problem);
        Assert.Equal(call.Length, proposal.Patch!.Length);
        Assert.All(proposal.Patch.Bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void AnAddressInsideAnInstructionNamesTheOneItIsInside()
    {
        // An offset that is not a boundary would overwrite the tail of an instruction and leave its
        // head to be decoded against whatever follows. The refusal carries the address to use.
        var (body, call) = Find((_, i) => i.Length > 1);

        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Va(body, call.Offset + 1), "nop");

        Assert.False(proposal.Ok);
        Assert.Contains($"IL_{call.Offset:X4}", proposal.Problem!, StringComparison.Ordinal);
        Assert.Contains($"0x{Va(body, call.Offset):X}", proposal.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressOutsideAnyMethodSaysSo()
    {
        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Image.ImageBase + 0x200, "nop");

        Assert.False(proposal.Ok);
        Assert.Contains("not inside any method's IL", proposal.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortReplacementGrowsToWholeInstructionsAndIsPaddedWithNops()
    {
        var (body, call) = Find((b, i) => i.Op.Value == OpCodes.Call.Value && i.Length == 5 && Moves(b, i));

        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Va(body, call.Offset), "nop", force: true);

        Assert.True(proposal.Ok, proposal.Problem);
        Assert.Equal(5, proposal.Patch!.Length);
        Assert.Contains("nop(s) padded", proposal.Patch.Comment!, StringComparison.Ordinal);
    }

    [Fact]
    public void APatchNamesTheMethodItLandsIn()
    {
        var (body, at) = Find((_, i) => i.Op.Value == OpCodes.Ldarg_0.Value);

        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Va(body, at.Offset), "ldnull");

        Assert.Contains("::", proposal.Patch!.Comment!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The whole loop
    // ------------------------------------------------------------------

    [Fact]
    public void AnIlPatchIsWrittenByTheSameMachineryAsANativeOne()
    {
        // Nothing in PatchStore or PatchWriter knows about IL, and that is the point: an IL patch is
        // a change to bytes at an RVA, which is what those have always taken.
        var (body, at) = Find((_, i) => i.Op.Value == OpCodes.Ldarg_0.Value);
        var proposal = IlPatches.Assemble(Assembly, Bodies, Image, Va(body, at.Offset), "ldnull");

        var store = new PatchStore();
        store.Set(proposal.Patch!.Rva, proposal.Patch);

        string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spydate-il-{Guid.NewGuid():N}.dll");
        try
        {
            var written = PatchWriter.Write(Image, store, copy);
            Assert.True(written.Ok, string.Join("; ", written.Problems));
            Assert.Equal(1, written.Applied);

            byte[] before = File.ReadAllBytes(Path);
            byte[] after = File.ReadAllBytes(copy);
            Assert.Equal(before.Length, after.Length);

            var differing = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
            Assert.Single(differing);
            Assert.Equal(0x14, after[differing[0]]);
        }
        finally
        {
            File.Delete(copy);
        }
    }


}
