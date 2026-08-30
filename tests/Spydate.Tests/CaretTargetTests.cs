using Spydate.Core.Text;

namespace Spydate.Tests;

/// <summary>
/// What F2 acts on. The ordering is the whole of the behaviour: a stack slot before a symbol, a symbol
/// before the line it sits on, the line before the document as a whole.
/// </summary>
public class CaretTargetTests
{
    private const ulong Line = 0x140001260;
    private const ulong Document = 0x140001000;

    [Theory]
    [InlineData("arg_0")]
    [InlineData("local_18")]
    public void AStackSlotUnderTheCaretWins(string word)
    {
        var target = CaretTargets.Resolve(word, Line, Document);

        Assert.Equal(CaretTargetKind.StackSlot, target.Kind);
        Assert.Equal(word, target.Slot);
    }

    [Fact]
    public void ASlotTheUserHasAlreadyNamedIsStillASlot()
    {
        // After renaming arg_0 to "count", the caret is on "count" - renaming again must find the slot.
        var target = CaretTargets.Resolve("count", Line, Document, slotForName: w => w == "count" ? "arg_0" : null);

        Assert.Equal(CaretTargetKind.StackSlot, target.Kind);
        Assert.Equal("arg_0", target.Slot);
    }

    [Fact]
    public void AGeneratedNameGivesItsOwnAddress()
    {
        // This is what lets a callee be renamed from the code that calls it.
        var target = CaretTargets.Resolve("sub_14000100A", Line, Document);

        Assert.Equal(CaretTargetKind.Address, target.Kind);
        Assert.Equal(0x14000100AUL, target.Address);
    }

    [Fact]
    public void AKnownSymbolGivesTheAddressItBelongsTo()
    {
        var target = CaretTargets.Resolve("ReadHeader", Line, Document, addressForSymbol: w => w == "ReadHeader" ? 0x140002000UL : null);

        Assert.Equal(0x140002000UL, target.Address);
    }

    [Fact]
    public void AnUnknownWordFallsBackToTheLine()
    {
        var target = CaretTargets.Resolve("rax", Line, Document);

        Assert.Equal(CaretTargetKind.Address, target.Kind);
        Assert.Equal(Line, target.Address);
    }

    [Fact]
    public void NoWordAndNoLineMeansTheDocumentItself()
    {
        var target = CaretTargets.Resolve(null, null, Document);

        Assert.Equal(Document, target.Address);
    }

    [Fact]
    public void NothingAtAllResolvesToNothing()
    {
        Assert.Equal(CaretTargetKind.None, CaretTargets.Resolve(null, null, null).Kind);
    }

    [Fact]
    public void ALineAddressBeatsTheDocument()
    {
        Assert.Equal(Line, CaretTargets.Resolve(null, Line, Document).Address);
    }

    [Theory]
    [InlineData("arg_0", true)]
    [InlineData("local_1C", true)]
    [InlineData("argument", false)]
    [InlineData("local", false)]
    [InlineData("arg_", false)]
    [InlineData("sub_1000", false)]
    [InlineData(null, false)]
    public void SlotNamesAreRecognisedByTheirPrefix(string? word, bool expected)
        => Assert.Equal(expected, CaretTargets.IsGeneratedSlotName(word));
}
