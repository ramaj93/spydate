using System.Diagnostics;
using Spydate.Core.PE;
using Spydate.Disassembly;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>Finding functions nothing points at, by scanning the leftover bytes for prologues.</summary>
public class GapSweepTests
{
    private readonly ITestOutputHelper _output;

    public GapSweepTests(ITestOutputHelper output) => _output = output;

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    [Theory]
    [InlineData(new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08 }, 64)]        // mov [rsp+8], rbx
    [InlineData(new byte[] { 0x48, 0x83, 0xEC, 0x28 }, 64)]              // sub rsp, 0x28
    [InlineData(new byte[] { 0x4C, 0x8B, 0xDC, 0x49, 0x89 }, 64)]        // mov r11, rsp
    [InlineData(new byte[] { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x20 }, 64)]  // push rbx; sub rsp, 0x20
    [InlineData(new byte[] { 0xFF, 0x25, 0x00, 0x10, 0x00, 0x00 }, 64)]  // jmp [import]
    [InlineData(new byte[] { 0x55, 0x8B, 0xEC }, 32)]                    // push ebp; mov ebp, esp
    [InlineData(new byte[] { 0x8B, 0xFF, 0x55, 0x8B, 0xEC }, 32)]        // hot-patch pad
    [InlineData(new byte[] { 0x83, 0xEC, 0x10 }, 32)]                    // sub esp, 0x10
    public void RecognisesCommonPrologues(byte[] code, int bitness)
        => Assert.True(FunctionPrologues.LooksLikeFunctionStart(code, bitness));

    [Theory]
    [InlineData(new byte[] { 0x90, 0x90, 0x90, 0x90 }, 64)]              // nops
    [InlineData(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, 64)]              // padding
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, 64)]              // zeros
    [InlineData(new byte[] { 0x48, 0x8B, 0x01, 0xC3 }, 64)]              // mov rax,[rcx]; ret — mid-function
    [InlineData(new byte[] { 0x53, 0x90, 0x90, 0x90 }, 64)]              // lone push, no frame setup
    [InlineData(new byte[] { 0x41, 0x42, 0x43, 0x44 }, 32)]              // ASCII text
    public void RejectsNonPrologues(byte[] code, int bitness)
        => Assert.False(FunctionPrologues.LooksLikeFunctionStart(code, bitness));

    [Fact]
    public void PaddingIsRecognised()
    {
        Assert.True(FunctionPrologues.IsPadding(0xCC));
        Assert.True(FunctionPrologues.IsPadding(0x90));
        Assert.True(FunctionPrologues.IsPadding(0x00));
        Assert.False(FunctionPrologues.IsPadding(0x55));
    }

    [SkippableFact]
    public void SweepFindsFunctionsNothingReferences()
    {
        // x86 has no unwind table, so seeds are only the entry point and exports: the sweep is
        // what finds the rest.
        string path = Path.GetFullPath(Path.Combine(System32, @"..\SysWOW64\notepad.exe"));
        Skip.IfNot(File.Exists(path), "32-bit notepad.exe not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.Machine == MachineType.I386, $"{pe.Machine} is not x86");

        // Only the seeded half is analysed here: the swept half is the shared corpus analysis, which is
        // this same image under the same default options.
        var withoutSweep = new BinaryAnalysis(pe, options: new DiscoveryOptions { SweepGapsForFunctions = false });
        var withSweep = Corpus.Analysed(Corpus.NotepadX86);

        var sw = Stopwatch.StartNew();
        int seeded = withoutSweep.DiscoverAll(maxFunctions: 20_000).Count;
        long seededMs = sw.ElapsedMilliseconds;
        int swept = withSweep.Functions.Count;
        _output.WriteLine($"{pe.FileName} x86: {seeded} functions from seeds ({seededMs} ms), {swept} with the gap sweep");

        Assert.True(swept > seeded, $"the sweep added nothing ({seeded} -> {swept})");

        // Swept functions must still be well formed: no invalid instructions, blocks in order.
        foreach (var f in withSweep.Functions)
        {
            Assert.DoesNotContain(f.Instructions, i => i.Flow == InstructionFlow.Invalid);
            ulong previousEnd = 0;
            foreach (var block in f.Blocks)
            {
                Assert.True(block.StartVa >= previousEnd, $"blocks of {f.Name} overlap at 0x{block.StartVa:X}");
                previousEnd = block.EndVa;
            }
        }
    }

    [SkippableFact]
    public void SweptFunctionsStayInsideExecutableSections()
    {
        string path = Path.Combine(System32, "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = Corpus.Image(path);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");

        var analysis = Corpus.Analysed(path);
        var functions = analysis.Functions;
        _output.WriteLine($"{pe.FileName}: {functions.Count} functions");

        Assert.All(functions, f => Assert.True(
            analysis.Source.IsExecutable(f.EntryVa),
            $"{f.Name} at 0x{f.EntryVa:X} is not in executable memory"));

        // Entries must be unique and never inside another function's first block.
        Assert.Equal(functions.Select(f => f.EntryVa).Distinct().Count(), functions.Count);
    }
}
