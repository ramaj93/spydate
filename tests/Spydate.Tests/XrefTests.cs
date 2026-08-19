using Spydate.Core.PE;
using Spydate.Core.Symbols;
using Spydate.Disassembly;

namespace Spydate.Tests;

public class XrefTests
{
    private const ulong Base = 0x140000000;

    private static XrefTable ExtractFrom(byte[] code, ulong entry = Base + 0x1000)
    {
        // A 64 KiB image with the code at +0x1000, so data targets are mapped but not executable.
        var source = new MemoryCodeSource(code, entry, bitness: 64, imageBase: Base, imageSize: 0x10000);
        var symbols = new SymbolTable();
        var disassembler = new X86Disassembler(64, symbols);
        var function = new FunctionDiscovery(source, disassembler, symbols).Discover(entry, "test");
        var table = new XrefTable();
        new XrefExtractor(source).Extract(function, table);
        return table;
    }

    [Fact]
    public void DirectCallProducesCallXref()
    {
        // call +0x10 ; ret   (call at 0x1000 is 5 bytes, so the target is 0x1015)
        var code = new byte[] { 0xE8, 0x10, 0x00, 0x00, 0x00, 0xC3 };
        var table = ExtractFrom(code);

        var xref = Assert.Single(table.To(Base + 0x1015));
        Assert.Equal(Base + 0x1000, xref.FromVa);
        Assert.Equal(XrefKind.Call, xref.Kind);
        Assert.True(xref.IsCode);
    }

    [Fact]
    public void ConditionalBranchProducesJumpXref()
    {
        // test eax, eax ; je +2 ; nop ; nop ; ret
        var code = new byte[] { 0x85, 0xC0, 0x74, 0x02, 0x90, 0x90, 0xC3 };
        var table = ExtractFrom(code);

        var xref = Assert.Single(table.To(Base + 0x1006));
        Assert.Equal(XrefKind.Jump, xref.Kind);
    }

    [Fact]
    public void RipRelativeLoadProducesReadXref()
    {
        // mov eax, [rip+0x100] ; ret   (next instruction is at 0x1006, so the target is 0x1106)
        var code = new byte[] { 0x8B, 0x05, 0x00, 0x01, 0x00, 0x00, 0xC3 };
        var table = ExtractFrom(code);

        var xref = Assert.Single(table.To(Base + 0x1106));
        Assert.Equal(XrefKind.Read, xref.Kind);
        Assert.True(xref.IsData);
    }

    [Fact]
    public void RipRelativeStoreProducesWriteXref()
    {
        // mov [rip+0x100], eax ; ret
        var code = new byte[] { 0x89, 0x05, 0x00, 0x01, 0x00, 0x00, 0xC3 };
        var table = ExtractFrom(code);

        Assert.Equal(XrefKind.Write, Assert.Single(table.To(Base + 0x1106)).Kind);
    }

    [Fact]
    public void LeaProducesOffsetXrefNotRead()
    {
        // lea rax, [rip+0x100] ; ret — the address is taken, never dereferenced.
        var code = new byte[] { 0x48, 0x8D, 0x05, 0x00, 0x01, 0x00, 0x00, 0xC3 };
        var table = ExtractFrom(code);

        Assert.Equal(XrefKind.Offset, Assert.Single(table.To(Base + 0x1107)).Kind);
    }

    [Fact]
    public void IndirectCallThroughSlotProducesIndirectCallXref()
    {
        // call qword ptr [rip+0x100] ; ret
        var code = new byte[] { 0xFF, 0x15, 0x00, 0x01, 0x00, 0x00, 0xC3 };
        var table = ExtractFrom(code);

        var xref = Assert.Single(table.To(Base + 0x1106));
        Assert.Equal(XrefKind.IndirectCall, xref.Kind);
        Assert.True(xref.IsCode);
    }

    [Fact]
    public void AddressSizedImmediateInsideTheImageIsAnOffsetXref()
    {
        // mov rax, 0x140001234 (movabs) ; ret
        var code = new byte[] { 0x48, 0xB8, 0x34, 0x12, 0x00, 0x40, 0x01, 0x00, 0x00, 0x00, 0xC3 };
        var table = ExtractFrom(code);

        Assert.Equal(XrefKind.Offset, Assert.Single(table.To(0x140001234)).Kind);
    }

    [Fact]
    public void ImmediateOutsideTheImageIsNotAnXref()
    {
        // mov eax, 0x12345678 — a plain constant, not an address in this image.
        var code = new byte[] { 0xB8, 0x78, 0x56, 0x34, 0x12, 0xC3 };
        var table = ExtractFrom(code);

        Assert.Empty(table.To(0x12345678));
    }

    [Fact]
    public void DuplicateReferencesAreRecordedOnce()
    {
        var table = new XrefTable();
        var xref = new Xref(0x1000, 0x2000, XrefKind.Call);

        Assert.True(table.Add(xref));
        Assert.False(table.Add(xref));
        Assert.Equal(1, table.Count);
        Assert.Equal(1, table.CountTo(0x2000));

        // A different kind from the same site is a distinct reference.
        Assert.True(table.Add(new Xref(0x1000, 0x2000, XrefKind.Read)));
        Assert.Equal(2, table.CountTo(0x2000));
    }

    [Fact]
    public void BothDirectionsAreIndexed()
    {
        var table = new XrefTable();
        table.Add(new Xref(0x1000, 0x2000, XrefKind.Call));
        table.Add(new Xref(0x1100, 0x2000, XrefKind.Call));
        table.Add(new Xref(0x1000, 0x3000, XrefKind.Read));

        Assert.Equal(2, table.To(0x2000).Count);
        Assert.Equal(2, table.From(0x1000).Count);
        Assert.Empty(table.To(0x9999));
    }

    [SkippableFact]
    public void RealBinaryCollectsCallersForDiscoveredFunctions()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");

        var analysis = new BinaryAnalysis(pe);
        var functions = analysis.DiscoverAll(maxFunctions: 300);

        Assert.True(analysis.Xrefs.Count > functions.Count, "expected more references than functions");

        // Every call target that was itself discovered must know about its caller.
        var caller = functions.First(f => f.CallTargets.Count > 0);
        ulong callee = caller.CallTargets[0];
        Assert.Contains(analysis.Xrefs.To(callee), x => x.Kind == XrefKind.Call && caller.Blocks.Any(b => x.FromVa >= b.StartVa && x.FromVa < b.EndVa));

        // And the reverse lookup finds the enclosing function.
        var resolved = analysis.XrefsTo(callee);
        Assert.Contains(resolved, r => r.From?.EntryVa == caller.EntryVa);
    }
}
