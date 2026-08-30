using Spydate.Core.Text;

namespace Spydate.Tests;

/// <summary>Following one view of a function from another: which line is about which address.</summary>
public class LineAddressMapTests
{
    private const string Listing = """
        ; sub_140001000 @ 0x140001000  (2 blocks, 4 instructions, 0xE bytes)
        sub_140001000 proc
        0000000140001000  48 89 5C 24 08    mov [rsp+8], rbx
        0000000140001005  85 C9             test ecx, ecx
        0000000140001007  74 05             je 0x14000100e
        000000014000100E  C3                ret
        sub_140001000 endp
        """;

    private const string PseudoC = """
        // Function sub_140001000 @ 0x140001000 (2 blocks)
        uint64_t sub_140001000(void)
        {
            arg_0 = rbx;                                        // 140001000
            if (ecx == 0)                                       // 140001007
            {
                return rax;                                     // 14000100E
            }
        }
        """;

    [Fact]
    public void EveryLineThatStatesAnAddressIsFound()
    {
        var listing = LineAddressMap.Build(Listing);
        var pseudo = LineAddressMap.Build(PseudoC);

        Assert.Equal(4, listing.Count);
        Assert.Equal(3, pseudo.Count);
        Assert.Equal(0x140001000UL, listing.AddressAt(3));
        Assert.Equal(0x140001007UL, pseudo.AddressAt(5));
        Assert.Null(pseudo.AddressAt(2));   // the signature is about nothing in particular
    }

    [Fact]
    public void AnAddressWithALineOfItsOwnFindsIt()
    {
        var pseudo = LineAddressMap.Build(PseudoC);

        Assert.Equal(4, pseudo.LineFor(0x140001000));
        Assert.Equal(5, pseudo.LineFor(0x140001007));
        Assert.Equal(7, pseudo.LineFor(0x14000100E));
    }

    [Fact]
    public void AnInstructionThatWasFoldedAwayLandsOnTheStatementItIsPartOf()
    {
        // 0x140001005 is the `test`, which the decompiler folded into the `if` at 0x140001007. The
        // nearest line at or before it is the assignment, which is where that instruction still sits.
        var pseudo = LineAddressMap.Build(PseudoC);

        Assert.Equal(4, pseudo.LineFor(0x140001005));
    }

    [Fact]
    public void AnAddressBeforeTheTextHasNoLine()
    {
        Assert.Null(LineAddressMap.Build(PseudoC).LineFor(0x140000FFF));
    }

    [Fact]
    public void TheSpanOfATextIsKnown()
    {
        var listing = LineAddressMap.Build(Listing);

        Assert.True(listing.Covers(0x140001005));
        Assert.False(listing.Covers(0x140000FFF));
        Assert.False(listing.Covers(0x140002000));
    }

    [Fact]
    public void BothViewsAgreeOnWhereAnAddressIs()
    {
        // The point of the whole thing: an address picked in one view resolves in the other.
        var listing = LineAddressMap.Build(Listing);
        var pseudo = LineAddressMap.Build(PseudoC);

        ulong address = listing.AddressAt(6)!.Value;   // the `ret` line

        Assert.Equal(0x14000100EUL, address);
        Assert.Equal(7, pseudo.LineFor(address));
    }

    [Fact]
    public void EmptyTextIsHarmless()
    {
        var map = LineAddressMap.Build(string.Empty);

        Assert.Equal(0, map.Count);
        Assert.Null(map.LineFor(0x1000));
        Assert.Null(map.AddressAt(1));
        Assert.False(map.Covers(0x1000));
    }
}
