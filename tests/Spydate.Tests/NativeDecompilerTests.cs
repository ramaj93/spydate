using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Decompiler.Native.IR;
using Spydate.Disassembly;

namespace Spydate.Tests;

public class NativeDecompilerTests
{
    private static DecompiledFunction Decompile(byte[] code, ulong baseVa, int bitness, SymbolTable? symbols = null)
    {
        symbols ??= new SymbolTable();
        var source = new MemoryCodeSource(code, baseVa, bitness);
        var dis = new X86Disassembler(bitness, symbols);
        var f = new FunctionDiscovery(source, dis, symbols).Discover(baseVa);
        return new NativeDecompiler(bitness, symbols).Decompile(f);
    }

    [Fact]
    public void ReturnsConstant()
    {
        // mov eax, 1 ; ret
        var r = Decompile(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }, 0x1000, 32);

        Assert.Contains("return 1;", r.Text);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void XorZeroIdiomBecomesZero()
    {
        // xor eax, eax ; ret
        var r = Decompile(new byte[] { 0x31, 0xC0, 0xC3 }, 0x1000, 32);

        Assert.Contains("return 0;", r.Text);
    }

    [Fact]
    public void FramePointerArgumentIsNamedAndPropagated()
    {
        // push ebp ; mov ebp, esp ; mov eax, [ebp+8] ; add eax, 5 ; pop ebp ; ret
        var code = new byte[] { 0x55, 0x8B, 0xEC, 0x8B, 0x45, 0x08, 0x83, 0xC0, 0x05, 0x5D, 0xC3 };
        var r = Decompile(code, 0x401000, 32);

        Assert.Contains("arg_0", r.Text);
        Assert.Contains("return arg_0 + 5;", r.Text);
        Assert.Contains("sub_401000(uint32_t arg_0)", r.Text);
    }

    [Fact]
    public void ConditionalBranchProducesIfElse()
    {
        // cmp ecx, 10 ; jl +5 ; mov eax, 1 ; ret ; (0x100b) mov eax, 2 ; ret
        var code = new byte[]
        {
            0x83, 0xF9, 0x0A,             // 0x1000 cmp ecx, 0xa
            0x7C, 0x06,                   // 0x1003 jl 0x100b
            0xB8, 0x01, 0x00, 0x00, 0x00, // 0x1005 mov eax, 1
            0xC3,                         // 0x100a ret
            0xB8, 0x02, 0x00, 0x00, 0x00, // 0x100b mov eax, 2
            0xC3,                         // 0x1010 ret
        };
        var r = Decompile(code, 0x1000, 32);

        Assert.True(r.Text.Contains("if ((int32_t)ecx < 10)"), r.Text);
        Assert.Contains("return 1;", r.Text);
        Assert.Contains("return 2;", r.Text);
        Assert.DoesNotContain("goto", r.Text);
    }

    [Fact]
    public void CallThroughImportSlotIsNamed()
    {
        // 0x140001000: call [rip+0x100] ; ret     (slot at 0x140001106)
        var code = new byte[] { 0xFF, 0x15, 0x00, 0x01, 0x00, 0x00, 0xC3 };
        var symbols = new SymbolTable();
        symbols.Add(new Symbol(0x140001106, "kernel32!GetTickCount", SymbolKind.Import));
        var r = Decompile(code, 0x140001000, 64, symbols);

        Assert.True(r.Text.Contains("return kernel32!GetTickCount();"), r.Text);
    }

