using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>Copy propagation across block boundaries: what survives a join, and what must not.</summary>
public class PropagationTests
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
    public void AValueSetInOneBlockIsReadInTheNext()
    {
        var code = new byte[]
        {
            0xBF, 0x05, 0x00, 0x00, 0x00, // 0x1000 mov edi, 5
            0x85, 0xC9,                   // 0x1005 test ecx, ecx
            0x74, 0x03,                   // 0x1007 je 0x100c
            0x89, 0xF8,                   // 0x1009 mov eax, edi
            0xC3,                         // 0x100b ret
            0x31, 0xC0,                   // 0x100c xor eax, eax
            0xC3,                         // 0x100e ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("return 5;", text);
        Assert.Contains("return 0;", text);
        Assert.DoesNotContain("edi", text);
    }

    [Fact]
    public void PredecessorsThatDisagreeKeepTheRegister()
    {
        // Both paths set edi, to different values; the join must read edi rather than pick one.
        var code = new byte[]
        {
            0x85, 0xC9,                   // 0x1000 test ecx, ecx
            0x74, 0x07,                   // 0x1002 je 0x100b
            0xBF, 0x01, 0x00, 0x00, 0x00, // 0x1004 mov edi, 1
            0xEB, 0x05,                   // 0x1009 jmp 0x1010
            0xBF, 0x02, 0x00, 0x00, 0x00, // 0x100b mov edi, 2
            0x89, 0xF8,                   // 0x1010 mov eax, edi
            0xC3,                         // 0x1012 ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("return edi;", text);
        Assert.DoesNotContain("return 1;", text);
        Assert.DoesNotContain("return 2;", text);
    }

    [Fact]
    public void AValueThatOnlyOnePathSetsDoesNotSurviveTheJoin()
    {
        // edi is set on the taken path only, so the join cannot assume it.
        var code = new byte[]
        {
            0x85, 0xC9,                   // 0x1000 test ecx, ecx
            0x74, 0x05,                   // 0x1002 je 0x1009
            0xBF, 0x07, 0x00, 0x00, 0x00, // 0x1004 mov edi, 7
            0x89, 0xF8,                   // 0x1009 mov eax, edi
            0xC3,                         // 0x100b ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("return edi;", text);
        Assert.DoesNotContain("return 7;", text);
    }

    [Fact]
    public void ACallResultNobodyReadsIsDropped()
    {
        var code = new byte[]
        {
            0xE8, 0x05, 0x00, 0x00, 0x00, // 0x1000 call 0x100a
            0x31, 0xC0,                   // 0x1005 xor eax, eax
            0xC3,                         // 0x1007 ret
            0xCC, 0xCC,                   // padding
            0xC3,                         // 0x100a ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("sub_100A();", text);
        Assert.DoesNotContain("= sub_100A()", text);   // the result is overwritten before anyone reads it
        Assert.Contains("return 0;", text);
    }

    [Fact]
    public void AnUnresolvedIndirectJumpKeepsEverythingLive()
    {
        // Nothing is known about what runs after `jmp eax`, so no assignment before it can be called dead.
        var code = new byte[]
        {
            0xBF, 0x05, 0x00, 0x00, 0x00, // 0x1000 mov edi, 5
            0xFF, 0xE0,                   // 0x1005 jmp eax
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("edi = 5;", text);
    }

    [Fact]
    public void AFrameSlotReadReachesTheNextBlock()
    {
        // push ebp ; mov ebp,esp ; mov esi,[ebp+8] ; test ecx,ecx ; je ... ; mov eax,esi ; ret
        var code = new byte[]
        {
            0x55,                         // 0x1000 push ebp
            0x8B, 0xEC,                   // 0x1001 mov ebp, esp
            0x8B, 0x75, 0x08,             // 0x1003 mov esi, [ebp+8]
            0x85, 0xC9,                   // 0x1006 test ecx, ecx
            0x74, 0x04,                   // 0x1008 je 0x100e
            0x8B, 0xC6,                   // 0x100a mov eax, esi
            0x5D,                         // 0x100c pop ebp
            0xC3,                         // 0x100d ret
            0x31, 0xC0,                   // 0x100e xor eax, eax
            0x5D,                         // 0x1010 pop ebp
            0xC3,                         // 0x1011 ret
        };

        string text = Decompile(code, 0x1000, 32);

        Assert.Contains("return arg_0;", text);
    }
}
