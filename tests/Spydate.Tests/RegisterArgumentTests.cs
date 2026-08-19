using Iced.Intel;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// x86 register arguments. The question "is this __thiscall?" is put to the callee's own code, so these
/// tests are mostly about what does and does not count as being handed something in a register.
/// </summary>
public class RegisterArgumentTests
{
    private static Function Discover(byte[] code, ulong entryVa, ulong baseVa = 0x1000)
    {
        var symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, baseVa, 32);
        var dis = new X86Disassembler(32, symbols);
        return new FunctionDiscovery(source, dis, symbols).Discover(entryVa);
    }

    private static int CountFor(params byte[] code) => RegisterUse.FastcallArgumentCount(Discover(code, 0x1000));

    [Fact]
    public void ReadingEcxFirstIsAThisPointer()
    {
        // mov eax, [ecx] ; ret
        Assert.Equal(1, CountFor(0x8B, 0x01, 0xC3));
    }

    [Fact]
    public void ReadingEcxAndEdxIsFastcall()
    {
        // mov ebx, ecx ; test edx, edx ; ret
        Assert.Equal(2, CountFor(0x8B, 0xD9, 0x85, 0xD2, 0xC3));
    }

    [Fact]
    public void WritingEcxFirstIsNotAnArgument()
    {
        // mov ecx, 5 ; mov eax, [ecx] ; ret
        Assert.Equal(0, CountFor(0xB9, 0x05, 0x00, 0x00, 0x00, 0x8B, 0x01, 0xC3));
    }

    [Fact]
    public void ZeroingEcxIsNotReadingIt()
    {
        // xor ecx, ecx ; mov eax, [ecx] ; ret — the read of ecx here only produces zero.
        Assert.Equal(0, CountFor(0x31, 0xC9, 0x8B, 0x01, 0xC3));
    }

    [Fact]
    public void PushEcxIsStackAllocationNotAnArgument()
    {
        // push ebp ; mov ebp, esp ; push ecx ; ret — MSVC's way of reserving four bytes.
        Assert.Equal(0, CountFor(0x55, 0x8B, 0xEC, 0x51, 0xC3));
    }

    [Fact]
    public void ACallSettlesTheQuestion()
    {
        // call $+5 ; mov eax, [ecx] ; ret — after the call, ecx holds whatever the callee left.
        Assert.Equal(0, CountFor(0xE8, 0x00, 0x00, 0x00, 0x00, 0x8B, 0x01, 0xC3));
    }

    [Fact]
    public void OnlyTheEntryBlockCounts()
    {
        // test eax, eax ; je +2 ; xor eax, eax ; mov eax, [ecx] ; ret
        // The read of ecx is down a later path, which says nothing about the convention.
        Assert.Equal(0, CountFor(0x85, 0xC0, 0x74, 0x02, 0x31, 0xC0, 0x8B, 0x01, 0xC3));
    }

    [Fact]
    public void ReadsBeforeWritingIgnoresUnrelatedRegisters()
    {
        var function = Discover(new byte[] { 0x8B, 0x01, 0xC3 }, 0x1000);

        Assert.True(RegisterUse.ReadsBeforeWriting(function, Register.ECX));
        Assert.False(RegisterUse.ReadsBeforeWriting(function, Register.EDX));
        Assert.False(RegisterUse.ReadsBeforeWriting(function, Register.ESI));
    }

    /// <summary>
    /// mov ecx, 7 ; call callee ; ret, where the callee reads ecx — the value has to reach the call.
    /// </summary>
    [Fact]
    public void ACallToAThiscallFunctionShowsItsRegisterArgument()
    {
        var code = new byte[]
        {
            0xB9, 0x07, 0x00, 0x00, 0x00, // 0x1000 mov ecx, 7
            0xE8, 0x06, 0x00, 0x00, 0x00, // 0x1005 call 0x1010
            0xC3,                         // 0x100a ret
            0xCC, 0xCC, 0xCC, 0xCC, 0xCC, // padding
            0x8B, 0x01,                   // 0x1010 mov eax, [ecx]
            0xC3,                         // 0x1012 ret
        };

        var symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, 0x1000, 32);
        var dis = new X86Disassembler(32, symbols);
        var discovery = new FunctionDiscovery(source, dis, symbols);
        int Oracle(ulong va) => RegisterUse.FastcallArgumentCount(discovery.Discover(va));

        var decompiler = new NativeDecompiler(32, symbols, registerArguments: Oracle);

        string caller = decompiler.Decompile(discovery.Discover(0x1000)).Text;
        Assert.Contains("sub_1010(7)", caller);

        string callee = decompiler.Decompile(discovery.Discover(0x1010)).Text;
        Assert.Contains("sub_1010(uint32_t ecx)", callee);
        Assert.Contains("return *(uint32_t*)ecx;", callee);
    }

    [Fact]
    public void ACallToAFunctionThatIgnoresEcxGetsNoExtraArgument()
    {
        var code = new byte[]
        {
            0xB9, 0x07, 0x00, 0x00, 0x00, // 0x1000 mov ecx, 7
            0xE8, 0x06, 0x00, 0x00, 0x00, // 0x1005 call 0x1010
            0xC3,                         // 0x100a ret
            0xCC, 0xCC, 0xCC, 0xCC, 0xCC,
            0x31, 0xC0,                   // 0x1010 xor eax, eax
            0xC3,                         // 0x1012 ret
        };

        var symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, 0x1000, 32);
        var dis = new X86Disassembler(32, symbols);
        var discovery = new FunctionDiscovery(source, dis, symbols);
        int Oracle(ulong va) => RegisterUse.FastcallArgumentCount(discovery.Discover(va));

        string text = new NativeDecompiler(32, symbols, registerArguments: Oracle).Decompile(discovery.Discover(0x1000)).Text;

        Assert.Contains("sub_1010();", text);
    }
}
