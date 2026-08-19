using Spydate.Core.PE;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Decompiler.Native.Lifting;
using Spydate.Decompiler.Native.Structuring;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// Control-flow structuring: the shapes the emitter should recover, and the invariants that make the
/// result trustworthy even when it cannot recover one.
/// </summary>
public class StructuringTests
{
    private static string Decompile(byte[] code, ulong baseVa, int bitness)
    {
        var symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, baseVa, bitness);
        var dis = new X86Disassembler(bitness, symbols);
        var function = new FunctionDiscovery(source, dis, symbols).Discover(baseVa);
        return new NativeDecompiler(bitness, symbols).Decompile(function).Text;
    }

    [Fact]
    public void TwoWayBranchBecomesIfElse()
    {
        // cmp ecx, 10 ; jl +6 ; mov eax, 1 ; ret ; mov eax, 2 ; ret
        var code = new byte[]
        {
            0x83, 0xF9, 0x0A,             // 0x1000 cmp ecx, 0xa
            0x7C, 0x06,                   // 0x1003 jl 0x100b
            0xB8, 0x01, 0x00, 0x00, 0x00, // 0x1005 mov eax, 1
            0xC3,                         // 0x100a ret
            0xB8, 0x02, 0x00, 0x00, 0x00, // 0x100b mov eax, 2
            0xC3,                         // 0x1010 ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("if ((int32_t)ecx < 10)", text);
        Assert.Contains("else", text);
        Assert.Contains("return 2;", text);
        Assert.Contains("return 1;", text);
        Assert.DoesNotContain("goto", text);
        Assert.DoesNotContain("loc_", text);
    }

    [Fact]
    public void OneArmedBranchHasNoElse()
    {
        // test ecx, ecx ; jz +5 ; inc eax ; ret
        var code = new byte[]
        {
            0x85, 0xC9,                   // 0x1000 test ecx, ecx
            0x74, 0x02,                   // 0x1002 je 0x1006
            0xFF, 0xC0,                   // 0x1004 inc eax
            0xC3,                         // 0x1006 ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("if (ecx != 0)", text);   // inverted: the jump skips the body
        Assert.DoesNotContain("else", text);
        Assert.DoesNotContain("goto", text);
    }

    [Fact]
    public void BottomTestedLoopBecomesDoWhile()
    {
        // mov eax, 0 ; loop: add eax, ecx ; dec ecx ; jnz loop ; ret
        var code = new byte[]
        {
            0xB8, 0x00, 0x00, 0x00, 0x00, // 0x1000 mov eax, 0
            0x03, 0xC1,                   // 0x1005 add eax, ecx
            0x49,                         // 0x1007 dec ecx
            0x75, 0xFB,                   // 0x1008 jne 0x1005
            0xC3,                         // 0x100a ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("do", text);
        Assert.Contains("while (ecx", text);
        Assert.DoesNotContain("goto", text);
    }

    [Fact]
    public void TopTestedLoopBecomesWhile()
    {
        // xor eax, eax ; jmp test ; body: add eax, ecx ; dec ecx ; test: test ecx, ecx ; jnz body ; ret
        var code = new byte[]
        {
            0x31, 0xC0,                   // 0x1000 xor eax, eax
            0xEB, 0x03,                   // 0x1002 jmp 0x1007
            0x03, 0xC1,                   // 0x1004 add eax, ecx
            0x49,                         // 0x1006 dec ecx
            0x85, 0xC9,                   // 0x1007 test ecx, ecx
            0x75, 0xF9,                   // 0x1009 jne 0x1004
            0xC3,                         // 0x100b ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("while (ecx != 0)", text);
        Assert.DoesNotContain("do", text);
        Assert.DoesNotContain("goto", text);
    }

    [Fact]
    public void EarlyExitFromALoopBecomesBreak()
    {
        // loop: cmp [edx], 0 ; je out ; add edx, 4 ; dec ecx ; jnz loop ; out: ret
        var code = new byte[]
        {
            0x83, 0x3A, 0x00,             // 0x1000 cmp dword [edx], 0
            0x74, 0x06,                   // 0x1003 je 0x100b
            0x83, 0xC2, 0x04,             // 0x1005 add edx, 4
            0x49,                         // 0x1008 dec ecx
            0x75, 0xF5,                   // 0x1009 jne 0x1000
            0xC3,                         // 0x100b ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("while (", text);
        Assert.Contains("break;", text);
        Assert.DoesNotContain("goto", text);
    }

    [Fact]
    public void UnstructuredEdgeKeepsItsLabel()
    {
        // Two paths converge on a shared tail that neither dominates: one arm falls into it, the other
        // has to jump. The goto that survives must still have a label to land on.
        var code = new byte[]
        {
            0x85, 0xC9,                   // 0x1000 test ecx, ecx
            0x74, 0x07,                   // 0x1002 je 0x100b
            0x85, 0xD2,                   // 0x1004 test edx, edx
            0x74, 0x05,                   // 0x1006 je 0x100d
            0xFF, 0xC0,                   // 0x1008 inc eax
            0xC3,                         // 0x100a ret
            0xFF, 0xC8,                   // 0x100b dec eax
            0xFF, 0xC8,                   // 0x100d dec eax
            0xC3,                         // 0x100f ret
        };

        string text = Decompile(code, 0x1000, 32);

        foreach (string line in text.Split('\n'))
        {
            int at = line.IndexOf("goto loc_", StringComparison.Ordinal);
            if (at < 0 || line.Contains("outside this function", StringComparison.Ordinal))
            {
                continue;
            }

            string label = line[(at + "goto ".Length)..].TrimEnd('\r', ' ', ';');
            Assert.Contains(label + ":", text);
        }
    }

    [Fact]
    public void ARecoveredTableBecomesASwitch()
    {
        // cmp eax,2 ; ja default ; jmp [eax*4+table], with three cases that each return.
        var code = new byte[0x40];
        Array.Fill(code, (byte)0xCC);
        new byte[] { 0x83, 0xF8, 0x02 }.CopyTo(code, 0x00);
        new byte[] { 0x77, 0x1B }.CopyTo(code, 0x03);
        new byte[] { 0xFF, 0x24, 0x85, 0x30, 0x10, 0x00, 0x00 }.CopyTo(code, 0x05);
        new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }.CopyTo(code, 0x0C);
        new byte[] { 0xB8, 0x02, 0x00, 0x00, 0x00, 0xC3 }.CopyTo(code, 0x14);
        new byte[] { 0x31, 0xC0, 0xC3 }.CopyTo(code, 0x1A);
        new byte[] { 0xB8, 0xFF, 0xFF, 0xFF, 0xFF, 0xC3 }.CopyTo(code, 0x20);
        new byte[] { 0x0C, 0x10, 0x00, 0x00, 0x14, 0x10, 0x00, 0x00, 0x1A, 0x10, 0x00, 0x00 }.CopyTo(code, 0x30);

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("switch (eax)", text);
        Assert.Contains("case 0:", text);
        Assert.Contains("case 1:", text);
        Assert.Contains("case 2:", text);
        Assert.Contains("return 1;", text);
        Assert.Contains("return 2;", text);
        Assert.DoesNotContain("__asm", text);   // the dispatch is no longer an unlifted instruction
    }

    [Fact]
    public void CasesSharingABodyShareAnArm()
    {
        // The same dispatch, but every entry points at the same body.
        var code = new byte[0x40];
        Array.Fill(code, (byte)0xCC);
        new byte[] { 0x83, 0xF8, 0x02 }.CopyTo(code, 0x00);
        new byte[] { 0x77, 0x1B }.CopyTo(code, 0x03);
        new byte[] { 0xFF, 0x24, 0x85, 0x30, 0x10, 0x00, 0x00 }.CopyTo(code, 0x05);
        new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }.CopyTo(code, 0x0C);
        new byte[] { 0xB8, 0xFF, 0xFF, 0xFF, 0xFF, 0xC3 }.CopyTo(code, 0x20);
        new byte[] { 0x0C, 0x10, 0x00, 0x00, 0x0C, 0x10, 0x00, 0x00, 0x0C, 0x10, 0x00, 0x00 }.CopyTo(code, 0x30);

        string text = Decompile(code, 0x1000, 32);

        // One arm carries all three labels: nothing but labels between the first and the last.
        int first = text.IndexOf("case 0:", StringComparison.Ordinal);
        int last = text.IndexOf("case 2:", StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first, text);
        Assert.DoesNotContain(";", text[first..last]);
        Assert.Equal(1, text.Split("return 1;").Length - 1);
    }

    /// <summary>
    /// The invariant that makes the output safe to read: over every function in a real binary, each block
    /// is emitted exactly once, and every goto has somewhere to land.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Windows\SysWOW64\notepad.exe")]
    public void EveryBlockIsEmittedExactlyOnce(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var image = PeImage.Load(path);
        var analysis = new BinaryAnalysis(image);
        analysis.DiscoverAll();
        var lifter = new X86Lifter(image.Bitness, analysis.Symbols);

        foreach (var function in analysis.Functions.OrderBy(f => f.EntryVa))
        {
            var ir = lifter.Lift(function);
            var body = Structurer.Structure(ir);

            var labels = CStmts.Descendants(body).OfType<CLabel>().Select(l => l.Va).ToList();
            Assert.Equal(labels.Count, labels.Distinct().Count());
            Assert.Equal(
                ir.Blocks.Select(b => b.StartVa).OrderBy(v => v),
                labels.OrderBy(v => v));

            var landable = labels.ToHashSet();
            foreach (var jump in CStmts.Descendants(body).OfType<CGoto>().Where(g => !g.External))
            {
                Assert.Contains(jump.Va, landable);
            }
        }
    }
}
