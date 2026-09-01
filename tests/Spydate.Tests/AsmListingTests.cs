using Spydate.Core.PE;
using Spydate.Core.Text;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// The disassembly listing. It had no tests until it moved out of the window: the test project
/// targets net10.0 and the app targets net10.0-windows, so nothing in the app can be reached from
/// here. Moving it down a layer is what made these possible.
/// </summary>
public class AsmListingTests
{
    private const ulong Base = 0x140001000;

    /// <summary>cmp/jl over two arms, so there is a branch, a label and a join.</summary>
    private static PeImage Branchy()
    {
        var code = new byte[0x40];
        Array.Fill(code, (byte)0xCC);
        new byte[]
        {
            0x83, 0xF9, 0x0A,             // +00 cmp ecx, 0xa
            0x7C, 0x06,                   // +03 jl +6 → +0B
            0xB8, 0x01, 0x00, 0x00, 0x00, // +05 mov eax, 1
            0xC3,                         // +0A ret
            0xB8, 0x02, 0x00, 0x00, 0x00, // +0B mov eax, 2
            0xC3,                         // +10 ret
        }.CopyTo(code, 0);
        return SyntheticPe.WithSectionData(code);
    }

    private static (BinaryAnalysis Analysis, Function Function) Open()
    {
        var analysis = new BinaryAnalysis(Branchy());
        return (analysis, analysis.GetOrDiscoverFunction(Base));
    }

    [Fact]
    public void AFunctionListingSaysWhatItIsBeforeItSaysWhatItDoes()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        var function = analysis.Functions.First(f => f.Blocks.Count is > 2 and < 12);

        string text = AsmListing.ForFunction(analysis, function);
        var lines = text.Split('\n');

        Assert.StartsWith($"; {function.Name} @ 0x{function.EntryVa:X}", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("; section ", StringComparison.Ordinal));
        Assert.Contains($"{function.Name} proc", text, StringComparison.Ordinal);
        Assert.Contains($"{function.Name} endp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryInstructionAppearsOnceAndInOrder()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        // The listing is what a person reads to check the decompiler against, so it has to be
        // complete and in order: a dropped instruction is a lie about the binary.
        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        int checkedFunctions = 0;

        foreach (var function in analysis.Functions.OrderBy(f => f.EntryVa).Where(f => f.InstructionCount < 200).Take(150))
        {
            string text = AsmListing.ForFunction(analysis, function);
            var map = LineAddressMap.Build(text);
            var listed = new List<ulong>();
            for (int line = 1; line <= text.Split('\n').Length; line++)
            {
                if (map.AddressAt(line) is { } va)
                {
                    listed.Add(va);
                }
            }

            Assert.Equal(function.Instructions.Select(i => i.Va).ToList(), listed);
            checkedFunctions++;
        }

        Assert.True(checkedFunctions > 100, $"only {checkedFunctions} functions were checked");
    }

    [Fact]
    public void ABlockSomethingBranchesToGetsALabel()
    {
        var (analysis, function) = Open();

        string text = AsmListing.ForFunction(analysis, function);

        // +0B is the `jl` target, so control can arrive there from elsewhere and the reader needs a
        // name for it. +05 is only ever fallen into, and a label there would be noise.
        Assert.Contains($"{analysis.NameFor(Base + 0x0B)}:", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{analysis.NameFor(Base + 0x05)}:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AUserCommentIsShownAgainstItsInstruction()
    {
        var (analysis, function) = Open();
        analysis.Annotations.SetComment(Base + 3, "the bounds check");

        string line = AsmListing.ForFunction(analysis, function)
            .Split('\n')
            .First(l => l.StartsWith((Base + 3).ToString("X16"), StringComparison.Ordinal));

        Assert.Contains("; the bounds check", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetRenamedAfterDecodingIsShownUnderItsNewName()
    {
        // Operands are re-formatted at print time rather than reused from the decoder, so a name
        // given after the bytes were decoded still reaches the page.
        var (analysis, function) = Open();
        analysis.Annotations.SetName(Base + 0x0B, "not_small_enough");

        Assert.Contains("not_small_enough", AsmListing.ForFunction(analysis, function), StringComparison.Ordinal);
    }

    [Fact]
    public void ARangeListingIsLinearAndSaysSo()
    {
        var analysis = new BinaryAnalysis(Branchy());

        string text = AsmListing.ForRange(analysis, Base, 0x11);

        Assert.StartsWith($"; linear disassembly from 0x{Base:X}", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" proc", text, StringComparison.Ordinal);   // no blocks, so no labels
        Assert.Contains("cmp", text, StringComparison.Ordinal);
        Assert.Contains("ret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameFunctionAlwaysListsTheSameWay()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        var function = analysis.Functions.First(f => f.Blocks.Count > 3);

        Assert.Equal(AsmListing.ForFunction(analysis, function), AsmListing.ForFunction(analysis, function));
    }
}
