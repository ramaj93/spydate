using Spydate.Core.PE;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>Covers the directories parsed for analysis depth: relocations, TLS, load config, resources, Rich.</summary>
public class PeDirectoryTests
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string Kernel32 = Path.Combine(System32, "kernel32.dll");
    private static readonly string Notepad = Path.Combine(System32, "notepad.exe");
    private static readonly string User32 = Path.Combine(System32, "user32.dll");

    [SkippableFact]
    public void Kernel32_RelocationsCoverMappedRvas()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");
        var pe = PeImage.Load(Kernel32);
        Skip.If(pe.Relocations.Count == 0, "image has no base relocations");

        Assert.All(pe.Relocations, block =>
        {
            Assert.True(block.BlockSize >= 8);
            Assert.All(block.Entries, e =>
            {
                // Every fix-up must land inside its own 4 KiB page and inside a real section.
                Assert.InRange(e.Rva, block.PageRva, block.PageRva + 0xFFF);
                Assert.NotNull(pe.SectionFromRva(e.Rva));
            });
        });

        // x64 images relocate 64-bit pointers; x86 images patch 32-bit halves.
        var expected = pe.Is64Bit ? RelocationType.Dir64 : RelocationType.HighLow;
        Assert.Contains(pe.Relocations.SelectMany(b => b.Entries), e => e.Type == expected);
        Assert.True(pe.RelocationCount > 0);
        Assert.Empty(pe.Warnings);
    }

    [SkippableFact]
    public void Kernel32_LoadConfigExposesControlFlowGuardTargets()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");
        var pe = PeImage.Load(Kernel32);

        var config = pe.LoadConfig;
        Assert.NotNull(config);
        Assert.True(config!.Size >= 0x40);
        Assert.NotEqual(0UL, config.SecurityCookieVa);
        Assert.NotNull(pe.VaToOffset(config.SecurityCookieVa));

        // The stride nibble lives in the same DWORD as the flags; if it is not masked off the
        // enum stops matching names and ToString() degrades to a raw number in the UI.
        Assert.InRange(config.GuardCfFunctionTableStride, 4, 19);
        Assert.DoesNotContain(config.GuardFlags.ToString(), "0123456789");

        // The stride nibble shares the DWORD with the flags; if it is not masked off the enum
        // stops matching names and ToString() degrades to a raw number in the UI.
        Assert.InRange(config.GuardCfFunctionTableStride, 4, 19);
        Assert.All("0123456789", digit => Assert.DoesNotContain(digit, config.GuardFlags.ToString()));

        Skip.IfNot(config.HasControlFlowGuard, "image is not CFG-instrumented");
        Assert.Contains(nameof(GuardFlags.CfInstrumented), config.GuardFlags.ToString());
        Assert.Contains("CfInstrumented", config.GuardFlags.ToString());
        Assert.NotEmpty(config.GuardCfFunctionRvas);
        Assert.All(config.GuardCfFunctionRvas, rva =>
        {
            var section = pe.SectionFromRva(rva);
            Assert.NotNull(section);
            Assert.True(section!.IsExecutable, $"CFG target 0x{rva:X} is not in executable memory");
        });
    }

    [SkippableFact]
    public void Kernel32_ResourceTreeIsWellFormed()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");
        var pe = PeImage.Load(Kernel32);

        var root = pe.Resources;
        Assert.NotNull(root);
        Assert.True(root!.IsDirectory);
        Assert.NotEmpty(root.Children!);

        int leaves = 0;
        void Walk(ResourceNode node, int depth)
        {
            Assert.Equal(depth, node.Level);
            if (node.IsDirectory)
            {
                Assert.True(depth < 3, "resource tree is deeper than type/name/language");
                foreach (var child in node.Children!)
                {
                    Walk(child, depth + 1);
                }

                return;
            }

            leaves++;
            Assert.True(node.DataSize > 0);
            Assert.NotNull(pe.RvaToOffset(node.DataRva));
            Assert.Equal(node.DataSize, (uint)pe.ReadAtRva(node.DataRva, (int)node.DataSize).Length);
        }

        Walk(root, 0);
        Assert.True(leaves > 0, "no resource data entries");

        // System DLLs always carry a version resource.
        Assert.Contains(root.Children!, c => c.Id == (uint)ResourceType.Version);
    }

    [SkippableFact]
    public void User32_ResourceNamesAndTypesAreReadable()
    {
        Skip.IfNot(File.Exists(User32), "user32.dll not found");
        var pe = PeImage.Load(User32);
        Skip.If(pe.Resources is null, "no resources");

        var types = pe.Resources!.Children!;
        Assert.All(types, t => Assert.False(string.IsNullOrEmpty(t.DisplayName)));
        Assert.Contains(types, t => t.DisplayName is nameof(ResourceType.Version) or nameof(ResourceType.String) or nameof(ResourceType.Icon));
    }

    [SkippableFact]
    public void Notepad_RichHeaderDecodes()
    {
        Skip.IfNot(File.Exists(Notepad), "notepad.exe not found");
        var pe = PeImage.Load(Notepad);
        Skip.If(pe.RichHeader is null, "image has no Rich header");

        var rich = pe.RichHeader!;
        Assert.NotEqual(0u, rich.Checksum);
        Assert.NotEmpty(rich.Entries);
        Assert.True(rich.Offset >= 0x40 && rich.Offset < pe.DosHeader.NewHeaderOffset);
        Assert.All(rich.Entries, e =>
        {
            Assert.True(e.UseCount > 0);
            Assert.Contains($"0x{e.ProductId:X4}", e.Description);
        });
    }

    [SkippableFact]
    public void TlsCallbacksAndGuardTargetsBecomeDiscoverySeeds()
    {
        Skip.IfNot(File.Exists(Kernel32), "kernel32.dll not found");
        var pe = PeImage.Load(Kernel32);
        Skip.IfNot(pe.IsX86Family, $"{pe.Machine} is not x86/x64");

        var analysis = new BinaryAnalysis(pe);
        var seeds = analysis.GetSeeds();

        Assert.NotEmpty(seeds);
        Assert.Equal(seeds.Select(s => s.Va).Distinct().Count(), seeds.Count);
        Assert.All(seeds, s => Assert.True(analysis.Source.IsExecutable(s.Va)));

        if (pe.Tls is { CallbackVas.Count: > 0 } tls)
        {
            Assert.Contains(seeds, s => s.Va == tls.CallbackVas[0] && s.Name == "TlsCallback0");
        }

        if (pe.LoadConfig is { HasControlFlowGuard: true } config)
        {
            // The CFG table should widen the seed set well past entry point + exports.
            ulong sample = pe.RvaToVa(config.GuardCfFunctionRvas[0]);
            Assert.Contains(seeds, s => s.Va == sample);
        }
    }

    [Fact]
    public void WellFormedRelocationBlockParses()
    {
        var pe = SyntheticPe.WithRelocationBlock(pageRva: 0x1000, blockSize: 16);

        var block = Assert.Single(pe.Relocations);
        Assert.Equal(0x1000u, block.PageRva);
        // Three Dir64 fix-ups; the IMAGE_REL_BASED_ABSOLUTE padding entry is dropped.
        Assert.Equal(new uint[] { 0x1010, 0x1020, 0x1030 }, block.Entries.Select(e => e.Rva));
        Assert.All(block.Entries, e => Assert.Equal(RelocationType.Dir64, e.Type));
    }

    [Fact]
    public void ZeroSizedRelocationBlockIsRejectedNotLooped()
    {
        // A block claiming size 0 would spin forever if the parser trusted it.
        var pe = SyntheticPe.WithRelocationBlock(pageRva: 0x1000, blockSize: 0);

        Assert.Empty(pe.Relocations);
        Assert.Contains(pe.Warnings, w => w.Contains("invalid size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OversizedRelocationBlockStopsAtEndOfFile()
    {
        // The block claims far more fix-ups than the file holds; parsing must clamp, warn and stop.
        var pe = SyntheticPe.WithRelocationBlock(pageRva: 0x1000, blockSize: 0x4000);

        var block = Assert.Single(pe.Relocations);
        Assert.All(block.Entries, e => Assert.InRange(e.Rva, 0x1000u, 0x1FFFu));
        Assert.Contains(pe.Warnings, w => w.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }
}
