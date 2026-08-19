using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>The scalar SSE subset: one arithmetic operation on one value, and the conversions around it.</summary>
public class FloatLiftingTests
{
    private static DecompiledFunction Decompile(byte[] code, int bitness = 64)
    {
        var symbols = new SymbolTable();
        ulong baseVa = bitness == 64 ? 0x140001000u : 0x1000u;
        var source = new MemoryCodeSource(code, baseVa, bitness);
        var dis = new X86Disassembler(bitness, symbols);
        var function = new FunctionDiscovery(source, dis, symbols).Discover(baseVa);
        return new NativeDecompiler(bitness, symbols).Decompile(function);
    }

    [Fact]
    public void ScalarAdditionIsArithmeticNotInlineAsm()
    {
        // addsd xmm0, xmm1 ; ret
        var r = Decompile(new byte[] { 0xF2, 0x0F, 0x58, 0xC1, 0xC3 });

        Assert.Contains("xmm0 = xmm0 + xmm1;", r.Text);
        Assert.DoesNotContain("__asm", r.Text);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void TheVexFormKeepsItsSeparateDestination()
    {
        // vmulsd xmm0, xmm1, xmm2 ; ret
        var r = Decompile(new byte[] { 0xC5, 0xF3, 0x59, 0xC2, 0xC3 });

        Assert.Contains("xmm0 = xmm1 * xmm2;", r.Text);
        Assert.DoesNotContain("__asm", r.Text);
    }

    [Fact]
    public void IntegerToFloatIsAFloatCast()
    {
        // cvtsi2sd xmm0, ecx ; ret
        var r = Decompile(new byte[] { 0xF2, 0x0F, 0x2A, 0xC1, 0xC3 });

        Assert.Contains("xmm0 = (double)ecx;", r.Text);
    }

    [Fact]
    public void TruncatingBackToAnIntegerIsAnIntegerCast()
    {
        // cvttsd2si eax, xmm0 ; ret
        var r = Decompile(new byte[] { 0xF2, 0x0F, 0x2C, 0xC0, 0xC3 });

        Assert.Contains("(int32_t)xmm0", r.Text);
    }

    [Fact]
    public void SquareRootIsNamed()
    {
        // sqrtsd xmm0, xmm1 ; ret
        var r = Decompile(new byte[] { 0xF2, 0x0F, 0x51, 0xC1, 0xC3 });

        Assert.Contains("xmm0 = sqrt(xmm1);", r.Text);
    }

    [Fact]
    public void AFloatComparisonReadsAsAComparison()
    {
        // comisd xmm0, xmm1 ; jbe +2 ; mov eax, 1 ; ret ; xor eax, eax ; ret
        var code = new byte[]
        {
            0x66, 0x0F, 0x2F, 0xC1,       // comisd xmm0, xmm1
            0x76, 0x06,                   // jbe +6
            0xB8, 0x01, 0x00, 0x00, 0x00, // mov eax, 1
            0xC3,                         // ret
            0x31, 0xC0,                   // xor eax, eax
            0xC3,                         // ret
        };

        var r = Decompile(code);

        Assert.Contains("if (xmm0 <= xmm1)", r.Text);
        Assert.DoesNotContain("__asm", r.Text);
    }

    [Fact]
    public void PackedArithmeticStaysInlineAsm()
    {
        // addps xmm0, xmm1 ; ret — four additions at once is not one number plus another.
        var r = Decompile(new byte[] { 0x0F, 0x58, 0xC1, 0xC3 });

        Assert.Contains("__asm { addps", r.Text);
    }
}
