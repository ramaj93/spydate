using Spydate.Core.Text;

namespace Spydate.Tests;

/// <summary>
/// Turning a place in the output back into an address, which is how the rename and comment commands know
/// what the user is pointing at.
/// </summary>
public class AddressTextTests
{
    [Theory]
    [InlineData("0000000140001000  48 89 5C 24 08    mov [rsp+8], rbx", 0x140001000UL)]
    [InlineData("00401000  55                push ebp", 0x401000UL)]
    [InlineData("    0000000140001000  C3   ret", 0x140001000UL)]
    public void AListingLineStartsWithItsAddress(string line, ulong expected)
        => Assert.Equal(expected, AddressText.FromLine(line));

    [Theory]
    [InlineData("    return 1;                                       // 140001260", 0x140001260UL)]
    [InlineData("    eax = 5;      // 40DFA8  the retry counter", 0x40DFA8UL)]
    public void PseudoCKeepsTheAddressInATrailingComment(string line, ulong expected)
        => Assert.Equal(expected, AddressText.FromLine(line));

    [Theory]
    [InlineData("")]
    [InlineData("uint32_t sub_1000(void)")]
    [InlineData("; section .text")]
    [InlineData("    // a comment with no address")]
    public void LinesWithoutAnAddressGiveNothing(string line)
        => Assert.Null(AddressText.FromLine(line));

    [Theory]
    [InlineData("sub_140001260", 0x140001260UL)]
    [InlineData("loc_40DFA8", 0x40DFA8UL)]
    [InlineData("loc_40DFA8:", 0x40DFA8UL)]
    [InlineData("data_14003A100", 0x14003A100UL)]
    [InlineData("unk_401000", 0x401000UL)]
    public void AGeneratedNameCarriesItsAddress(string identifier, ulong expected)
        => Assert.Equal(expected, AddressText.FromGeneratedName(identifier));

    [Theory]
    [InlineData("ParseCommandLine")]
    [InlineData("kernel32!CreateFileW")]
    [InlineData("submarine")]
    [InlineData("")]
    [InlineData("sub_notHex")]
    public void ANameTheUserChoseCarriesNothing(string identifier)
        => Assert.Null(AddressText.FromGeneratedName(identifier));

    [Fact]
    public void TheWordUnderTheCaretIsFound()
    {
        const string line = "    rax = sub_140001260(arg_0);";
        int at = line.IndexOf("sub_", StringComparison.Ordinal);

        Assert.Equal("sub_140001260", AddressText.WordAt(line, at));
        Assert.Equal("sub_140001260", AddressText.WordAt(line, at + 5));
        Assert.Equal("rax", AddressText.WordAt(line, 4));
        Assert.Equal("arg_0", AddressText.WordAt(line, line.IndexOf("arg_0", StringComparison.Ordinal) + 2));
    }

    [Fact]
    public void ACaretJustPastAWordStillMeansThatWord()
    {
        // Clicking at the end of a name puts the caret after it, which is where people expect to be.
        const string line = "call sub_1000";

        Assert.Equal("sub_1000", AddressText.WordAt(line, line.Length));
    }

    [Fact]
    public void ACaretOnNothingMeansNothing()
    {
        Assert.Null(AddressText.WordAt("a  b", 2));
        Assert.Null(AddressText.WordAt(string.Empty, 0));
        Assert.Null(AddressText.WordAt("abc", 99));
    }

    [Theory]
    [InlineData("0x140001000", 0x140001000UL)]
    [InlineData("140001000", 0x140001000UL)]
    [InlineData("  0X1a  ", 0x1AUL)]
    public void HexIsAcceptedWithOrWithoutThePrefix(string text, ulong expected)
        => Assert.Equal(expected, AddressText.ParseHex(text));

    [Theory]
    [InlineData("not hex")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotAnAddress(string? text) => Assert.Null(AddressText.ParseHex(text));
}
