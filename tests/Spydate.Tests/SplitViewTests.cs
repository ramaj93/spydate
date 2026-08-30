using Spydate.Core.PE;
using Spydate.Core.Text;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// What showing a function in both views at once depends on: every address the pseudo-C states is an
/// instruction that is really there, so a line picked in one pane resolves in the other.
/// </summary>
public class SplitViewTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Windows\SysWOW64\notepad.exe")]
    public void EveryAddressInThePseudoCIsARealInstruction(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var image = PeImage.Load(path);
        var analysis = new BinaryAnalysis(image);
        analysis.DiscoverAll();
        var decompiler = new NativeDecompiler(analysis);

        int checkedLines = 0;
        foreach (var function in analysis.Functions.OrderBy(f => f.EntryVa).Take(200))
        {
            var instructions = function.Instructions.Select(i => i.Va).ToHashSet();
            var map = LineAddressMap.Build(decompiler.Decompile(function).Text);

            for (int line = 1; line <= 4000; line++)
            {
                if (map.AddressAt(line) is not { } address)
                {
                    continue;
                }

                checkedLines++;
                Assert.True(
                    instructions.Contains(address),
                    $"{function.Name}: line {line} claims 0x{address:X}, which is not an instruction in it");
            }
        }

        Assert.True(checkedLines > 500, $"only {checkedLines} addressed lines were checked");
    }

    [Fact]
    public void AConditionCarriesTheAddressOfItsTest()
    {
        // The `if` line used to state no address at all, so there was nothing there to follow or comment.
        var code = new byte[]
        {
            0x83, 0xF9, 0x0A,             // 0x1000 cmp ecx, 0xa
            0x7C, 0x06,                   // 0x1003 jl 0x100b
            0xB8, 0x01, 0x00, 0x00, 0x00, // 0x1005 mov eax, 1
            0xC3,                         // 0x100a ret
            0xB8, 0x02, 0x00, 0x00, 0x00, // 0x100b mov eax, 2
            0xC3,                         // 0x1010 ret
        };

        var symbols = new Core.Symbols.SymbolTable();
        var source = new MemoryCodeSource(code, 0x1000, 32);
        var dis = new X86Disassembler(32, symbols);
        var function = new FunctionDiscovery(source, dis, symbols).Discover(0x1000);
        string text = new NativeDecompiler(32, symbols).Decompile(function).Text;

        var map = LineAddressMap.Build(text);
        int line = text.Split('\n').ToList().FindIndex(l => l.Contains("if (", StringComparison.Ordinal)) + 1;

        Assert.True(line > 0, text);
        Assert.Equal(0x1003UL, map.AddressAt(line));
    }

    [Fact]
    public void ALoopCarriesTheAddressOfItsTest()
    {
        // mov eax,0 ; loop: add eax,ecx ; dec ecx ; jnz loop ; ret
        var code = new byte[] { 0xB8, 0x00, 0x00, 0x00, 0x00, 0x03, 0xC1, 0x49, 0x75, 0xFB, 0xC3 };

        var symbols = new Core.Symbols.SymbolTable();
        var source = new MemoryCodeSource(code, 0x1000, 32);
        var dis = new X86Disassembler(32, symbols);
        var function = new FunctionDiscovery(source, dis, symbols).Discover(0x1000);
        string text = new NativeDecompiler(32, symbols).Decompile(function).Text;

        var map = LineAddressMap.Build(text);
        int line = text.Split('\n').ToList().FindIndex(l => l.Contains("while (", StringComparison.Ordinal)) + 1;

        Assert.True(line > 0, text);
        Assert.Equal(0x1008UL, map.AddressAt(line));   // the `jnz` that ends each turn of the loop
    }
}
