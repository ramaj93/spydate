using System.Text;
using Spydate.Core.PE;
using Spydate.Core.Strings;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Decompiler.Native.Passes;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>Naming the addresses that turn up in lifted code: globals, function pointers, string literals.</summary>
public class GlobalNamingTests
{
    private const ulong DataVa = 0x140001000;   // the synthetic image's only section
    private const ulong CodeVa = 0x140002000;   // where the test code is decoded from

    private static string Decompile(byte[] code, PeImage image, SymbolTable? symbols = null, bool isFunction = false)
    {
        symbols ??= new SymbolTable();
        var source = new MemoryCodeSource(code, CodeVa, 64);
        var dis = new X86Disassembler(64, symbols);
        var function = new FunctionDiscovery(source, dis, symbols).Discover(CodeVa);
        var names = new GlobalNames(
            image,
            symbols,
            () => StringIndex.Build(StringScanner.Scan(image)),
            va => isFunction && va == DataVa);
        return new NativeDecompiler(64, symbols, names: names).Decompile(function).Text;
    }

    /// <summary>rip-relative displacement that makes an instruction of the given length reach the data section.</summary>
    private static byte[] RipRelative(byte[] opcode, int totalLength)
    {
        int displacement = (int)(long)(DataVa - (CodeVa + (ulong)totalLength));
        return opcode.Concat(BitConverter.GetBytes(displacement)).ToArray();
    }

    [Fact]
    public void AbsoluteReadBecomesANamedGlobal()
    {
        // mov rax, [rip+disp] ; ret
        var image = SyntheticPe.WithDataSection(new byte[] { 1, 2, 3, 4 });
        var code = RipRelative(new byte[] { 0x48, 0x8B, 0x05 }, 7).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image);

        Assert.Contains("return data_140001000;", text);
        Assert.DoesNotContain("0x140001000", text);
    }

    [Fact]
    public void AbsoluteWriteBecomesAnAssignmentToTheName()
    {
        // mov [rip+disp], ecx ; ret
        var image = SyntheticPe.WithDataSection(new byte[] { 0 });
        var code = RipRelative(new byte[] { 0x89, 0x0D }, 6).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image);

        Assert.Contains("data_140001000 = ecx;", text);
    }

    [Fact]
    public void SymbolNamesWinOverTheAddress()
    {
        var image = SyntheticPe.WithDataSection(new byte[] { 0 });
        var symbols = new SymbolTable();
        symbols.Add(new Symbol(DataVa, "g_refCount", SymbolKind.Data));
        var code = RipRelative(new byte[] { 0x48, 0x8B, 0x05 }, 7).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image, symbols);

        Assert.Contains("return g_refCount;", text);
    }

    [Fact]
    public void PointerToDataIsTakenByAddress()
    {
        // lea rax, [rip+disp] ; ret     — the value is the address, so it needs the &
        var image = SyntheticPe.WithDataSection(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        var code = RipRelative(new byte[] { 0x48, 0x8D, 0x05 }, 7).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image);

        Assert.Contains("return &data_140001000;", text);
    }

    [Fact]
    public void PointerToCodeIsJustTheName()
    {
        // A function's name already means its address in C, so no & is added.
        var image = SyntheticPe.WithSectionData(new byte[] { 0 });
        var code = RipRelative(new byte[] { 0x48, 0x8D, 0x05 }, 7).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image, isFunction: true);

        Assert.Contains("return sub_140001000;", text);
        Assert.DoesNotContain("&sub_140001000", text);
    }

    [Fact]
    public void PointerToTextBecomesTheLiteral()
    {
        var image = SyntheticPe.WithDataSection(Encoding.ASCII.GetBytes("cannot open file\0"));
        var code = RipRelative(new byte[] { 0x48, 0x8D, 0x05 }, 7).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image);

        Assert.Contains("return \"cannot open file\";", text);
    }

    [Fact]
    public void Utf16LiteralsKeepTheirPrefix()
    {
        var image = SyntheticPe.WithDataSection(Encoding.Unicode.GetBytes("ntdll.dll\0"));
        var code = RipRelative(new byte[] { 0x48, 0x8D, 0x05 }, 7).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image);

        Assert.Contains("L\"ntdll.dll\"", text);
    }

    [Fact]
    public void TextInsideCodeIsNotALiteral()
    {
        // x64 prologue bytes read as "@SVWH" in ASCII. Naming a call target after them would be worse
        // than useless, so strings are only recognised outside executable sections.
        var image = SyntheticPe.WithSectionData(Encoding.ASCII.GetBytes("@SVWH\0"));
        var code = RipRelative(new byte[] { 0x48, 0x8D, 0x05 }, 7).Append((byte)0xC3).ToArray();

        string text = Decompile(code, image);

        Assert.DoesNotContain("@SVWH", text);
    }

    [Fact]
    public void NumbersThatAreNotAddressesAreLeftAlone()
    {
        // mov eax, 0x1234 ; ret — the immediate is inside no section, so it stays a number.
        var image = SyntheticPe.WithDataSection(new byte[] { 0 });
        var code = new byte[] { 0xB8, 0x34, 0x12, 0x00, 0x00, 0xC3 };

        string text = Decompile(code, image);

        Assert.Contains("eax = 0x1234;", text);
    }

    [Fact]
    public void ACallResultIsNotDuplicatedIntoItsReader()
    {
        // call rax ; mov [rip+disp], rax ; jmp $+2 ; ret
        // The block does not end at the return, so rax is still live when it ends: the call must stay
        // where it is rather than being substituted into the store and printed twice.
        var image = SyntheticPe.WithDataSection(new byte[] { 0 });
        var code = new byte[] { 0xFF, 0xD0 }
            .Concat(RipRelative(new byte[] { 0x48, 0x89, 0x05 }, 9))
            .Concat(new byte[] { 0xEB, 0x00, 0xC3 })
            .ToArray();

        string text = Decompile(code, image);

        Assert.Equal(1, text.Split("rax(").Length - 1);
    }
}
