using Spydate.Core.Symbols;
using Spydate.Disassembly;

namespace Spydate.Tests;

public class X86DisassemblerTests
{
    [Fact]
    public void Decodes32BitPrologueAndReturn()
    {
        // push ebp; mov ebp, esp; mov eax, [ebp+8]; pop ebp; ret
        var code = new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x5D, 0xC3 };
        var dis = new X86Disassembler(32);

        var insns = dis.Decode(code, 0x401000, 0x400000);

        Assert.Equal(5, insns.Count);
        Assert.Equal(new[] { "push", "mov", "mov", "pop", "ret" }, insns.Select(i => i.Mnemonic));
        Assert.Equal("ebp", insns[0].Operands);
        Assert.Equal("eax, [ebp+8]", insns[2].Operands.Replace(" ", string.Empty).Replace(",", ", "));
        Assert.Equal(InstructionFlow.Return, insns[4].Flow);
        Assert.Equal(0x1000u, insns[0].Rva);
        Assert.Equal("55", insns[0].BytesText);
    }

    [Fact]
    public void Decodes64BitCallAndReportsTarget()
    {
        // sub rsp, 28h ; call +0x10 ; add rsp, 28h ; ret
        var code = new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0x10, 0x00, 0x00, 0x00, 0x48, 0x83, 0xC4, 0x28, 0xC3 };
        var dis = new X86Disassembler(64);

        var insns = dis.Decode(code, 0x140001000, 0x140000000);

        var call = insns[1];
        Assert.Equal(InstructionFlow.Call, call.Flow);
        Assert.Equal(0x140001009UL + 0x10, call.BranchTargetVa);
        Assert.Equal(InstructionFlow.Return, insns[^1].Flow);
    }

    [Fact]
    public void IndirectCallThroughRipRelativeSlotUsesSymbol()
    {
        // call [rip+0x100] at 0x140001000 → slot = 0x140001006 + 0x100
        var code = new byte[] { 0xFF, 0x15, 0x00, 0x01, 0x00, 0x00 };
        var symbols = new SymbolTable();
        ulong slot = 0x140001006 + 0x100;
        symbols.Add(new Symbol(slot, "kernel32!ExitProcess", SymbolKind.Import));
        var dis = new X86Disassembler(64, symbols);

        var insns = dis.Decode(code, 0x140001000, 0x140000000);

        var call = Assert.Single(insns);
        Assert.Equal(InstructionFlow.IndirectCall, call.Flow);
        Assert.Equal(slot, call.IndirectSlotVa);
        Assert.Contains("kernel32!ExitProcess", call.Operands);
    }

    [Fact]
    public void ConditionalBranchTarget()
    {
        // test eax, eax ; jne +5 ; nop
        var code = new byte[] { 0x85, 0xC0, 0x75, 0x05, 0x90 };
        var dis = new X86Disassembler(32);

        var insns = dis.Decode(code, 0x1000, 0);

        Assert.Equal(InstructionFlow.ConditionalBranch, insns[1].Flow);
        Assert.Equal(0x1004UL + 5, insns[1].BranchTargetVa);
        Assert.Equal("jne", insns[1].Mnemonic);
    }

    [Fact]
    public void InvalidBytesBecomeDbPseudoInstructions()
    {
        // 0x06 (push es) is invalid in 64-bit mode, then a valid ret.
        var code = new byte[] { 0x06, 0xC3 };
        var dis = new X86Disassembler(64);

        var insns = dis.Decode(code, 0x1000, 0);

        Assert.Equal(2, insns.Count);
        Assert.Equal(InstructionFlow.Invalid, insns[0].Flow);
        Assert.Equal("db", insns[0].Mnemonic);
        Assert.Equal(1, insns[0].Length);
        Assert.Equal("ret", insns[1].Mnemonic);
    }

    [Fact]
    public void EmptyInputYieldsNothing()
    {
        var dis = new X86Disassembler(64);
        Assert.Empty(dis.Decode(ReadOnlyMemory<byte>.Empty, 0x1000, 0));
    }
}
