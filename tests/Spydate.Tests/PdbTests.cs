using Spydate.Core.PE;
using Spydate.Core.Pdb;
using Spydate.Core.Symbols;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>Reading native (MSF) PDBs: container, identity, and public symbols.</summary>
public class PdbTests
{
    private readonly ITestOutputHelper _output;

    public PdbTests(ITestOutputHelper output) => _output = output;

    /// <summary>A native PDB that ships with the .NET host pack, if this machine has one.</summary>
    private static string? FindNativePdb()
    {
        string packs = @"C:\Program Files\dotnet\packs";
        if (!Directory.Exists(packs))
        {
            return null;
        }

        foreach (string candidate in Directory.EnumerateFiles(packs, "*.pdb", SearchOption.AllDirectories).Take(200))
        {
            try
            {
                using var stream = File.OpenRead(candidate);
                var head = new byte[32];
                if (stream.Read(head, 0, head.Length) == head.Length && MsfFile.LooksLikeMsf(head))
                {
                    return candidate;
                }
            }
            catch (IOException)
            {
                // Unreadable file: keep looking.
            }
        }

        return null;
    }

    [SkippableFact]
    public void NativePdbExposesItsIdentityAndPublicSymbols()
    {
        string? path = FindNativePdb();
        Skip.If(path is null, "no native (MSF) PDB on this machine");

        var pdb = PdbFile.Load(path!);
        _output.WriteLine($"{Path.GetFileName(path)}: guid {pdb.Guid}, age {pdb.Age}, {pdb.PublicSymbols.Count} public symbols");
        _output.WriteLine("  " + string.Join("\n  ", pdb.PublicSymbols.Take(6).Select(s => s.ToString())));

        Assert.NotEqual(Guid.Empty, pdb.Guid);
        Assert.True(pdb.Age > 0);

        // A PDB built for a static library has types but no DBI stream, so public symbols may be
        // absent; whatever is there must still be well formed.
        Assert.All(pdb.PublicSymbols, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.True(s.Segment > 0, "segment indices are 1-based");
            Assert.DoesNotContain('\0', s.Name);
        });
    }

    [SkippableFact]
    public void PdbIdentityIsComparedAgainstTheImageCodeViewRecord()
    {
        string? path = FindNativePdb();
        Skip.If(path is null, "no native (MSF) PDB on this machine");

        var pdb = PdbFile.Load(path!);

        // kernel32's CodeView record names a different PDB, so the match must fail: this is the
        // check that stops symbols from one build being applied to another.
        string kernel32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Skip.IfNot(File.Exists(kernel32), "kernel32.dll not found");

        var codeView = PeImage.Load(kernel32).Debug.Select(d => d.CodeView).FirstOrDefault(cv => cv is not null);
        Skip.If(codeView is null, "kernel32 has no CodeView record");

        Assert.False(pdb.Matches(codeView!));
    }

    [SkippableFact]
    public void ProbePathsStartWithTheRecordedBuildPath()
    {
        string kernel32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Skip.IfNot(File.Exists(kernel32), "kernel32.dll not found");

        var image = PeImage.Load(kernel32);
        var probes = PdbFile.ProbePaths(image).ToList();

        Assert.NotEmpty(probes);
        Assert.Contains(probes, p => p.EndsWith("kernel32.pdb", StringComparison.OrdinalIgnoreCase));
        // The build-time path comes first, then the copy next to the image.
        Assert.Equal(image.Debug.Select(d => d.CodeView).First(cv => cv is not null)!.PdbPath, probes[0]);
    }

    [Fact]
    public void PortablePdbIsRejectedWithAClearMessage()
    {
        // .NET assemblies ship portable PDBs, which are a different format entirely.
        var portable = "BSJB"u8.ToArray().Concat(new byte[64]).ToArray();

        var ex = Assert.Throws<PdbParseException>(() => PdbFile.Parse(portable));
        Assert.Contains("portable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(MsfFile.LooksLikeMsf(portable));
    }

    [Fact]
    public void GarbageIsRejected()
    {
        var random = new byte[4096];
        new Random(11).NextBytes(random);

        Assert.Throws<PdbParseException>(() => PdbFile.Parse(random));
        Assert.Null(PdbFile.TryLoad(Path.GetTempFileName(), out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TruncatedContainerIsRejectedRatherThanRead()
    {
        // A valid signature, then a superblock claiming far more blocks than the file holds.
        var data = new byte[512];
        MsfFile.Signature.CopyTo(data);
        int header = MsfFile.Signature.Length;
        BitConverter.GetBytes(4096u).CopyTo(data, header);      // block size
        BitConverter.GetBytes(2u).CopyTo(data, header + 4);         // free block map
        BitConverter.GetBytes(100_000u).CopyTo(data, header + 8);   // block count
        BitConverter.GetBytes(64u).CopyTo(data, header + 12);        // directory size
        BitConverter.GetBytes(3u).CopyTo(data, header + 20);         // block map address

        var ex = Assert.Throws<PdbParseException>(() => PdbFile.Parse(data));
        Assert.Contains("block", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicSymbolsAreReadFromTheRecordStream()
    {
        var guid = Guid.NewGuid();
        var pdb = PdbFile.Parse(SyntheticPdb.Build(guid, age: 7, new[]
        {
            new SyntheticPdb.Public("?Compute@@YAHH@Z", 1, 0x1230, IsFunction: true),
            new SyntheticPdb.Public("g_counter", 3, 0x40, IsFunction: false),
            new SyntheticPdb.Public("main", 1, 0x2000, IsFunction: true),
        }));

        Assert.Equal(guid, pdb.Guid);
        Assert.Equal(7u, pdb.Age);
        Assert.Equal(3, pdb.PublicSymbols.Count);

        var compute = pdb.PublicSymbols[0];
        Assert.Equal("?Compute@@YAHH@Z", compute.Name);
        Assert.Equal(1, compute.Segment);
        Assert.Equal(0x1230u, compute.Offset);
        Assert.True(compute.IsFunction);

        var data = pdb.PublicSymbols[1];
        Assert.Equal("g_counter", data.Name);
        Assert.Equal(3, data.Segment);
        Assert.False(data.IsFunction);
    }

    [Fact]
    public void RecordsOfOtherKindsAreSkipped()
    {
        // An S_OBJNAME (0x1101) between two publics must not derail the walk.
        var records = new List<byte>();
        void Record(ushort kind, byte[] payload)
        {
            int length = 2 + payload.Length;
            int padding = (4 - ((length + 2) % 4)) % 4;
            records.AddRange(BitConverter.GetBytes((ushort)(length + padding)));
            records.AddRange(BitConverter.GetBytes(kind));
            records.AddRange(payload);
            records.AddRange(new byte[padding]);
        }

        var pub = new List<byte>();
        pub.AddRange(BitConverter.GetBytes(2u));        // flags: function
        pub.AddRange(BitConverter.GetBytes(0x500u));    // offset
        pub.AddRange(BitConverter.GetBytes((ushort)2)); // segment
        pub.AddRange(System.Text.Encoding.UTF8.GetBytes("only_one\0"));

        Record(0x1101, System.Text.Encoding.UTF8.GetBytes("obj.obj\0"));
        Record(0x110E, pub.ToArray());

        var pdb = PdbFile.Parse(SyntheticPdb.BuildWithRecords(Guid.NewGuid(), 1, records.ToArray()));

        var symbol = Assert.Single(pdb.PublicSymbols);
        Assert.Equal("only_one", symbol.Name);
        Assert.Equal(2, symbol.Segment);
    }

    [Fact]
    public void ZeroLengthRecordEndsTheWalkInsteadOfLooping()
    {
        // A length of 0 would leave the cursor where it is; the reader must stop.
        var records = new byte[] { 0x00, 0x00, 0x0E, 0x11, 0, 0, 0, 0 };

        var pdb = PdbFile.Parse(SyntheticPdb.BuildWithRecords(Guid.NewGuid(), 1, records));

        Assert.Empty(pdb.PublicSymbols);
    }

    [Fact]
    public void MissingDbiStreamMeansNoSymbolsRatherThanAnError()
    {
        // Static-library PDBs carry types but no DBI stream; the identity is still readable.
        var guid = Guid.NewGuid();
        var pdb = PdbFile.Parse(SyntheticPdb.Build(guid, age: 3, Array.Empty<SyntheticPdb.Public>(), includeDbi: false));

        Assert.Equal(guid, pdb.Guid);
        Assert.Equal(3u, pdb.Age);
        Assert.Empty(pdb.PublicSymbols);
    }

    [Fact]
    public void PublicsAreMappedThroughTheSectionTable()
    {
        // The synthetic image has one section at RVA 0x1000; segment 1 offset 0x40 is 0x1040.
        var image = SyntheticPe.WithSectionData(new byte[] { 0x90 });
        var pdb = PdbFile.Parse(SyntheticPdb.Build(Guid.NewGuid(), 1, new[]
        {
            new SyntheticPdb.Public("compute", 1, 0x40, IsFunction: true),
            new SyntheticPdb.Public("table", 1, 0x80, IsFunction: false),
        }));

        var symbols = new SymbolTable();
        int added = PdbSymbols.Apply(image, pdb, symbols);

        Assert.Equal(2, added);
        Assert.True(symbols.TryGet(image.RvaToVa(0x1040), out var function));
        Assert.Equal("compute", function.Name);
        Assert.Equal(SymbolKind.Function, function.Kind);

        Assert.True(symbols.TryGet(image.RvaToVa(0x1080), out var data));
        Assert.Equal(SymbolKind.Data, data.Kind);
    }

    [Fact]
    public void SymbolsOutsideTheirSectionAreSkipped()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0x90 });
        var pdb = PdbFile.Parse(SyntheticPdb.Build(Guid.NewGuid(), 1, new[]
        {
            new SyntheticPdb.Public("way_past_the_end", 1, 0x9999, IsFunction: true),
            new SyntheticPdb.Public("no_such_section", 9, 0x10, IsFunction: true),
        }));

        Assert.Equal(0, PdbSymbols.Apply(image, pdb, new SymbolTable()));
    }

    [Fact]
    public void ExistingNamesAreKept()
    {
        // An export name is the undecorated one a reader expects; a PDB public is decorated.
        var image = SyntheticPe.WithSectionData(new byte[] { 0x90 });
        var symbols = new SymbolTable();
        symbols.Add(new Symbol(image.RvaToVa(0x1040), "Compute", SymbolKind.Function));

        var pdb = PdbFile.Parse(SyntheticPdb.Build(Guid.NewGuid(), 1, new[]
        {
            new SyntheticPdb.Public("?Compute@@YAHH@Z", 1, 0x40, IsFunction: true),
        }));

        Assert.Equal(0, PdbSymbols.Apply(image, pdb, symbols));
        Assert.True(symbols.TryGet(image.RvaToVa(0x1040), out var kept));
        Assert.Equal("Compute", kept.Name);
    }

    [SkippableFact]
    public void MissingPdbIsReportedWithTheNameItLookedFor()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var image = PeImage.Load(path);
        var result = PdbSymbols.TryLoadFor(image, new SymbolTable());

        // Windows binaries ship without their PDBs; the failure has to say which file was wanted.
        Assert.False(result.Loaded);
        Assert.Contains("kernel32.pdb", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.SymbolsAdded);
    }

    [Fact]
    public void ImageWithoutDebugRecordReportsThat()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0x90 });

        var result = PdbSymbols.TryLoadFor(image, new SymbolTable());

        Assert.False(result.Loaded);
        Assert.Contains("CodeView", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModuleStreamProceduresCarryNamesAndSizes()
    {
        // Publics only cover externally visible symbols; procedures include file-local ones.
        var pdb = PdbFile.Parse(SyntheticPdb.BuildWithModule(Guid.NewGuid(), 1, new[]
        {
            new SyntheticPdb.Procedure("PublicEntry", 1, 0x100, 0x80, IsGlobal: true),
            new SyntheticPdb.Procedure("static_helper", 1, 0x180, 0x40, IsGlobal: false),
        }));

        Assert.Equal(2, pdb.Functions.Count);

        var global = pdb.Functions[0];
        Assert.Equal("PublicEntry", global.Name);
        Assert.Equal(0x100u, global.Offset);
        Assert.Equal(0x80u, global.CodeSize);
        Assert.True(global.IsGlobal);

        var local = pdb.Functions[1];
        Assert.Equal("static_helper", local.Name);
        Assert.Equal(0x40u, local.CodeSize);
        Assert.False(local.IsGlobal);

        // Nothing in the public record stream, so publics stay empty.
        Assert.Empty(pdb.PublicSymbols);
    }

    [Fact]
    public void ProcedureSizesReachTheSymbolTable()
    {
        var image = SyntheticPe.WithSectionData(new byte[] { 0x90 });
        var pdb = PdbFile.Parse(SyntheticPdb.BuildWithModule(Guid.NewGuid(), 1, new[]
        {
            new SyntheticPdb.Procedure("static_helper", 1, 0x60, 0x30, IsGlobal: false),
        }));

        var symbols = new SymbolTable();
        int added = PdbSymbols.Apply(image, pdb, symbols);

        Assert.Equal(1, added);
        Assert.True(symbols.TryGet(image.RvaToVa(0x1060), out var symbol));
        Assert.Equal("static_helper", symbol.Name);
        Assert.Equal(SymbolKind.Function, symbol.Kind);
        Assert.Equal(0x30u, symbol.Size);
    }

    [Fact]
    public void ModulesWithoutASymbolStreamAreSkipped()
    {
        // A module entry with no stream index must not stop the walk or invent symbols.
        var pdb = PdbFile.Parse(SyntheticPdb.Build(Guid.NewGuid(), 1, Array.Empty<SyntheticPdb.Public>()));

        Assert.Empty(pdb.Functions);
    }
}
