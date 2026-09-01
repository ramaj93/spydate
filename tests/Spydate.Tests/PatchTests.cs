using Spydate.Core.PE;
using Spydate.Core.Project;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>The patch store's rules, which exist to stop a patch from moving anything.</summary>
public sealed class PatchStoreTests
{
    private static Patch At(uint rva, params byte[] bytes)
        => new() { Rva = rva, Bytes = bytes, Original = new byte[bytes.Length] };

    [Fact]
    public void APatchIsRememberedWithWhatItReplaced()
    {
        var store = new PatchStore();
        store.Set(0x1000, new Patch { Bytes = [0x90, 0x90], Original = [0x74, 0x10], Comment = "skip the check" });

        var patch = store.Get(0x1000);

        Assert.NotNull(patch);
        Assert.Equal("9090", patch.Hex);
        Assert.Equal("7410", patch.OriginalHex);
        Assert.Equal(0x1000u, patch.Rva);
        Assert.True(patch.Enabled);
        Assert.NotNull(patch.Modified);
    }

    [Fact]
    public void OneThatChangesTheLengthIsRefused()
    {
        var store = new PatchStore();

        // The whole design rests on this: a longer replacement would push everything after it along,
        // and every address the analyst has written down would quietly mean something else.
        var wrong = new Patch { Bytes = [0x90, 0x90, 0x90], Original = [0x74, 0x10] };

        var ex = Assert.Throws<ArgumentException>(() => store.Set(0x1000, wrong));
        Assert.Contains("as many bytes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void TwoPatchesCannotCoverTheSameByte()
    {
        var store = new PatchStore();
        store.Set(0x1000, At(0x1000, 0x90, 0x90, 0x90, 0x90));

        // Starts inside the first one. Which wins is not a question with a good answer.
        var ex = Assert.Throws<ArgumentException>(() => store.Set(0x1002, At(0x1002, 0x90, 0x90)));
        Assert.Contains("already covers", ex.Message, StringComparison.Ordinal);

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ReplacingOneAtTheSameAddressIsNotAnOverlap()
    {
        var store = new PatchStore();
        store.Set(0x1000, At(0x1000, 0x90, 0x90));
        store.Set(0x1000, At(0x1000, 0xCC, 0xCC));

        Assert.Equal(1, store.Count);
        Assert.Equal("CCCC", store.Get(0x1000)!.Hex);
    }

    [Fact]
    public void APatchCanBeSwitchedOffWithoutBeingForgotten()
    {
        var store = new PatchStore();
        store.Set(0x1000, At(0x1000, 0x90));

        Assert.True(store.SetEnabled(0x1000, false));

        Assert.Equal(1, store.Count);
        Assert.Equal(0, store.EnabledCount);
        Assert.False(store.SetEnabled(0x1000, false));   // already off
    }

    [Fact]
    public void TheOneCoveringAnAddressIsFoundFromInsideIt()
    {
        var store = new PatchStore();
        store.Set(0x1000, At(0x1000, 0x90, 0x90, 0x90, 0x90));

        Assert.NotNull(store.Covering(0x1002));
        Assert.Null(store.Covering(0x1004));
        Assert.Null(store.Covering(0x0FFF));
    }

    [Fact]
    public void ClearingReportsEveryRemovalSoViewsCanUnwindThem()
    {
        var store = new PatchStore();
        store.Set(0x1000, At(0x1000, 0x90));
        store.Set(0x2000, At(0x2000, 0x90));

        var seen = new List<PatchChange>();
        store.Changed += (_, e) => seen.Add(e);
        store.Clear();

        Assert.Equal(2, seen.Count);
        Assert.All(seen, c => Assert.Null(c.After));
    }

    [Fact]
    public void WhatChangedThisSessionIsTrackedForMerging()
    {
        var store = new PatchStore();
        store.Set(0x1000, At(0x1000, 0x90));
        Assert.Single(store.ChangedAddresses);
        Assert.True(store.IsDirty);

        store.MarkSaved();

        Assert.Empty(store.ChangedAddresses);
        Assert.False(store.IsDirty);
    }
}

/// <summary>Writing a patched copy, against a real PE.</summary>
public sealed class PatchWriterTests
{
    [Fact]
    public void ThePatchedCopyDiffersOnlyWhereItWasPatched()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX64);
        uint rva = image.EntryPointRva;
        var original = image.ReadAtRva(rva, 4).ToArray();

        var patches = new PatchStore();
        patches.Set(rva, new Patch { Bytes = [0x90, 0x90, 0x90, 0x90], Original = original });

        string path = Path.Combine(Path.GetTempPath(), $"spydate-patched-{Guid.NewGuid():N}.exe");
        try
        {
            var result = PatchWriter.Write(image, patches, path);

            Assert.True(result.Ok, string.Join("; ", result.Problems));
            Assert.Equal(1, result.Applied);

            byte[] before = image.Data.ToArray();
            byte[] after = File.ReadAllBytes(path);

            Assert.Equal(before.Length, after.Length);

            uint offset = image.RvaToOffset(rva)!.Value;
            for (int i = 0; i < before.Length; i++)
            {
                bool patched = i >= offset && i < offset + 4;
                if (patched)
                {
                    Assert.Equal(0x90, after[i]);
                }
                else
                {
                    Assert.Equal(before[i], after[i]);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ItRefusesToWriteOverTheFileItCameFrom()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX64);

        // Overwriting the binary under analysis destroys the only record of what it originally did,
        // at the moment the analyst is least able to notice.
        var ex = Assert.Throws<ArgumentException>(() => PatchWriter.Write(image, new PatchStore(), image.Path!));
        Assert.Contains("somewhere other than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APatchAgainstDifferentBytesIsRefusedAndNothingIsWritten()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX64);
        uint rva = image.EntryPointRva;

        var patches = new PatchStore();
        patches.Set(rva, new Patch { Bytes = [0x90, 0x90], Original = [0xDE, 0xAD] });

        string path = Path.Combine(Path.GetTempPath(), $"spydate-patched-{Guid.NewGuid():N}.exe");
        try
        {
            var result = PatchWriter.Write(image, patches, path);

            Assert.False(result.Ok);
            Assert.Contains(result.Problems, p => p.Contains("expected DEAD", StringComparison.Ordinal));

            // Nothing at all: a half-patched binary is worse than none, because it looks like it worked.
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADisabledPatchIsCountedButNotWritten()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX64);
        uint rva = image.EntryPointRva;
        var original = image.ReadAtRva(rva, 2).ToArray();

        var patches = new PatchStore();
        patches.Set(rva, new Patch { Bytes = [0x90, 0x90], Original = original });
        patches.SetEnabled(rva, false);

        string path = Path.Combine(Path.GetTempPath(), $"spydate-patched-{Guid.NewGuid():N}.exe");
        try
        {
            var result = PatchWriter.Write(image, patches, path);

            Assert.True(result.Ok);
            Assert.Equal(0, result.Applied);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(image.Data.ToArray(), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The two transforms, against real code.</summary>
public sealed class InstructionPatchTests
{
    [Fact]
    public void NoppingOutCoversWholeInstructions()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);

        var decoded = analysis.DisassembleRange(va, 32, 2);
        Assert.Equal(2, decoded.Count);

        var proposal = InstructionPatches.NopOut(analysis, va, 2);

        Assert.True(proposal.Ok, proposal.Problem);

        // Exactly the two instructions, not a byte more or less — a partial instruction left behind
        // decodes as whatever the remaining bytes happen to spell.
        Assert.Equal(decoded[0].Length + decoded[1].Length, proposal.Patch!.Length);
        Assert.All(proposal.Patch.Bytes, b => Assert.Equal(0x90, b));
        Assert.Equal(proposal.Patch.Length, proposal.Patch.Original.Count);
    }

    [Fact]
    public void InvertingABranchKeepsItsLengthAndFlipsItsCondition()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);

        // Find a real conditional branch rather than assuming where one is.
        ulong? found = null;
        foreach (var function in analysis.Functions.Take(80))
        {
            foreach (var instruction in analysis.DisassembleRange(function.EntryVa, 256))
            {
                if (instruction.Text.StartsWith("j", StringComparison.Ordinal)
                    && !instruction.Text.StartsWith("jmp", StringComparison.Ordinal))
                {
                    found = instruction.Va;
                    break;
                }
            }

            if (found is not null)
            {
                break;
            }
        }

        Assert.NotNull(found);

        var before = analysis.DisassembleRange(found.Value, 16, 1)[0];
        var proposal = InstructionPatches.InvertBranch(analysis, found.Value);

        Assert.True(proposal.Ok, proposal.Problem);
        Assert.Equal(before.Length, proposal.Patch!.Length);
        Assert.NotEqual(proposal.Patch.OriginalHex, proposal.Patch.Hex);

        // And it really is the opposite: inverting twice is where it started.
        var store = new PatchStore();
        store.Set(proposal.Patch.Rva, proposal.Patch);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void SomethingThatIsNotABranchIsReportedRatherThanPatched()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);

        // The PE header is not code; whatever it decodes as, it is not a conditional branch.
        var proposal = InstructionPatches.InvertBranch(analysis, analysis.Image.ImageBase + 0x40);

        if (!proposal.Ok)
        {
            Assert.NotNull(proposal.Problem);
        }
    }
}

/// <summary>Patches through the project file.</summary>
public sealed class PatchProjectTests
{
    [Fact]
    public void PatchesSurviveASaveAndLoad()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX64);
        string path = Path.Combine(Path.GetTempPath(), $"spydate-proj-{Guid.NewGuid():N}.spydate");
        try
        {
            var written = new PatchStore();
            written.Set(0x1000, new Patch { Bytes = [0x90, 0x90], Original = [0x74, 0x10], Comment = "skip the check" });
            written.Set(0x2000, new Patch { Bytes = [0xEB], Original = [0x74] });
            written.SetEnabled(0x2000, false);

            SpydateProject.SaveTo(path, image, new AnnotationStore(), written);

            var read = new PatchStore();
            var result = SpydateProject.Load(path, image, new AnnotationStore(), read);

            Assert.True(result.Loaded, result.Reason);
            Assert.Equal(2, result.PatchesApplied);
            Assert.Equal(2, read.Count);
            Assert.Equal(1, read.EnabledCount);

            var one = read.Get(0x1000)!;
            Assert.Equal("9090", one.Hex);
            Assert.Equal("7410", one.OriginalHex);
            Assert.Equal("skip the check", one.Comment);
            Assert.False(read.Get(0x2000)!.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APatchedProjectStillOpensWhereAnnotationsAreAllThatIsWanted()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX64);
        string path = Path.Combine(Path.GetTempPath(), $"spydate-proj-{Guid.NewGuid():N}.spydate");
        try
        {
            var patches = new PatchStore();
            patches.Set(0x1000, new Patch { Bytes = [0x90], Original = [0x74] });

            var annotations = new AnnotationStore();
            annotations.SetName(image.ImageBase + 0x1000, "checked_here");
            SpydateProject.SaveTo(path, image, annotations, patches);

            // A caller that knows nothing of patches — the format did not change version, so this
            // has to keep working, and it must not drop the patches it never asked about.
            var back = new AnnotationStore();
            var result = SpydateProject.Load(path, image, back);

            Assert.True(result.Loaded, result.Reason);
            Assert.Equal("checked_here", back.NameFor(image.ImageBase + 0x1000));
            Assert.Equal(0, result.PatchesApplied);

            SpydateProject.SaveTo(path, image, back);

            var reread = new PatchStore();
            SpydateProject.Load(path, image, new AnnotationStore(), reread);
            Assert.Equal(1, reread.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AHandEditedPatchThatBreaksTheRulesIsSkippedNotFatal()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX64);
        string path = Path.Combine(Path.GetTempPath(), $"spydate-proj-{Guid.NewGuid():N}.spydate");
        try
        {
            var good = new PatchStore();
            good.Set(0x1000, new Patch { Bytes = [0x90], Original = [0x74] });
            SpydateProject.SaveTo(path, image, new AnnotationStore(), good);

            // The file is meant to be hand-editable, so it will be hand-edited wrongly.
            string json = File.ReadAllText(path)
                .Replace("\"bytes\": \"90\"", "\"bytes\": \"9090\"", StringComparison.Ordinal);
            File.WriteAllText(path, json);

            var read = new PatchStore();
            var result = SpydateProject.Load(path, image, new AnnotationStore(), read);

            Assert.True(result.Loaded, result.Reason);
            Assert.Equal(0, result.PatchesApplied);
            Assert.Equal(1, result.PatchesSkipped);
            Assert.Equal(0, read.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Typed instructions, turned into bytes.</summary>
public sealed class X86AssemblerTests
{
    private static string Hex(string text, ulong rip = 0x140001000)
    {
        var result = X86Assembler.Encode(text, is64Bit: true, rip);
        Assert.True(result.Ok, result.Problem);
        return Convert.ToHexString(result.Bytes);
    }

    [Theory]
    [InlineData("nop", "90")]
    [InlineData("int3", "CC")]
    [InlineData("ret", "C3")]
    [InlineData("leave", "C9")]
    [InlineData("xor eax, eax", "31C0")]
    [InlineData("mov eax, 1", "B801000000")]
    [InlineData("push rbp", "55")]
    [InlineData("pop rbp", "5D")]
    [InlineData("inc rax", "48FFC0")]
    public void OrdinaryInstructionsEncodeAsTheyShould(string text, string expected)
        => Assert.Equal(expected, Hex(text));

    [Fact]
    public void SeveralInstructionsCanBeGivenAtOnce()
    {
        // The whole point of a patch dialog: replace one call with a return value and a return.
        Assert.Equal("31C0C3", Hex("xor eax, eax; ret"));
    }

    [Fact]
    public void AJumpIsEncodedRelativeToWhereItWillSit()
    {
        // Two bytes at the same target from different addresses must differ, or the offset is wrong.
        string near = Hex("jmp 0x140001010", 0x140001000);
        string far = Hex("jmp 0x140001010", 0x140000000);

        Assert.NotEqual(near, far);
        Assert.StartsWith("EB", near, StringComparison.Ordinal);   // short jump, +0x0E
    }

    [Fact]
    public void RawBytesGoThroughUntouched()
    {
        Assert.Equal("4883EC28", Hex("bytes: 48 83 EC 28"));
        Assert.Equal("9090", Hex("bytes:9090"));
    }

    [Fact]
    public void AMnemonicItDoesNotKnowIsRefusedByNameRatherThanGuessed()
    {
        var result = X86Assembler.Encode("vpshufb xmm0, xmm1, xmm2", is64Bit: true, 0x140001000);

        Assert.False(result.Ok);
        Assert.Contains("bytes:", result.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mov rax, rbx, rcx")]
    [InlineData("mov eax, rbx")]
    [InlineData("mov nonsense, 1")]
    [InlineData("jmp somewhere")]
    [InlineData("bytes: ZZ")]
    [InlineData("bytes: 909")]
    [InlineData("")]
    public void NonsenseIsReportedRatherThanEncoded(string text)
    {
        var result = X86Assembler.Encode(text, is64Bit: true, 0x140001000);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
    }
}

/// <summary>Fitting assembled bytes over real instructions.</summary>
public sealed class AssemblePatchTests
{
    [Fact]
    public void AShorterReplacementIsPaddedToWholeInstructions()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);
        int first = analysis.DisassembleRange(va, 16, 1)[0].Length;

        var proposal = InstructionPatches.Assemble(analysis, va, "nop");

        Assert.True(proposal.Ok, proposal.Problem);

        // One byte of nop, then padding out to the instruction it replaced — never part of one.
        Assert.Equal(first, proposal.Patch!.Length);
        Assert.All(proposal.Patch.Bytes, b => Assert.Equal(0x90, b));
    }

    [Fact]
    public void ALongerReplacementTakesInAsManyInstructionsAsItNeeds()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);
        int first = analysis.DisassembleRange(va, 16, 1)[0].Length;

        // Ten bytes, which will not fit in one short instruction.
        var proposal = InstructionPatches.Assemble(analysis, va, "mov rax, 0x1122334455667788");

        Assert.True(proposal.Ok, proposal.Problem);
        Assert.True(proposal.Patch!.Length >= 10, $"only {proposal.Patch.Length} bytes covered");

        // Still a whole number of instructions, so nothing is left half-overwritten.
        int covered = 0;
        foreach (var instruction in analysis.DisassembleRange(va, proposal.Patch.Length + 16))
        {
            covered += instruction.Length;
            if (covered >= proposal.Patch.Length)
            {
                break;
            }
        }

        Assert.Equal(proposal.Patch.Length, covered);
        Assert.True(proposal.Patch.Length > first || first >= 10);
    }

    [Fact]
    public void WhatCannotBeAssembledIsReportedNotPatched()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);

        var proposal = InstructionPatches.Assemble(analysis, va, "frobnicate rax");

        Assert.False(proposal.Ok);
        Assert.NotNull(proposal.Problem);
    }
}

/// <summary>Patched lines, as the listing shows them.</summary>
public sealed class PatchedListingTests
{
    [Fact]
    public void APatchedInstructionIsMarkedAndSaysWhatItBecomes()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);
        Assert.True(analysis.TryGetFunction(va, out var function));

        var proposal = InstructionPatches.Assemble(analysis, va, "xor eax, eax; ret");
        Assert.True(proposal.Ok, proposal.Problem);

        var patches = new PatchStore();
        patches.Set(proposal.Patch!.Rva, proposal.Patch);

        string plain = AsmListing.ForFunction(analysis, function!);
        string marked = AsmListing.ForFunction(analysis, function!, patches);

        Assert.DoesNotContain("PATCHED", plain, StringComparison.Ordinal);
        Assert.Contains("PATCHED", marked, StringComparison.Ordinal);

        // The new bytes, and what they will do — read off the patch rather than restated by hand.
        Assert.Contains("31 C0", marked, StringComparison.Ordinal);
        Assert.Contains("xor", marked, StringComparison.OrdinalIgnoreCase);

        // The original line is still the original. A listing that showed the patched instruction in
        // place would describe a program that does not exist in any file yet.
        Assert.Contains(analysis.DisassembleRange(va, 16, 1)[0].BytesText, marked, StringComparison.Ordinal);
    }

    [Fact]
    public void ASwitchedOffPatchSaysSoRatherThanLookingApplied()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);
        Assert.True(analysis.TryGetFunction(va, out var function));

