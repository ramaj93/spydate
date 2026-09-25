using System.Buffers.Binary;
using System.Globalization;
using Spydate.Core.Binary;
using Spydate.Core.Elf;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;
using Spydate.Disassembly.Arm64;
using Spydate.Mcp.Session;
using Spydate.Mcp.Tools;

namespace Spydate.Tests;

/// <summary>
/// The ARM64 decoder and what analysis does with it: text for every instruction group, addresses put together
/// from <c>adrp</c> pairs, switch tables, PLT stubs named after their imports.
/// </summary>
public class Arm64Tests
{
    private const ulong At = 0x100000;

    [Fact]
    public void EveryRecordedVectorDecodesToItsText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Arm64", "vectors.txt");
        var wrong = new List<string>();
        int count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t');
            uint word = uint.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string text = Arm64Decoder.Decode(word, At)?.ToString() ?? "(nothing)";
            count++;
            if (text != parts[1])
            {
                wrong.Add($"{parts[0]}: expected '{parts[1]}', got '{text}'");
            }
        }

        Assert.True(count > 1000, $"only {count} vectors");
        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData(0xD503237Fu, "pacibsp")]
    [InlineData(0xD50323FFu, "autibsp")]
    [InlineData(0xD503245Fu, "bti c")]
    [InlineData(0x00000000u, "udf #0x0")]
    [InlineData(0xB8E10000u, "ldaddal w1, w0, [x0]")]
    [InlineData(0xF821001Fu, "stadd x1, [x0]")]
    [InlineData(0x88E1FC02u, "casal w1, w2, [x0]")]
    [InlineData(0xB37D87EEu, "bfc x14, #3, #34")]
    [InlineData(0xCE020C20u, "eor3 v0.16b, v1.16b, v2.16b, v3.16b")]
    [InlineData(0x4E829420u, "sdot v0.4s, v1.16b, v2.16b")]
    [InlineData(0x9200F020u, "and x0, x1, #0x5555555555555555")]
    [InlineData(0xF87B9475u, "ldraa x21, [x3, #-568]")]
    [InlineData(0x1AC26146u, "smax w6, w10, w2")]
    [InlineData(0x4C9F2060u, "st1 {v0.16b-v3.16b}, [x3], #64")]
    [InlineData(0x0DCB42C7u, "ld1 {v7.h}[0], [x22], x11")]
    public void InstructionsNewerThanTheReferenceDecoderReadAsTheArchitectureNamesThem(uint word, string text)
    {
        // Pointer authentication, branch targets, LSE atomics, bfc, SHA-3, dot products, CSSC: all later than
        // the decoder the vectors were checked against, and checked against the architecture by hand instead.
        Assert.Equal(text, Arm64Decoder.Decode(word, At)?.ToString());
    }

    [Theory]
    [InlineData(0x94000010u, InstructionFlow.Call, 0x100040ul)]
    [InlineData(0x17FFFFFCu, InstructionFlow.UnconditionalBranch, 0xFFFF0ul)]
    [InlineData(0x54000041u, InstructionFlow.ConditionalBranch, 0x100008ul)]
    [InlineData(0xB4000040u, InstructionFlow.ConditionalBranch, 0x100008ul)]
    [InlineData(0x36180040u, InstructionFlow.ConditionalBranch, 0x100008ul)]
    public void BranchesSayWhereTheyGo(uint word, InstructionFlow flow, ulong target)
    {
        var ins = Arm64Decoder.Decode(word, At)!;

        Assert.Equal(flow, ins.Flow);
        Assert.Equal(target, ins.Target);
    }

    [Theory]
    [InlineData(0xD65F03C0u, InstructionFlow.Return)]
    [InlineData(0xD65F0BFFu, InstructionFlow.Return)]     // retaa
    [InlineData(0xD61F0220u, InstructionFlow.IndirectBranch)]
    [InlineData(0xD63F0220u, InstructionFlow.IndirectCall)]
    [InlineData(0xD4200000u, InstructionFlow.Interrupt)]  // brk #0
    [InlineData(0xD4000001u, InstructionFlow.Next)]       // svc #0: the kernel returns
    public void ControlFlowIsClassified(uint word, InstructionFlow flow)
    {
        Assert.Equal(flow, Arm64Decoder.Decode(word, At)!.Flow);
    }

    [Fact]
    public void AnyWordDecodesOrIsRefusedWithoutThrowing()
    {
        // Untrusted bytes: every word is either an instruction or null, never an exception.
        var random = new Random(1234);
        int decoded = 0;
        for (int i = 0; i < 200_000; i++)
        {
            uint word = (uint)random.NextInt64(0, 1L << 32);
            if (Arm64Decoder.Decode(word, (ulong)random.NextInt64()) is { } ins)
            {
                decoded++;
                Assert.False(string.IsNullOrEmpty(ins.ToString()));
            }
        }

        Assert.InRange(decoded, 20_000, 180_000);
    }

    [Fact]
    public void AnAddressBuiltFromAPageIsPutOnTheInstructionThatFinishesIt()
    {
        const ulong code = 0x10000;
        const ulong data = 0x23010;
        const ulong slot = 0x24028;
        var bytes = Words(
            Adrp(0, code, data),
            AddImm(0, 0, (uint)(data & 0xFFF)),
            LdrX(1, 0, 8),
            Adrp(16, code + 12, slot),
            LdrX(17, 16, (uint)(slot & 0xFFF)),
            Blr(17));

        var decoded = new Arm64Disassembler().Decode(bytes, code, 0);

        Assert.Equal("adrp", decoded[0].Mnemonic);
        Assert.Null(decoded[0].DataVa);
        Assert.Equal(data, decoded[1].DataVa);
        Assert.Equal(XrefKind.Offset, decoded[1].DataKind);
        Assert.Equal(data + 8, decoded[2].DataVa);
        Assert.Equal(XrefKind.Read, decoded[2].DataKind);
        Assert.Equal(slot, decoded[5].IndirectSlotVa);
        Assert.Equal(InstructionFlow.IndirectCall, decoded[5].Flow);
    }

    [Fact]
    public void WhatARegisterHeldIsForgottenAtAJump()
    {
        const ulong code = 0x10000;
        var bytes = Words(Adrp(0, code, 0x30000), B(code + 4, code + 8), AddImm(0, 0, 0x10));

        var decoded = new Arm64Disassembler().Decode(bytes, code, 0);

        // Reached by the branch, not from the adrp: x0 may hold anything there.
        Assert.Null(decoded[2].DataVa);
    }

    [Fact]
    public void ASwitchTableOfScaledOffsetsIsFollowed()
    {
        // cmp w0, #2 ; b.hi default ; adrp/add x9 = table ; ldrb w10, [x9, x0] ; adr x11, case0 ;
        // add x11, x11, x10, lsl #2 ; br x11 — then three cases, a default, and the table of word offsets.
        const ulong code = 0x1000;
        const ulong case0 = code + 0x20;
        const ulong defaultCase = code + 0x2C;
        const ulong table = code + 0x30;
        var words = new List<uint>
        {
            CmpW(0, 2),
            BCond(code + 4, defaultCase, 8),
            Adrp(9, code + 8, table),
            AddImm(9, 9, (uint)(table & 0xFFF)),
            LdrbRegister(10, 9, 0),
            Adr(11, code + 0x14, case0),
            AddShifted(11, 11, 10, 2),
            Br(11),
            Ret, Ret, Ret, Ret,
            0x00020100,   // entries 0, 1, 2
        };
        var source = new MemoryCodeSource(Words([.. words]).ToArray(), code, 64);
        var discovery = new FunctionDiscovery(source, new Arm64Disassembler(), new SymbolTable());

        var function = discovery.Discover(code);

        var switchTable = Assert.Single(function.JumpTables);
        Assert.Equal(table, switchTable.TableVa);
        Assert.True(switchTable.CountFromBoundsCheck);
        Assert.Equal([case0, case0 + 4, case0 + 8], switchTable.Targets);
        Assert.Contains(function.Blocks, b => b.StartVa == case0 + 8);
    }

    [Fact]
    public void ACallWithAnotherFunctionRightBehindItDoesNotReturn()
    {
        // f: bl throw_helper — and g starts at the next instruction. A compiler puts a call that never returns last,
        // so the bytes after it are g, not more of f.
        const ulong f = 0x1000;
        const ulong g = 0x1004;
        var source = new MemoryCodeSource(Words(Bl(f, 0x1100), Ret, Ret).ToArray(), f, 64);
        var options = DiscoveryOptions.Default with { IsFunctionStart = va => va == g };

        var function = new FunctionDiscovery(source, new Arm64Disassembler(), new SymbolTable(), options).Discover(f);

        Assert.Equal(1, function.InstructionCount);
        Assert.Equal(g, function.EndVa);
    }

    [Fact]
    public void ACallThroughThePltReadsAsTheImport()
    {
        var elf = HelloElf();

        Assert.Equal(Architecture.Arm64, elf.Architecture);
        Assert.Single(elf.PltStubs);

        var analysis = new BinaryAnalysis(elf);
        var main = analysis.GetOrDiscoverFunction(elf.RvaToVa(elf.Sections.Single(s => s.Name == ".text").Rva));
        string listing = AsmListing.ForFunction(analysis, main);

        Assert.Contains("bl      puts", listing, StringComparison.Ordinal);
        Assert.Contains("\"hello from elf\"", listing, StringComparison.Ordinal);
        Assert.True(NativeDecompiler.Supports(analysis));
        Assert.Contains("puts(\"hello from elf\")", new NativeDecompiler(analysis).Decompile(main).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentReadsArm64AsPseudoCOrAsAssembly()
    {
        Assert.True(InstructionDecoders.Supports(Architecture.Arm64));
        Assert.False(InstructionDecoders.Supports(Architecture.Arm));

        var elf = HelloElf();
        var analysis = new BinaryAnalysis(elf);
        analysis.DiscoverAll();
        var session = new BinarySession("hello", elf, analysis, null, new DiscoveryState(analysis.FunctionCount, true, TimeSpan.Zero));
        var store = new SessionStore();
        store.Set(session);
        var code = new CodeTools(store);

        Assert.NotNull(session.Decompiler);
        Assert.False(session.CanDebug);
        Assert.Contains("puts(\"hello from elf\")", code.ReadFunction("main"), StringComparison.Ordinal);
        Assert.Contains("bl      puts", code.ReadFunction("main", view: "asm"), StringComparison.Ordinal);
    }

    /// <summary>An AArch64 program whose main loads a string and calls puts through the PLT.</summary>
    private static ElfImage HelloElf() => ElfImage.Parse(new SyntheticElf
    {
        Machine = 183,
        Interpreter = "/lib/ld-linux-aarch64.so.1",
        Imports = ["puts"],
        Functions = [("main", 0, 16)],
        Code = layout =>
        {
            // main: adrp x0, hello ; add x0, x0, :lo12:hello ; bl puts@plt ; ret
            ulong main = layout.TextVa;
            return Words(
                Adrp(0, main, layout.RodataVa),
                AddImm(0, 0, (uint)(layout.RodataVa & 0xFFF)),
                Bl(main + 8, layout.StubVas[0]),
                Ret).ToArray();
        },
    }.Build());

    [SkippableTheory]
    [InlineData("ikvm.image.runtime.linux-arm64", "linux-arm64", "libzip.so")]
    [InlineData("ikvm.image.runtime.win-arm64", "win-arm64", "jvm.dll")]
    public void RealArm64LibrariesDecodeWhole(string package, string rid, string file)
    {
        // Real compiler output, when this machine has it: IKVM's JDK images ship ARM64 builds of the JVM.
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", package);
        string? path = Directory.Exists(root)
            ? Directory.GetDirectories(root).Select(v => Path.Combine(v, "ikvm", "any", rid, "bin", file)).FirstOrDefault(File.Exists)
            : null;
        Skip.If(path is null, $"{package} is not in the NuGet cache");

        var image = BinaryImage.Load(path!);
        var analysis = new BinaryAnalysis(image);
        var functions = analysis.DiscoverAll();

        Assert.Equal(Architecture.Arm64, image.Architecture);
        Assert.True(functions.Count > 100);
        Assert.DoesNotContain(functions.SelectMany(f => f.Instructions), i => i.Flow == InstructionFlow.Invalid);
        Assert.DoesNotContain(functions, f => f.ExtendsBeyondBounds);
        Assert.Contains(analysis.Xrefs.All(), x => x.IsData && analysis.StringAt(x.ToVa) is not null);
        // A call to the C library by name: through a PLT stub on Linux, through an import slot loaded by adrp/ldr on Windows.
        Assert.Contains(functions.SelectMany(f => f.Instructions), i => i.IsCall && (i.BranchTargetVa ?? i.IndirectSlotVa) is { } t
            && analysis.Symbols.TryGet(t, out var s) && s.Name.Split('!')[^1] is "malloc" or "free" or "strlen" or "GetLastError");
    }

    // ---- pseudo-C ----------------------------------------------------------------------------------

    [Fact]
    public void AConditionalCompareChainIsOneCondition()
    {
        // cmp w0, #1 ; ccmp w1, #2, #0, ne ; cset w0, eq ; ret  —  w0 != 1 && w1 == 2
        string text = PseudoC(CmpW(0, 1), 0x7A421820, 0x1A9F17E0, Ret);

        Assert.Contains("w0 != 1", text, StringComparison.Ordinal);
        Assert.Contains("w1 == 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("flags>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameRecordIsNotPartOfTheFunction()
    {
        // stp x29, x30, [sp, #-16]! ; mov x29, sp ; bl f ; ldp x29, x30, [sp], #16 ; ret
        const ulong code = 0x1000;
        string text = PseudoC(0xA9BF7BFD, 0x910003FD, Bl(code + 8, 0x2000), 0xA8C17BFD, Ret);

        Assert.Contains("sub_2000()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x29", text, StringComparison.Ordinal);
        Assert.DoesNotContain("x30", text, StringComparison.Ordinal);
        Assert.DoesNotContain("local_", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoadAtAnOffsetReadsAsTheFieldItIs()
    {
        // ldr w8, [x0, #4] ; add w0, w8, #1 ; ret
        string text = PseudoC(0xB9400408, 0x11000500, Ret);

        Assert.Contains("*(uint32_t*)(x0 + 4) + 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountedLoopIsALoop()
    {
        // loop: sub x0, x0, #1 ; cbnz x0, loop ; ret
        string text = PseudoC(0xD1000400, 0xB5FFFFE0, Ret);

        Assert.Contains("while", text, StringComparison.Ordinal);
        Assert.DoesNotContain("goto", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallGetsTheArgumentsSetBeforeIt()
    {
        // mov w0, #7 ; mov x1, x2 ; b f  (a tail call with two arguments)
        const ulong code = 0x1000;
        string text = PseudoC(0x528000E0, 0xAA0203E1, B(code + 8, 0x2000));

        Assert.Contains("return sub_2000(7, x2);", text, StringComparison.Ordinal);
    }

    /// <summary>The pseudo-C of a function made of <paramref name="words"/> at 0x1000.</summary>
    private static string PseudoC(params uint[] words)
    {
        const ulong code = 0x1000;
        var source = new MemoryCodeSource(Words(words).ToArray(), code, 64);
        var function = new FunctionDiscovery(source, new Arm64Disassembler(), new SymbolTable()).Discover(code);
        var decompiler = new NativeDecompiler(64, convention: CallingConvention.Aapcs64, architecture: Architecture.Arm64);
        return decompiler.Decompile(function).Text;
    }

    // ---- encoders for the synthetic code -------------------------------------------------------------

    private const uint Ret = 0xD65F03C0;

    private static ReadOnlyMemory<byte> Words(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return bytes;
    }

    private static uint Adrp(uint rd, ulong pc, ulong target)
    {
        long pages = (long)(target >> 12) - (long)(pc >> 12);
        return 0x90000000 | (((uint)pages & 3) << 29) | ((((uint)(pages >> 2)) & 0x7FFFF) << 5) | rd;
    }

    private static uint Adr(uint rd, ulong pc, ulong target)
    {
        long delta = (long)target - (long)pc;
        return 0x10000000 | (((uint)delta & 3) << 29) | ((((uint)(delta >> 2)) & 0x7FFFF) << 5) | rd;
    }

    private static uint AddImm(uint rd, uint rn, uint imm) => 0x91000000 | (imm << 10) | (rn << 5) | rd;

    private static uint AddShifted(uint rd, uint rn, uint rm, uint lsl) => 0x8B000000 | (rm << 16) | (lsl << 10) | (rn << 5) | rd;

    private static uint LdrX(uint rt, uint rn, uint offset) => 0xF9400000 | ((offset / 8) << 10) | (rn << 5) | rt;

    private static uint LdrbRegister(uint rt, uint rn, uint rm) => 0x38606800 | (rm << 16) | (rn << 5) | rt;

    private static uint CmpW(uint rn, uint imm) => 0x7100001F | (imm << 10) | (rn << 5);

    private static uint Blr(uint rn) => 0xD63F0000 | (rn << 5);

    private static uint Br(uint rn) => 0xD61F0000 | (rn << 5);

    private static uint Bl(ulong pc, ulong target) => 0x94000000 | ((uint)(((long)target - (long)pc) >> 2) & 0x3FFFFFF);

    private static uint B(ulong pc, ulong target) => 0x14000000 | ((uint)(((long)target - (long)pc) >> 2) & 0x3FFFFFF);

    private static uint BCond(ulong pc, ulong target, uint cond) => 0x54000000 | ((((uint)(((long)target - (long)pc) >> 2)) & 0x7FFFF) << 5) | cond;
}
