using Spydate.Core.PE;
using Spydate.Core.Symbols;
using Spydate.Disassembly;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>Naming the compiler-generated helpers every MSVC binary contains.</summary>
public class CrtHelperTests
{
    private readonly ITestOutputHelper _output;

    public CrtHelperTests(ITestOutputHelper output) => _output = output;

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    [SkippableFact]
    public void LoadConfigSymbolsAreNamed()
    {
        string path = Path.Combine(System32, "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        Skip.If(pe.LoadConfig is null, "no load config");

        var symbols = new SymbolTable();
        int added = CrtHelpers.ApplyLoadConfigSymbols(pe, symbols);
        _output.WriteLine($"{added} load-config symbols");

        Assert.True(symbols.TryGet(pe.LoadConfig!.SecurityCookieVa, out var cookie));
        Assert.Equal("__security_cookie", cookie.Name);
        Assert.Equal(SymbolKind.Data, cookie.Kind);

        // The guard routines only exist in CFG-instrumented images.
        if (pe.LoadConfig.HasControlFlowGuard)
        {
            var guards = symbols.All.Where(s => s.Name.StartsWith("_guard_", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(guards);
            Assert.All(guards, g => Assert.True(pe.SectionFromVa(g.Va)?.IsExecutable == true));
        }
    }

    [Fact]
    public void LoadConfigSymbolsAreSkippedWithoutALoadConfig()
    {
        var pe = SyntheticPe.WithSectionData(new byte[] { 0x90 });
        Assert.Equal(0, CrtHelpers.ApplyLoadConfigSymbols(pe, new SymbolTable()));
    }

    [SkippableTheory]
    [InlineData(@"System32\ntdll.dll")]
    [InlineData(@"SysWOW64\ntdll.dll")]
    public void StackProbeSignatureMatchesTheExportedProbe(string relative)
    {
        // ntdll exports the probe, which makes it an oracle: the signature must agree with the
        // name the image itself gives that address.
        string path = Path.Combine(Path.GetDirectoryName(System32)!, relative);
        Skip.IfNot(File.Exists(path), $"{relative} not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");

        var probe = pe.Exports?.Entries.FirstOrDefault(e => e.Name is "__chkstk" or "_chkstk" or "_alloca_probe");
        Skip.If(probe is null, "no stack probe export");

        var analysis = new BinaryAnalysis(pe, options: new DiscoveryOptions { SweepGapsForFunctions = false });
        var function = analysis.GetOrDiscoverFunction(pe.RvaToVa(probe!.Rva), probe.Name);
        _output.WriteLine($"{pe.FileName} {pe.Machine}: {probe.Name} has {function.InstructionCount} instructions");

        Assert.Equal(pe.Bitness == 64 ? "__chkstk" : "_chkstk", CrtHelpers.Identify(function, pe));
    }

    [SkippableFact]
    public void HelperNamesAreUniqueWithinAnImage()
    {
        string path = Path.Combine(System32, "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");

        var analysis = new BinaryAnalysis(pe);
        var functions = analysis.DiscoverAll(maxFunctions: 5000);

        var helpers = functions
            .Where(f => f.Name.StartsWith("__", StringComparison.Ordinal) || f.Name.StartsWith("_guard", StringComparison.Ordinal))
            .ToList();
        _output.WriteLine($"{functions.Count} functions; helpers: {string.Join(", ", helpers.Select(h => $"{h.Name}@0x{h.EntryVa:X}"))}");

        // Two functions claiming the same helper name means a signature is matching too much.
        foreach (var group in helpers.GroupBy(h => h.Name))
        {
            Assert.True(group.Count() == 1, $"{group.Key} matched {group.Count()} functions");
        }
    }

    [SkippableFact]
    public void ExportNamesAreNeverOverwrittenByHelperGuesses()
    {
        string path = Path.Combine(System32, "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");
        var exports = pe.Exports!;

        var analysis = new BinaryAnalysis(pe);
        analysis.DiscoverAll(maxFunctions: 3000);

        // Several exports can share an address (aliases), so any of the names at that address is
        // acceptable - what matters is that a helper guess never replaced one.
        var namesByVa = exports.Entries
            .Where(e => !e.IsForwarder && e.Name is not null)
            .GroupBy(e => pe.RvaToVa(e.Rva))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Name!).ToHashSet(StringComparer.Ordinal));

        foreach (var (va, names) in namesByVa.Take(400))
        {
            if (analysis.TryGetFunction(va, out var f))
            {
                Assert.True(names.Contains(f.Name), $"0x{va:X} is exported as {string.Join("/", names)} but was named {f.Name}");
            }
        }
    }
}
