using Spydate.Core.PE;
using Spydate.Core.Symbols;
using Spydate.Disassembly;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>Unwind-table bounds, unreached-byte sweeping and no-return call handling.</summary>
public class DiscoveryBoundsTests
{
    private readonly ITestOutputHelper _output;

    public DiscoveryBoundsTests(ITestOutputHelper output) => _output = output;

    private const ulong Base = 0x140000000;
    private const ulong Entry = Base + 0x1000;

    private static Function Discover(byte[] code, DiscoveryOptions? options = null, ulong? boundsEnd = null)
    {
        var source = new MemoryCodeSource(code, Entry, bitness: 64, imageBase: Base, imageSize: 0x10000);
        var symbols = new SymbolTable();
        var discovery = new FunctionDiscovery(source, new X86Disassembler(64, symbols), symbols, options);
        return discovery.Discover(Entry, "test", boundsEnd);
    }

    [Fact]
    public void CallToNoReturnEndsThePath()
    {
        // call +0 (a call to the next instruction, used as the no-return target) then junk bytes.
        var code = new byte[]
        {
            0xE8, 0x00, 0x00, 0x00, 0x00,  // call 0x140001005
            0x0F, 0x0B,                    // ud2 - would be decoded without the no-return rule
            0xC3,                          // ret
        };

        var withRule = Discover(code, new DiscoveryOptions { IsNoReturn = va => va == Entry + 5 });
        var withoutRule = Discover(code);

        Assert.Single(withRule.Instructions);
        Assert.Contains(withRule.Notes, n => n.Contains("does not return", StringComparison.Ordinal));
        Assert.True(withoutRule.InstructionCount > withRule.InstructionCount);
    }

    [Fact]
    public void IndirectCallToNoReturnSlotEndsThePath()
    {
        // call qword ptr [rip+0x100] ; ret   — the slot is an IAT entry for ExitProcess.
        var code = new byte[] { 0xFF, 0x15, 0x00, 0x01, 0x00, 0x00, 0xC3 };
        ulong slot = Entry + 0x106;

        var stopped = Discover(code, new DiscoveryOptions { IsNoReturn = va => va == slot });

        Assert.Single(stopped.Instructions);
        Assert.Equal(InstructionFlow.IndirectCall, stopped.Blocks[0].Last.Flow);
    }

    [Fact]
    public void FastfailInterruptEndsThePath()
    {
        // int 0x29 ; ret — __fastfail never returns, so the ret is not reachable.
        var code = new byte[] { 0xCD, 0x29, 0xC3 };

        var f = Discover(code);

        Assert.Single(f.Instructions);
        Assert.Empty(f.Blocks[0].Successors);
    }

    [Fact]
    public void OrdinaryInterruptStillFallsThrough()
    {
        // int 0x2C is a debug break: execution continues afterwards.
        var code = new byte[] { 0xCD, 0x2C, 0x90, 0xC3 };

        var f = Discover(code);

        Assert.Equal(3, f.InstructionCount);
    }

    [Fact]
    public void BoundsAreRecordedOnTheFunction()
    {
        var code = new byte[] { 0xC3 };

        var f = Discover(code, boundsEnd: Entry + 0x20);

        Assert.Equal(Entry + 0x20, f.BoundsEnd);
        Assert.Equal(0x20UL, f.DeclaredSize);
        Assert.False(f.ExtendsBeyondBounds);
    }

    [Fact]
    public void UnreachedBytesInsideTheBoundsAreSwept()
    {
        // ret, then padding, then a block only a jump table would reach.
        var code = new byte[]
        {
            0xC3,                          // 0x1000 ret — descent stops here
            0xCC, 0xCC, 0xCC,              // padding
            0x33, 0xC0,                    // 0x1004 xor eax, eax
            0xC3,                          // 0x1006 ret
        };

        var swept = Discover(code, boundsEnd: Entry + 7);
        var notSwept = Discover(code, new DiscoveryOptions { SweepUnreachedBytes = false }, boundsEnd: Entry + 7);

        Assert.Single(notSwept.Instructions);
        Assert.Equal(3, swept.InstructionCount);
        Assert.Contains(swept.Instructions, i => i.Va == Entry + 4 && i.Mnemonic == "xor");
        Assert.Contains(swept.Notes, n => n.Contains("Recovered", StringComparison.Ordinal));
    }

