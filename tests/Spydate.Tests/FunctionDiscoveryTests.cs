using Spydate.Core.Symbols;
using Spydate.Disassembly;

namespace Spydate.Tests;

public class FunctionDiscoveryTests
{
    private static FunctionDiscovery Create(byte[] code, ulong baseVa, int bitness, out SymbolTable symbols)
    {
        symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, baseVa, bitness);
        var dis = new X86Disassembler(bitness, symbols);
        return new FunctionDiscovery(source, dis, symbols);
    }

    [Fact]
    public void StraightLineFunctionIsOneBlock()
    {
        var code = new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x5D, 0xC3 };
        var discovery = Create(code, 0x401000, 32, out _);

        var f = discovery.Discover(0x401000);

        var block = Assert.Single(f.Blocks);
        Assert.Equal(5, block.Instructions.Count);
        Assert.Equal(0x401008UL, f.EndVa);
        Assert.Empty(block.Successors);
        Assert.Equal("sub_401000", f.Name);
    }

    [Fact]
    public void ConditionalBranchSplitsIntoThreeBlocks()
    {
        // 0x1000: test eax, eax
        // 0x1002: jne 0x1009
        // 0x1004: mov eax, 1
        // 0x1009: ret
        var code = new byte[]
        {
            0x85, 0xC0,
            0x75, 0x05,
            0xB8, 0x01, 0x00, 0x00, 0x00,
            0xC3,
        };
        var discovery = Create(code, 0x1000, 32, out _);

        var f = discovery.Discover(0x1000);

        Assert.Equal(3, f.Blocks.Count);
        var b0 = f.BlockByVa[0x1000];
        var b1 = f.BlockByVa[0x1004];
        var b2 = f.BlockByVa[0x1009];
        Assert.Equal(new ulong[] { 0x1004, 0x1009 }, b0.Successors);
        Assert.Equal(new ulong[] { 0x1009 }, b1.Successors);
        Assert.Empty(b2.Successors);
        Assert.Equal(2, b2.Predecessors.Count);
    }

    [Fact]
    public void CallTargetsAreRecordedButNotFollowed()
    {
        // 0x1000: call 0x1010 ; ret ; (padding) ; 0x1010: ret
        var code = new byte[16 + 1];
        code[0] = 0xE8; BitConverter.GetBytes(0x1010 - 0x1005).CopyTo(code, 1);
        code[5] = 0xC3;
        code[16] = 0xC3;
        var discovery = Create(code, 0x1000, 32, out _);

        var f = discovery.Discover(0x1000);

        Assert.Single(f.Blocks);
        Assert.Equal(new ulong[] { 0x1010 }, f.CallTargets);
    }

    [Fact]
    public void LoopIsHandledWithoutInfiniteRecursion()
    {
        // 0x1000: dec ecx ; jne 0x1000 ; ret
        var code = new byte[] { 0x49, 0x75, 0xFD, 0xC3 };
        var discovery = Create(code, 0x1000, 32, out _);

        var f = discovery.Discover(0x1000);

        Assert.Equal(2, f.Blocks.Count);
        Assert.Contains(0x1000UL, f.BlockByVa[0x1000].Successors);
        Assert.Contains(0x1000UL, f.BlockByVa[0x1000].Predecessors);
    }

    [Fact]
    public void JumpOutsideImageIsNoted()
    {
        // jmp far away
        var code = new byte[] { 0xE9, 0x00, 0x10, 0x00, 0x00 };
        var discovery = Create(code, 0x1000, 32, out _);

        var f = discovery.Discover(0x1000);

        Assert.Single(f.Blocks);
        Assert.Contains(f.Notes, n => n.Contains("outside", StringComparison.OrdinalIgnoreCase));
    }
}
