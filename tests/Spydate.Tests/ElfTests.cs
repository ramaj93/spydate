using System.Buffers.Binary;
using Spydate.Core.Binary;
using Spydate.Core.Elf;
using Spydate.Core.Project;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;
using Spydate.Mcp;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// ELF end to end: the parser on hand-built files (a Windows machine has no system ELF to lean on), the parser
/// on hostile ones, the analysis and the decompiler under the System V calling convention, the project file
/// keyed on an ELF's own identity, and the MCP surface. Real Linux binaries shipped with WSL are used where the
/// machine has them, and the tests pass vacuously where it does not.
/// </summary>
public sealed class ElfTests : IDisposable
{
    private const int StartOffset = 0x00;
    private const int MainOffset = 0x40;
    private const int AddOffset = 0x80;

    /// <summary>A real x64 program with PLT imports, shipped with WSL's NVIDIA support.</summary>
    private const string NvidiaSmi = @"C:\Windows\System32\lxss\lib\nvidia-smi";

    /// <summary>A real x64 shared object with a full .symtab.</summary>
    private const string NvidiaMl = @"C:\Windows\System32\lxss\lib\libnvidia-ml.so.1";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spydate-elf-" + Guid.NewGuid().ToString("N")[..8]);

    public ElfTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not go away is not a test failure.
        }
    }

    /// <summary>
    /// A glibc-shaped x64 program: <c>_start</c> hands <c>main</c> to <c>__libc_start_main</c>; <c>main</c> calls
    /// <c>add(2, 3)</c> and <c>puts("hello from elf")</c> through the PLT; <c>add</c> returns <c>rdi + rsi</c>.
    /// </summary>
    private static SyntheticElf Program(bool stripped = false, ushort type = 2, ulong imageBase = 0x400000, string? interpreter = "/lib64/ld-linux-x86-64.so.2", byte[]? buildId = null, bool withBuildId = true)
        => new()
        {
            Type = type,
            Base = imageBase,
            Interpreter = interpreter,
            Stripped = stripped,
            BuildId = withBuildId ? buildId ?? [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4] : null,
            Imports = ["puts", "__libc_start_main"],
            Functions = [("_start", StartOffset, 0x30), ("main", MainOffset, 0x30), ("add", AddOffset, 4)],
            Code = X64Code,
        };

    private static byte[] X64Code(ElfLayout layout)
    {
        var code = new byte[AddOffset + 4];
        ulong main = layout.TextVa + MainOffset;
        ulong add = layout.TextVa + AddOffset;

        // _start: endbr64; xor ebp,ebp; mov r9,rdx; pop rsi; mov rdx,rsp; and rsp,-16; push rax; push rsp;
        //         xor r8d,r8d; xor ecx,ecx; mov rdi, main; call [rip+__libc_start_main]; hlt
        var start = new List<byte> { 0xF3, 0x0F, 0x1E, 0xFA, 0x31, 0xED, 0x49, 0x89, 0xD1, 0x5E, 0x48, 0x89, 0xE2, 0x48, 0x83, 0xE4, 0xF0, 0x50, 0x54, 0x45, 0x31, 0xC0, 0x31, 0xC9 };
        start.AddRange([0x48, 0xC7, 0xC7]);
        start.AddRange(Le32((uint)main));
        start.AddRange([0xFF, 0x15]);
        start.AddRange(Le32((uint)(int)((long)layout.SlotVas[1] - (long)(layout.TextVa + (ulong)start.Count + 4))));
        start.Add(0xF4);
        start.CopyTo(code, StartOffset);

        // main: endbr64; sub rsp,8; mov edi,2; mov esi,3; call add; lea rdi,[rip+hello]; call puts@plt;
        //       xor eax,eax; add rsp,8; ret
        var m = new List<byte> { 0xF3, 0x0F, 0x1E, 0xFA, 0x48, 0x83, 0xEC, 0x08, 0xBF, 2, 0, 0, 0, 0xBE, 3, 0, 0, 0, 0xE8 };
        m.AddRange(Le32((uint)(int)((long)add - (long)(main + (ulong)m.Count + 4))));
        m.AddRange([0x48, 0x8D, 0x3D]);
        m.AddRange(Le32((uint)(int)((long)layout.RodataVa - (long)(main + (ulong)m.Count + 4))));
        m.Add(0xE8);
        m.AddRange(Le32((uint)(int)((long)layout.StubVas[0] - (long)(main + (ulong)m.Count + 4))));
        m.AddRange([0x31, 0xC0, 0x48, 0x83, 0xC4, 0x08, 0xC3]);
        m.CopyTo(code, MainOffset);

        // add: lea eax,[rdi+rsi]; ret
        new byte[] { 0x8D, 0x04, 0x37, 0xC3 }.CopyTo(code, AddOffset);
        return code;
    }

    private static byte[] Le32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // --- the parser ------------------------------------------------------

    [Fact]
    public void TheHeaderSegmentsAndSectionsAreRead()
    {
        var elf = ElfImage.Parse(Program().Build(), "prog");

        Assert.Equal(BinaryFormat.Elf, elf.Format);
        Assert.Equal(Architecture.X64, elf.Architecture);
        Assert.True(elf.Is64Bit);
        Assert.Equal(ElfType.Executable, elf.Header.Type);
        Assert.Equal("executable", elf.Kind);
        Assert.False(elf.IsLibrary);
        Assert.Equal(0x400000UL, elf.ImageBase);
        Assert.Equal("/lib64/ld-linux-x86-64.so.2", elf.Interpreter);
        Assert.Contains(elf.Segments, s => s.TypeName == "LOAD" && s.Permissions == "RWX");
        Assert.Contains(elf.Segments, s => s.TypeName == "GNU_STACK" && s.Permissions == "RW-");
        Assert.Contains(elf.Sections, s => s.Name == ".text" && s.IsExecutable);
        Assert.DoesNotContain(elf.Sections, s => s.Name == ".symtab");   // not loaded, so not in the address space
        Assert.Contains(elf.SectionHeaders, s => s.Name == ".symtab");
        Assert.Empty(elf.Warnings);
    }

    [Fact]
    public void AnImportNamesItsLibraryAndVersionAndItsPltStub()
    {
        var elf = ElfImage.Parse(Program().Build());

        Assert.Equal(["libc.so.6"], elf.Needed);
        var puts = Assert.Single(elf.Imports, i => i.Name == "puts");
        Assert.Equal("libc.so.6", puts.Module);
        Assert.Equal("GLIBC_2.2.5", elf.DynamicSymbols.Single(s => s.Name == "puts").Version);

        var (stub, import) = Assert.Single(elf.PltStubs, p => p.Import.Name == "puts");
        Assert.Equal(puts, import);
        Assert.Equal(0, (int)(elf.RvaToVa(stub) & 0xF));   // a stub starts on its 16-byte entry
    }

    [Fact]
    public void WithoutVersionsTheOnlyLibraryStillNamesTheImports()
    {
        var unversioned = new SyntheticElf { Versioned = false, Imports = ["puts"], Functions = [("f", 0, 1)] };

        var image = ElfImage.Parse(unversioned.Build());

        Assert.Equal("libc.so.6", Assert.Single(image.Imports).Module);   // one DT_NEEDED: it can only be that one
    }

    [Fact]
    public void EhFrameDeclaresEachFunctionsExtent()
    {
        var elf = ElfImage.Parse(Program(stripped: true).Build());
        ulong text = elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva);

        Assert.Equal(3, elf.UnwindRanges.Count);
        Assert.Contains(elf.UnwindRanges, r => elf.RvaToVa(r.BeginRva) == text + MainOffset && r.EndRva - r.BeginRva == 0x30);
        Assert.Contains(elf.UnwindRanges, r => elf.RvaToVa(r.BeginRva) == text + AddOffset && r.EndRva - r.BeginRva == 4);
    }

    [Fact]
    public void AStrippedProgramStillFindsMainThroughStart()
    {
        var elf = ElfImage.Parse(Program(stripped: true).Build());
        ulong text = elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva);

        Assert.Empty(elf.StaticSymbols);
        var main = Assert.Single(((ISymbolSource)elf).Symbols, s => s.Name == "main");
        Assert.Equal(text + MainOffset, elf.RvaToVa(main.Rva));
    }

    [Fact]
    public void ThePltStubIsNamedAfterTheImportAndTheGotSlotAfterTheLibrary()
    {
        var elf = ElfImage.Parse(Program().Build());
        var symbols = SymbolTable.FromImage(elf);
        var puts = elf.Imports.Single(i => i.Name == "puts");
        uint stub = elf.PltStubs.Single(p => p.Import == puts).Rva;

        Assert.Equal("libc!puts", symbols.Get(elf.RvaToVa(puts.SlotRva))!.Name);
        Assert.Equal("puts", symbols.Get(elf.RvaToVa(stub))!.Name);
        Assert.Equal("_start", SymbolTable.EntryPointName(elf));
    }

    [Fact]
    public void APieIsAProgramAndASharedObjectWithoutAnInterpreterIsALibrary()
    {
        var pie = ElfImage.Parse(Program(type: 3, imageBase: 0).Build());
        var library = ElfImage.Parse(Program(type: 3, imageBase: 0, interpreter: null).Build());

        Assert.False(pie.IsLibrary);
        Assert.Equal("position-independent executable", pie.Kind);
        Assert.Equal(0UL, pie.ImageBase);
        Assert.True(library.IsLibrary);
        Assert.Equal("shared object", library.Kind);
    }

    [Fact]
    public void A32BitProgramIsReadWithItsOwnLayouts()
    {
        var elf32 = new SyntheticElf
        {
            Is64 = false,
            Machine = 3,
            Base = 0x8048000,
            Interpreter = "/lib/ld-linux.so.2",
            LibraryVersion = "GLIBC_2.0",
            Imports = ["puts"],
            Functions = [("main", 0, 16)],
            Code = layout =>
            {
                // main: push hello; call puts@plt; add esp,4; xor eax,eax; ret
                var c = new List<byte> { 0x68 };
                c.AddRange(Le32((uint)layout.RodataVa));
                c.Add(0xE8);
                c.AddRange(Le32((uint)(int)((long)layout.StubVas[0] - (long)(layout.TextVa + (ulong)c.Count + 4))));
                c.AddRange([0x83, 0xC4, 0x04, 0x31, 0xC0, 0xC3]);
                return c.ToArray();
            },
        };

        var elf = ElfImage.Parse(elf32.Build());

        Assert.Equal(Architecture.X86, elf.Architecture);
        Assert.Equal("ELF32", elf.Header.ClassName);
        Assert.Equal("GLIBC_2.0", elf.DynamicSymbols.Single(s => s.Name == "puts").Version);
        Assert.Single(elf.PltStubs);
        Assert.Single(elf.UnwindRanges);
        Assert.Empty(elf.Warnings);

        var analysis = new BinaryAnalysis(elf);
        var text = new NativeDecompiler(analysis).Decompile(analysis.GetOrDiscoverFunction(elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva))).Text;
        Assert.Contains("puts(", text, StringComparison.Ordinal);
        Assert.Contains("hello from elf", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildIdIsTheFingerprintAndTheHeadersStandInWithoutOne()
    {
        var withId = ElfImage.Parse(Program(buildId: [0xAB, 0xCD, 0xEF, 0x01]).Build());
        var withoutA = ElfImage.Parse(Program(withBuildId: false).Build());

        Assert.Equal("abcdef01", withId.BuildId);
        Assert.Equal("abcdef01", withId.Fingerprint);
        Assert.Null(withoutA.BuildId);
        Assert.Equal(32, withoutA.Fingerprint.Length);

        // A patch to code changes no header, so a project follows the patched copy; a rebuild that moves
        // anything does not.
        var patched = Program(withBuildId: false).Build();
        var original = ElfImage.Parse(patched);
        patched[original.Sections.Single(s => s.Name == ".text").RawOffset + MainOffset + 9] ^= 0xFF;
        Assert.Equal(original.Fingerprint, ElfImage.Parse(patched).Fingerprint);
        Assert.NotEqual(original.Fingerprint, ElfImage.Parse(Program(withBuildId: false, stripped: true).Build()).Fingerprint);
    }

    [Fact]
    public void BinaryImageOpensAnElf()
    {
        string path = Write("prog", Program().Build());

        Assert.Equal(BinaryFormat.Elf, BinaryImage.Detect(path));
        var image = Assert.IsType<ElfImage>(BinaryImage.Load(path));
        Assert.Equal("prog", image.FileName);
    }

    // --- hostile input ---------------------------------------------------

    [Fact]
    public void ATruncatedElfIsRefusedNotCrashed()
    {
        byte[] whole = Program().Build();
        for (int length = 0; length < whole.Length; length += length < 0x100 ? 1 : 97)
        {
            try
            {
                var elf = ElfImage.Parse(whole.AsMemory(0, length));
                Touch(elf);
            }
            catch (ElfParseException)
            {
                // Refusing is the right answer for a header that is not all there.
            }
        }
    }

    [Fact]
    public void CorruptTablesBecomeWarningsNotCrashes()
    {
        byte[] whole = Program().Build();
        var random = new Random(1234);
        var interesting = new[] { 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF };

        // Every header byte, every program header byte, and a scattering of the rest, each set to the values
        // that break arithmetic: zero, all-ones, sign bits.
        var offsets = Enumerable.Range(0, 0x200)
            .Concat(Enumerable.Range(0, 400).Select(_ => random.Next(whole.Length)))
            .Concat(Enumerable.Range(whole.Length - 0x400, 0x400));
        foreach (int offset in offsets)
        {
            foreach (int value in interesting)
            {
                byte[] bytes = (byte[])whole.Clone();
                bytes[offset] = (byte)value;
                try
                {
                    Touch(ElfImage.Parse(bytes));
                }
                catch (ElfParseException)
                {
                }
            }
        }
    }

    [Fact]
    public void AbsurdCountsAndSizesAreBoundedNotTrusted()
    {
        byte[] bytes = Program().Build();

        // e_shnum = 0xFFFF and every section size = all-ones: nothing may allocate or loop on the stated numbers.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x3C), 0xFFFF);
        ulong shoff = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x28));
        for (int i = 1; i < 18 && (int)shoff + i * 64 + 40 <= bytes.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)shoff + i * 64 + 32), ulong.MaxValue);
        }

        // and the load segment claims to be enormous.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x40 + 40), ulong.MaxValue);

        var elf = ElfImage.Parse(bytes);
        Touch(elf);
        Assert.NotEmpty(elf.Warnings);
    }

    /// <summary>Everything a consumer reads, so a failure anywhere surfaces here rather than in a view.</summary>
    private static void Touch(ElfImage elf)
    {
        _ = elf.Sections.Count;
        _ = elf.Imports.Count;
        _ = elf.Exports.Count;
        _ = elf.PltStubs.Count;
        _ = elf.UnwindRanges.Count;
        _ = elf.Fingerprint;
        _ = elf.Kind;
        _ = elf.IsLibrary;
        _ = ((ISymbolSource)elf).Symbols.Count;
        _ = SymbolTable.FromImage(elf).Count;
        foreach (var s in elf.Sections)
        {
            _ = elf.ReadAtRva(s.Rva, 16);
        }
    }

    // --- analysis and the calling convention --------------------------------

    [Fact]
    public void ALinuxBinaryIsReadWithTheSystemVConvention()
    {
        var elf = ElfImage.Parse(Program().Build());

        Assert.Same(CallingConvention.SystemV64, CallingConvention.For(elf));
        Assert.Same(CallingConvention.Microsoft64, CallingConvention.For(64, BinaryFormat.Pe));
        Assert.Same(CallingConvention.X86, CallingConvention.For(32, BinaryFormat.Elf));
    }

    [Fact]
    public void MainDecompilesWithItsArgumentsInRdiAndRsi()
    {
        var elf = ElfImage.Parse(Program().Build());
        var analysis = new BinaryAnalysis(elf);
        analysis.DiscoverAll();
        ulong text = elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva);

        var main = analysis.GetOrDiscoverFunction(text + MainOffset);
        string pseudo = new NativeDecompiler(analysis).Decompile(main).Text;

        // Read as Windows code, 2 and 3 would sit in rdi/rsi unnoticed and add() would be called with nothing.
        Assert.Contains("add(2, 3)", pseudo, StringComparison.Ordinal);
        Assert.Contains("puts(", pseudo, StringComparison.Ordinal);
        Assert.Contains("hello from elf", pseudo, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPieProgramsThirtyTwoBitAddressReadsAsItsString()
    {
        // mov edi, hello; call puts@plt; xor eax,eax; ret — how gcc loads an address below 4 GB without -fPIE.
        var program = new SyntheticElf
        {
            Imports = ["puts"],
            Functions = [("main", 0, 16)],
            Code = layout =>
            {
                var c = new List<byte> { 0xBF };
                c.AddRange(Le32((uint)layout.RodataVa));
                c.Add(0xE8);
                c.AddRange(Le32((uint)(int)((long)layout.StubVas[0] - (long)(layout.TextVa + (ulong)c.Count + 4))));
                c.AddRange([0x31, 0xC0, 0xC3]);
                return c.ToArray();
            },
        };
        var elf = ElfImage.Parse(program.Build());
        var analysis = new BinaryAnalysis(elf);

        var main = analysis.GetOrDiscoverFunction(elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva));
        string pseudo = new NativeDecompiler(analysis).Decompile(main).Text;

        Assert.Contains("puts(\"hello from elf\")", pseudo, StringComparison.Ordinal);
    }

    [Fact]
    public void ACalleesSystemVArgumentsAreCountedFromItsRegisterUse()
    {
        var elf = ElfImage.Parse(Program().Build());
        var analysis = new BinaryAnalysis(elf);
        ulong text = elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva);

        var signature = analysis.SignatureFor(text + AddOffset);

        Assert.Equal(2, signature.ArgumentCount);
        Assert.Equal(2, signature.IntegerArgumentCount(CallingConvention.SystemV64));
        Assert.Equal(0, signature.FloatArgumentCount(CallingConvention.SystemV64));
    }

    [Fact]
    public void DiscoveryUsesTheSymbolsTheUnwindTableAndTheNoReturnStartup()
    {
        var elf = ElfImage.Parse(Program().Build());
        var analysis = new BinaryAnalysis(elf);
        analysis.DiscoverAll();
        ulong text = elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva);

        Assert.Equal("main", analysis.NameFor(text + MainOffset));
        Assert.Equal("add", analysis.NameFor(text + AddOffset));
        Assert.Equal("_start", analysis.NameFor(text + StartOffset));

        // __libc_start_main never returns, so _start ends at the call rather than running into hlt and beyond.
        var start = analysis.GetOrDiscoverFunction(text + StartOffset);
        Assert.True(start.CodeSize <= 0x30, $"_start ran on to 0x{start.CodeSize:X} bytes");
    }

    // --- the project file -------------------------------------------------

    [Fact]
    public void AnElfProjectIsKeyedOnItsFingerprintAndRoundTrips()
    {
        var elf = ElfImage.Parse(Program().Build(), Path.Combine(_directory, "prog"));
        var store = new AnnotationStore();
        ulong text = elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva);
        store.SetName(text + MainOffset, "real_main");
        string path = Path.Combine(_directory, "prog.spydate");

        SpydateProject.SaveTo(path, elf, store);
        string json = File.ReadAllText(path);
        Assert.Contains("\"fingerprint\"", json, StringComparison.Ordinal);
        Assert.Contains(elf.Fingerprint, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"timeDateStamp\"", json, StringComparison.Ordinal);

        var loaded = new AnnotationStore();
        var result = SpydateProject.Load(path, elf, loaded);
        Assert.True(result.Loaded, result.Reason);
        Assert.Equal("real_main", loaded.NameFor(text + MainOffset));

        // A different build is refused, not overlaid.
        var other = ElfImage.Parse(Program(buildId: [9, 9, 9, 9]).Build(), Path.Combine(_directory, "prog"));
        Assert.False(SpydateProject.Load(path, other, new AnnotationStore()).Loaded);
        Assert.Contains($"{elf.Fingerprint}-{elf.Length:X}", SpydateProject.UserStorePath(elf), StringComparison.Ordinal);
    }

    // --- the MCP surface --------------------------------------------------

    [Fact]
    public async Task OpenBinaryOrientsAnAgentInAnElf()
    {
        string path = Write("prog", Program().Build());
        using var store = new SessionStore();
        var tools = new SessionTools(store, McpOptions.Default);

        string overview = await tools.OpenBinaryAsync(path);

        Assert.Contains("ELF64 X86_64 executable", overview, StringComparison.Ordinal);
        Assert.Contains("libc.so.6", overview, StringComparison.Ordinal);
        Assert.Contains("System V", overview, StringComparison.Ordinal);
        Assert.Contains("rdi, rsi", overview, StringComparison.Ordinal);
        Assert.Contains("debug", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("PDB", overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListImportsGivesThePltStubAndCountsTheCallsThroughIt()
    {
        string path = Write("prog", Program().Build());
        using var store = new SessionStore();
        await new SessionTools(store, McpOptions.Default).OpenBinaryAsync(path);
        var elf = (ElfImage)store.Current!.Image;
        ulong stub = elf.RvaToVa(elf.PltStubs.Single(p => p.Import.Name == "puts").Rva);

        string text = new NavigationTools(store).ListImports(filter: "puts");

        Assert.Contains("plt_va", text, StringComparison.Ordinal);
        Assert.Contains($"0x{stub:X}", text, StringComparison.Ordinal);
        Assert.Contains("libc.so.6", text, StringComparison.Ordinal);

        // main calls puts through the stub, never through the GOT slot; counting the slot alone would say unused.
        var row = text.Split('\n').Single(l => l.Contains("puts", StringComparison.Ordinal) && l.Contains("0x", StringComparison.Ordinal));
        Assert.True(int.Parse(row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1], System.Globalization.CultureInfo.InvariantCulture) > 0, row);
    }

    [Fact]
    public async Task TheDebugToolsSayAnElfIsReadNotRun()
    {
        string path = Write("prog", Program().Build());
        using var store = new SessionStore { Debug = new NeverDebug() };
        await new SessionTools(store, McpOptions.Default).OpenBinaryAsync(path);
        var tools = new DebugTools(store, McpOptions.Default with { AllowDebug = true });

        string answer = tools.Run("start");

        Assert.Contains("not available for Elf", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJarIsStillNamedAndPointedAtReadFile()
    {
        string path = Path.Combine(_directory, "lib.jar");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("META-INF/MANIFEST.MF");
        }

        using var store = new SessionStore();
        string answer = await new SessionTools(store, McpOptions.Default).OpenBinaryAsync(path);

        Assert.Contains("Java archive", answer, StringComparison.Ordinal);
        Assert.Contains("read_file", answer, StringComparison.Ordinal);
    }

    /// <summary>A debugger that fails the test if anything reaches it: the refusal must come first.</summary>
    private sealed class NeverDebug : IDebugControl
    {
        public DebugSnapshot Snapshot() => new() { State = "idle" };
        public string? Start() => throw new InvalidOperationException("an ELF must never reach the debugger");
        public void Stop() => throw new InvalidOperationException();
        public void Continue() => throw new InvalidOperationException();
        public void Pause() => throw new InvalidOperationException();
        public void StepInstruction() => throw new InvalidOperationException();
        public void StepOver() => throw new InvalidOperationException();
        public void RunTo(ulong staticVa) => throw new InvalidOperationException();
        public bool SetBreakpoint(ulong staticVa, bool on) => throw new InvalidOperationException();
        public string? SetModuleBreakpoint(string module, uint rva, bool on) => throw new InvalidOperationException();
        public bool SelectThread(uint threadId) => throw new InvalidOperationException();
        public string? TryPatch(ulong va, string instruction, string? comment) => throw new InvalidOperationException();
        public bool UndoPatch(uint rva) => throw new InvalidOperationException();
        public byte[] ReadMemory(ulong staticVa, int length) => throw new InvalidOperationException();
        public bool WaitUntilStopped(TimeSpan timeout) => throw new InvalidOperationException();
    }

    // --- real Linux binaries, where the machine has them -------------------

    [Fact]
    public void ARealDynamicProgramParsesWithItsImportsStubsAndUnwindTable()
    {
        if (!Corpus.Has(NvidiaSmi))
        {
            return;
        }

        var elf = ElfImage.Load(NvidiaSmi);

        Assert.Equal(Architecture.X64, elf.Architecture);
        Assert.Contains("libc.so.6", elf.Needed);
        Assert.True(elf.Imports.Count > 50, $"{elf.Imports.Count} imports");
        Assert.True(elf.PltStubs.Count > 50, $"{elf.PltStubs.Count} PLT stubs");
        Assert.True(elf.UnwindRanges.Count > 100, $"{elf.UnwindRanges.Count} FDEs");
        Assert.DoesNotContain(elf.UnwindRanges, r => elf.SectionFromRva(r.BeginRva)!.Name.StartsWith(".plt", StringComparison.Ordinal));
        Assert.Contains(((ISymbolSource)elf).Symbols, s => s.Name == "main");
        Assert.Empty(elf.Warnings);
    }

    [Fact]
    public void ARealSharedObjectExportsItsApi()
    {
        if (!Corpus.Has(NvidiaMl))
        {
            return;
        }

        var elf = ElfImage.Load(NvidiaMl);

        Assert.True(elf.IsLibrary);
        Assert.Equal("libnvidia-ml.so.1", elf.SoName);
        Assert.Contains(elf.Exports, e => e.Name == "nvmlInit_v2");
        Assert.NotEmpty(elf.StaticSymbols);
        Assert.Empty(elf.Warnings);
    }
}