    [Fact]
    public void SweepStopsAtTheDeclaredEnd()
    {
        var code = new byte[]
        {
            0xC3,             // 0x1000 ret
            0x33, 0xC0,       // 0x1001 xor eax, eax — inside the bounds
            0x33, 0xC9,       // 0x1003 xor ecx, ecx — past the end, belongs to the next function
        };

        var f = Discover(code, boundsEnd: Entry + 3);

        Assert.Equal(2, f.InstructionCount);
        Assert.DoesNotContain(f.Instructions, i => i.Va >= Entry + 3);
    }

    [Fact]
    public void SweepIsSkippedWithoutBounds()
    {
        var code = new byte[] { 0xC3, 0x33, 0xC0, 0xC3 };

        Assert.Single(Discover(code).Instructions);
    }

    [SkippableFact]
    public void RealBinaryFunctionsRespectTheirUnwindBounds()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.Machine == MachineType.Amd64, $"{pe.Machine} has no x64 unwind table");

        var analysis = new BinaryAnalysis(pe);
        var functions = analysis.DiscoverAll(maxFunctions: 500);

        var bounded = functions.Where(f => f.BoundsEnd is not null).ToList();
        Assert.NotEmpty(bounded);

        // Discovery may stop early (indirect jumps), but it should not run past the declared end.
        var overruns = bounded.Where(f => f.ExtendsBeyondBounds).ToList();
        Assert.True(
            overruns.Count < bounded.Count / 10,
            $"{overruns.Count} of {bounded.Count} functions decoded past their unwind bounds");
    }

    [SkippableFact]
    public void NoReturnImportsAreRecognised()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Skip.IfNot(File.Exists(path), "notepad.exe not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");

        var analysis = new BinaryAnalysis(pe);
        var noReturnSlots = pe.Imports.Concat(pe.DelayImports)
            .SelectMany(m => m.Functions)
            .Where(f => f.Name is "ExitProcess" or "TerminateProcess" or "RaiseFailFastException")
            .Select(f => pe.RvaToVa(f.IatRva))
            .ToList();

        Skip.If(noReturnSlots.Count == 0, "no no-return imports in this build");
        Assert.All(noReturnSlots, slot => Assert.True(analysis.IsNoReturn(slot), $"0x{slot:X} should be no-return"));
    }

    [SkippableFact]
    public void RealBinaryRecoversUnreachedBytesAndStopsAtNoReturnCalls()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.Machine == MachineType.Amd64, $"{pe.Machine} has no x64 unwind table");

        var analysis = new BinaryAnalysis(pe);
        var functions = analysis.DiscoverAll(maxFunctions: 2000);

        int swept = functions.Count(f => f.Notes.Any(n => n.StartsWith("Recovered", StringComparison.Ordinal)));
        int stopped = functions.Count(f => f.Notes.Any(n => n.Contains("does not return", StringComparison.Ordinal)));
        _output.WriteLine($"{functions.Count} functions: {functions.Count(f => f.BoundsEnd is not null)} with unwind bounds, " +
                          $"{swept} swept for unreached bytes, {stopped} stopped at a no-return call");

        Assert.True(swept > 0, "the unwind-bounds sweep never recovered anything");
        Assert.True(stopped > 0, "no call to a no-return function was recognised");

        // A swept function must still be internally consistent: blocks sorted, no overlap.
        foreach (var f in functions.Where(f => f.Notes.Any(n => n.StartsWith("Recovered", StringComparison.Ordinal))))
        {
            ulong previousEnd = 0;
            foreach (var block in f.Blocks)
            {
                Assert.True(block.StartVa >= previousEnd, $"blocks of {f.Name} overlap at 0x{block.StartVa:X}");
                previousEnd = block.EndVa;
            }
        }
    }
}
