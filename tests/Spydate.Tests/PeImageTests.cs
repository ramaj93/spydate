using Spydate.Core.PE;
using Spydate.Core.Symbols;

namespace Spydate.Tests;

public class PeImageTests
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string Kernel32 = Path.Combine(System32, "kernel32.dll");
    private static readonly string Notepad = Path.Combine(System32, "notepad.exe");

    [SkippableFact]
    public void Kernel32_ParsesHeadersSectionsExportsImports()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");

        var pe = PeImage.Load(Kernel32);

        Assert.True(pe.IsDll);
        Assert.False(pe.IsManaged);
        Assert.Contains(pe.Machine, new[] { MachineType.Amd64, MachineType.I386, MachineType.Arm64 });
        Assert.NotEmpty(pe.Sections);
        Assert.Contains(pe.Sections, s => s.Name == ".text" && s.IsExecutable);
        Assert.NotNull(pe.Exports);
        Assert.Contains(pe.Exports!.Entries, e => e.Name == "CreateFileW");
        Assert.NotEmpty(pe.Imports);
        Assert.Contains(pe.Imports, m => m.Name.StartsWith("ntdll", StringComparison.OrdinalIgnoreCase) || m.Name.StartsWith("api-ms", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pe.Debug, d => d.CodeView is { } cv && cv.PdbPath.Contains("kernel32", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(pe.Warnings);
    }

    [SkippableFact]
    public void Kernel32_EntryPointMapsIntoExecutableSection()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");
        var pe = PeImage.Load(Kernel32);

        var section = pe.SectionFromRva(pe.EntryPointRva);
        Assert.NotNull(section);
        Assert.True(section!.IsExecutable);
        Assert.NotNull(pe.RvaToOffset(pe.EntryPointRva));
        var bytes = pe.ReadAtRva(pe.EntryPointRva, 16);
        Assert.Equal(16, bytes.Length);
    }

    [SkippableFact]
    public void Kernel32_SymbolTableContainsExportsAndImports()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");
        var pe = PeImage.Load(Kernel32);

        var symbols = SymbolTable.FromImage(pe);
        Assert.NotNull(symbols.GetByName("CreateFileW"));
        Assert.Contains(symbols.All, s => s.Kind == SymbolKind.Import && s.Name.Contains('!'));
        Assert.Contains(symbols.All, s => s.Kind == SymbolKind.EntryPoint);
    }

    [SkippableFact]
    public void Kernel32_X64HasExceptionTable()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");
        var pe = PeImage.Load(Kernel32);
        Skip.IfNot(pe.Machine == MachineType.Amd64, "not x64");

        Assert.NotEmpty(pe.ExceptionTable);
        Assert.All(pe.ExceptionTable, rf => Assert.True(rf.EndRva > rf.BeginRva));
        Assert.Contains(pe.ExceptionTable, rf => pe.SectionFromRva(rf.BeginRva)?.IsExecutable == true);
        // The DLL entry point has a frame and therefore unwind info; leaf thunks (e.g. CreateFileW) need not.
        Assert.Contains(pe.ExceptionTable, rf => rf.BeginRva <= pe.EntryPointRva && pe.EntryPointRva < rf.EndRva);
    }

    [SkippableFact]
    public void Notepad_IsGuiExecutable()
    {
        Skip.IfNot(File.Exists(Notepad), "notepad.exe not found");
        var pe = PeImage.Load(Notepad);

        Assert.False(pe.IsDll);
        Assert.Equal(Subsystem.WindowsGui, pe.Subsystem);
        Assert.NotEqual(0u, pe.EntryPointRva);
        Assert.NotEmpty(pe.Imports);
    }

    [Fact]
    public void ManagedAssembly_HasClrHeader()
    {
        var pe = PeImage.Load(typeof(PeImage).Assembly.Location);

        Assert.True(pe.IsManaged);
        Assert.NotNull(pe.ClrHeader);
        Assert.True(pe.ClrHeader!.MetaData.IsPresent);
        Assert.True(pe.ClrHeader.IsILOnly);
        Assert.Contains(pe.Sections, s => s.Name == ".text");
    }

    [Fact]
    public void RoundTrip_RvaOffsetVa()
    {
        var pe = PeImage.Load(typeof(PeImage).Assembly.Location);
        var text = pe.Sections.First(s => s.Name == ".text");
        uint rva = text.VirtualAddress + 0x10;

        uint? offset = pe.RvaToOffset(rva);
        Assert.NotNull(offset);
        Assert.Equal(rva, pe.OffsetToRva(offset!.Value));
        Assert.Equal(rva, pe.VaToRva(pe.RvaToVa(rva)));
        Assert.Null(pe.VaToRva(pe.ImageBase - 1));
    }

    [Fact]
    public void Garbage_ThrowsPeParseException()
    {
        var garbage = new byte[1024];
        new Random(42).NextBytes(garbage);
        Assert.Throws<PeParseException>(() => PeImage.Parse(garbage));
        Assert.False(PeImage.LooksLikePe(garbage));
    }

    [Fact]
    public void Truncated_ThrowsPeParseException()
    {
        var full = File.ReadAllBytes(typeof(PeImage).Assembly.Location);
        Assert.True(PeImage.LooksLikePe(full));

        var truncated = full.AsMemory(0, 0x80);
        Assert.Throws<PeParseException>(() => PeImage.Parse(truncated));
    }

    [Fact]
    public void BadLfanew_ThrowsPeParseException()
    {
        var full = File.ReadAllBytes(typeof(PeImage).Assembly.Location);
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(full, 0x3C);
        Assert.Throws<PeParseException>(() => PeImage.Parse(full));
    }

    [Fact]
    public void CorruptImportDirectory_IsWarningNotCrash()
    {
        var full = File.ReadAllBytes(typeof(PeImage).Assembly.Location);
        var pe = PeImage.Parse(full);
        // Point the import directory at an unmapped RVA and re-parse.
        int dirOffset = (int)pe.NtHeadersOffset + 4 + CoffFileHeader.Size + (pe.Is64Bit ? 112 : 96) + 8 * (int)DataDirectoryIndex.Import;
        BitConverter.GetBytes(0x7FFF0000u).CopyTo(full, dirOffset);
        BitConverter.GetBytes(0x100u).CopyTo(full, dirOffset + 4);

        var corrupt = PeImage.Parse(full);
        Assert.Empty(corrupt.Imports);
        Assert.Contains(corrupt.Warnings, w => w.Contains("import", StringComparison.OrdinalIgnoreCase));
    }
}