    [Fact]
    public void UnsupportedInstructionIsKeptAsAsm()
    {
        // cpuid ; ret
        var r = Decompile(new byte[] { 0x0F, 0xA2, 0xC3 }, 0x1000, 64);

        Assert.Contains("__asm { cpuid }", r.Text);
        Assert.Contains(r.Warnings, w => w.Contains("cpuid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LiftedIrHasOneBlockPerBasicBlock()
    {
        var code = new byte[] { 0x85, 0xC0, 0x75, 0x05, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 };
        var r = Decompile(code, 0x1000, 32);

        Assert.Equal(3, r.Ir.Blocks.Count);
        Assert.Contains(r.Ir.AllStatements, s => s is IrBranch);
        Assert.Contains(0x1009UL, r.Ir.LabelTargets);
    }

    [Fact]
    public void Win64CallArgumentsAreRecovered()
    {
        // sub rsp, 28h ; xor r8d, r8d ; lea edx, [r8+2] ; xor ecx, ecx ; call +0 ; add rsp, 28h ; ret
        var code = new byte[]
        {
            0x48, 0x83, 0xEC, 0x28,       // 140001000 sub rsp, 0x28
            0x45, 0x33, 0xC0,             // 140001004 xor r8d, r8d
            0x41, 0x8D, 0x50, 0x02,       // 140001007 lea edx, [r8+2]
            0x33, 0xC9,                   // 14000100B xor ecx, ecx
            0xE8, 0x00, 0x00, 0x00, 0x00, // 14000100D call 140001012
            0x48, 0x83, 0xC4, 0x28,       // 140001012 add rsp, 0x28
            0xC3,                         // 140001016 ret
        };
        var r = Decompile(code, 0x140001000, 64);

        // r8 is still read (as the 64-bit register) by the lea, so its definition must survive.
        Assert.True(r.Text.Contains("r8d = 0;"), r.Text);
        Assert.True(r.Text.Contains("sub_140001012(0, r8 + 2, 0)"), r.Text);
        Assert.DoesNotContain("rsp", r.Text);
    }

    [Fact]
    public void StackSlotArgumentsBeforeCallAreKept()
    {
        // sub rsp, 38h ; mov dword [rsp+20h], 4 ; xor ecx, ecx ; call +0 ; add rsp, 38h ; ret
        var code = new byte[]
        {
            0x48, 0x83, 0xEC, 0x38,                   // 140001000
            0xC7, 0x44, 0x24, 0x20, 0x04, 0x00, 0x00, 0x00, // 140001004 mov dword [rsp+0x20], 4  (5th arg slot)
            0x33, 0xC9,                               // 14000100C xor ecx, ecx
            0xE8, 0x00, 0x00, 0x00, 0x00,             // 14000100E call 140001013
            0x48, 0x83, 0xC4, 0x38,                   // 140001013
            0xC3,                                     // 140001017
        };
        var r = Decompile(code, 0x140001000, 64);

        Assert.True(r.Text.Contains("local_18 = 4;"), r.Text);
        Assert.True(r.Text.Contains("sub_140001013(0)"), r.Text);
    }

    [Fact]
    public void FramePointerAliasInR11IsTracked()
    {
        // mov r11, rsp ; sub rsp, 28h ; mov [r11-8], rcx ; add rsp, 28h ; ret
        var code = new byte[]
        {
            0x4C, 0x8B, 0xDC,             // 140001000 mov r11, rsp
            0x48, 0x83, 0xEC, 0x28,       // 140001003 sub rsp, 0x28
            0x49, 0x89, 0x4B, 0xF8,       // 140001007 mov [r11-8], rcx
            0x48, 0x83, 0xC4, 0x28,       // 14000100B add rsp, 0x28
            0xC3,                         // 14000100F ret
        };
        var r = Decompile(code, 0x140001000, 64);

        Assert.True(r.Text.Contains("local_8 = rcx;"), r.Text);
        Assert.DoesNotContain("r11", r.Text);
    }

    [Fact]
    public void X86StdcallEntryWithSavedFramePointer()
    {
        // kernel32!DllEntryPoint (x86):
        // mov edi,edi ; push ebp ; mov ebp,esp ; cmp [ebp+0xc],1 ; jne +0x0d ; push [ebp+0x10] ; push 1 ; push [ebp+8] ;
        // call +0 ; mov edx,[ebp+0xc] ; push ecx ; mov ecx,[ebp+8] ; call +0 ; movzx eax,al ; pop ebp ; ret 0xc
        var code = new byte[]
        {
            0x8B, 0xFF,                         // 1000 mov edi, edi
            0x55,                               // 1002 push ebp
            0x8B, 0xEC,                         // 1003 mov ebp, esp
            0x83, 0x7D, 0x0C, 0x01,             // 1005 cmp dword [ebp+0xc], 1
            0x75, 0x0D,                         // 1009 jne 1018
            0xFF, 0x75, 0x10,                   // 100B push [ebp+0x10]
            0x6A, 0x01,                         // 100E push 1
            0xFF, 0x75, 0x08,                   // 1010 push [ebp+8]
            0xE8, 0x00, 0x00, 0x00, 0x00,       // 1013 call 1018
            0x8B, 0x55, 0x0C,                   // 1018 mov edx, [ebp+0xc]
            0x51,                               // 101B push ecx
            0x8B, 0x4D, 0x08,                   // 101C mov ecx, [ebp+8]
            0xE8, 0x00, 0x00, 0x00, 0x00,       // 101F call 1024
            0x0F, 0xB6, 0xC0,                   // 1024 movzx eax, al
            0x5D,                               // 1027 pop ebp
            0xC2, 0x0C, 0x00,                   // 1028 ret 0xc
        };
        var r = Decompile(code, 0x1000, 32);

        Assert.True(r.Text.Contains("sub_1018(arg_0, 1, arg_8)"), r.Text);
        Assert.True(!r.Text.Contains("local_4 = ebp"), r.Text);   // push/pop ebp pair elided
        Assert.True(r.Text.Contains("edx = arg_4;"), r.Text);      // possible fastcall arg: keep visible on x86
        Assert.True(!r.Text.Contains("edi = edi"), r.Text);
        Assert.True(!r.Text.Contains("uint32_t local_C;"), r.Text); // consumed push slots are not declared
    }

    [Fact]
    public void X86PushedArgumentsAreRecovered()
    {
        // push 5 ; push 7 ; call +0 ; add esp, 8 ; ret
        var code = new byte[]
        {
            0x6A, 0x05,                   // 401000 push 5
            0x6A, 0x07,                   // 401002 push 7
            0xE8, 0x00, 0x00, 0x00, 0x00, // 401004 call 401009
            0x83, 0xC4, 0x08,             // 401009 add esp, 8
            0xC3,                         // 40100C ret
        };
        var r = Decompile(code, 0x401000, 32);

        Assert.True(r.Text.Contains("sub_401009(7, 5)"), r.Text);
        Assert.DoesNotContain("esp", r.Text);
    }

    [Fact]
    public void Win64PrologueWithStackLocals()
    {
        // mov [rsp+8], rcx ; sub rsp, 0x28 ; mov rax, [rsp+0x30] ; add rsp, 0x28 ; ret
        var code = new byte[]
        {
            0x48, 0x89, 0x4C, 0x24, 0x08,
            0x48, 0x83, 0xEC, 0x28,
            0x48, 0x8B, 0x44, 0x24, 0x30,
            0x48, 0x83, 0xC4, 0x28,
            0xC3,
        };
        var r = Decompile(code, 0x140001000, 64);

        Assert.True(r.Text.Contains("return rcx;"), r.Text);
        Assert.Contains("(uint64_t arg_0)", r.Text);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void AFunctionThatNeverSetsTheAccumulatorIsVoid()
    {
        // mov dword [ecx], 1 ; ret — `ret` returns whatever the caller left in eax, which is nothing.
        var r = Decompile(new byte[] { 0xC7, 0x01, 0x01, 0x00, 0x00, 0x00, 0xC3 }, 0x1000, 32);

        Assert.Contains("void sub_1000(", r.Text);
        Assert.DoesNotContain("return eax", r.Text);
        Assert.DoesNotContain("return rax", r.Text);
    }

    [Fact]
    public void AFunctionThatSetsTheAccumulatorKeepsItsResult()
    {
        var r = Decompile(new byte[] { 0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3 }, 0x1000, 32);

        Assert.Contains("uint32_t sub_1000(", r.Text);
        Assert.Contains("return 42;", r.Text);
    }

    [Fact]
    public void ATailCallIsAValue()
    {
        // jmp [rip+0x100] — the callee's result is this function's result, so it is not void.
        var symbols = new SymbolTable();
        symbols.Add(new Symbol(0x140001106, "kernel32!GetTickCount", SymbolKind.Import));
        var r = Decompile(new byte[] { 0xFF, 0x25, 0x00, 0x01, 0x00, 0x00 }, 0x140001000, 64, symbols);

        Assert.Contains("return kernel32!GetTickCount();", r.Text);
        Assert.DoesNotContain("void ", r.Text);
    }
}
