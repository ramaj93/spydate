using System.Diagnostics;
using Spydate.Core.PE;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>End-to-end smoke tests over real Windows binaries (skipped when unavailable).</summary>
public class RealBinaryTests
{
    private readonly ITestOutputHelper _output;

    public RealBinaryTests(ITestOutputHelper output) => _output = output;

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    [SkippableTheory]
    [InlineData("notepad.exe")]
    [InlineData("kernel32.dll")]
    [InlineData(@"..\SysWOW64\notepad.exe")]
    [InlineData(@"..\SysWOW64\kernel32.dll")]
    public void DiscoverAndDecompileDoesNotThrow(string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(System32, fileName));
        Skip.IfNot(File.Exists(path), $"{fileName} not found");

        // Deliberately not the shared corpus analysis. This is the smoke test for discovery itself, so
        // it should run discovery rather than inherit someone else's; and 32-bit kernel32 has no unwind
        // table, which makes a full sweep of it cost more than the cap this needs.
        var pe = Corpus.Image(path);
        Skip.IfNot(pe.IsX86Family, $"{fileName} is {pe.Machine}, not x86/x64");

        var sw = Stopwatch.StartNew();
        var analysis = new BinaryAnalysis(pe);
        var functions = analysis.DiscoverAll(maxFunctions: 400);
        sw.Stop();
        _output.WriteLine($"{fileName}: {functions.Count} functions in {sw.ElapsedMilliseconds} ms");

        Assert.NotEmpty(functions);
        Assert.All(functions, f => Assert.NotEmpty(f.Blocks));

        var decompiler = new NativeDecompiler(analysis);
        sw.Restart();
        int decompiled = 0;
        foreach (var f in functions.Take(150))
        {
            var result = decompiler.Decompile(f);
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
            Assert.Contains(f.Name.Replace('!', '_'), result.Text.Split('\n')[0] + result.Text);
            decompiled++;
        }

        sw.Stop();
        _output.WriteLine($"{fileName}: decompiled {decompiled} functions in {sw.ElapsedMilliseconds} ms");

        var entry = functions.FirstOrDefault(f => f.EntryVa == pe.EntryPointVa);
        if (entry is not null)
        {
            foreach (var block in entry.Blocks.Take(6))
            {
                _output.WriteLine($"loc_{block.StartVa:X}:");
                foreach (var ins in block.Instructions)
                {
                    _output.WriteLine($"  {ins.Va:X}  {ins.Mnemonic,-8} {analysis.Disassembler.FormatOperands(ins.Native)}");
                }
            }

            _output.WriteLine(decompiler.Decompile(entry).Text);
        }
    }

    [SkippableFact]
    public void LinearDisassemblyOfTextSectionStart()
    {
        string path = Path.Combine(System32, "notepad.exe");
        Skip.IfNot(File.Exists(path), "notepad.exe not found");
        var pe = PeImage.Load(path);
        Skip.IfNot(pe.IsX86Family, "not x86/x64");

        var analysis = new BinaryAnalysis(pe);
        var text = pe.Sections.First(s => s.Name == ".text");
        var insns = analysis.DisassembleRange(pe.RvaToVa(text.VirtualAddress), 4096);

        Assert.NotEmpty(insns);
        Assert.True(insns.Count(i => i.Flow == InstructionFlow.Invalid) < insns.Count / 4, "too many invalid instructions in .text");
    }
}