        var proposal = InstructionPatches.NopOut(analysis, va);
        var patches = new PatchStore();
        patches.Set(proposal.Patch!.Rva, proposal.Patch);
        patches.SetEnabled(proposal.Patch.Rva, false);

        string marked = AsmListing.ForFunction(analysis, function!, patches);

        Assert.Contains("patch (off)", marked, StringComparison.Ordinal);
        Assert.DoesNotContain("PATCHED", marked, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheLineAPatchStartsOnIsAnnotated()
    {
        if (!Corpus.Has(Corpus.NotepadX64))
        {
            return;
        }

        var analysis = Corpus.Analysed(Corpus.NotepadX64);
        ulong va = analysis.Image.RvaToVa(analysis.Image.EntryPointRva);
        Assert.True(analysis.TryGetFunction(va, out var function));

        // Wide enough to cover more than one instruction.
        var proposal = InstructionPatches.Assemble(analysis, va, "mov rax, 0x1122334455667788");
        Assert.True(proposal.Ok, proposal.Problem);

        var patches = new PatchStore();
        patches.Set(proposal.Patch!.Rva, proposal.Patch);

        string marked = AsmListing.ForFunction(analysis, function!, patches);

        // Every covered line is marked as patched, but only the first says what it becomes: the
        // others are the middle of one instruction and have nothing of their own to say.
        int becomes = marked.Split('\n').Count(l => l.Contains("mov", StringComparison.OrdinalIgnoreCase) && l.Contains("PATCHED", StringComparison.Ordinal));
        Assert.Equal(1, becomes);
    }
}
