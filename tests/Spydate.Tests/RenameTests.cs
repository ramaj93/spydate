using System.Text.RegularExpressions;
using Spydate.Core.PE;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// What a rename has to reach: the symbol table, the discovered function, every call site that mentions
/// it, and the comment column. And what it must not break — clearing a name puts back what analysis found.
/// </summary>
public class RenameTests
{
    private const ulong Caller = 0x140001000;
    private const ulong Callee = 0x14000100A;

    /// <summary>An image whose code calls a second function, so renames can be seen from both ends.</summary>
    private static PeImage TwoFunctions()
    {
        var code = new byte[0x40];
        Array.Fill(code, (byte)0xCC);
        new byte[] { 0xE8, 0x05, 0x00, 0x00, 0x00 }.CopyTo(code, 0x00);  // call 0x14000100A
        new byte[] { 0xC3 }.CopyTo(code, 0x05);                          // ret
        new byte[] { 0x31, 0xC0, 0xC3 }.CopyTo(code, 0x0A);              // xor eax, eax ; ret
        return SyntheticPe.WithSectionData(code);
    }

    private static (BinaryAnalysis Analysis, NativeDecompiler Decompiler) Open(PeImage image)
    {
        var analysis = new BinaryAnalysis(image);
        return (analysis, new NativeDecompiler(analysis));
    }

    private static string Decompile(BinaryAnalysis analysis, NativeDecompiler decompiler, ulong va)
        => decompiler.Decompile(analysis.GetOrDiscoverFunction(va)).Text;

    [Fact]
    public void ANameReachesTheFunctionAndTheSymbolTable()
    {
        var (analysis, _) = Open(TwoFunctions());
        var function = analysis.GetOrDiscoverFunction(Callee);
        Assert.Equal("sub_14000100A", function.Name);

        analysis.Annotations.SetName(Callee, "ReadHeader");

        Assert.Equal("ReadHeader", analysis.NameFor(Callee));
        Assert.True(analysis.TryGetFunction(Callee, out var renamed));
        Assert.Equal("ReadHeader", renamed.Name);
        Assert.Equal("ReadHeader", analysis.Symbols.Get(Callee)?.Name);
    }

    [Fact]
    public void ARenameShowsUpAtTheCallSite()
    {
        var (analysis, decompiler) = Open(TwoFunctions());
        Assert.Contains("sub_14000100A", Decompile(analysis, decompiler, Caller));

        analysis.Annotations.SetName(Callee, "ReadHeader");

        string text = Decompile(analysis, decompiler, Caller);
        Assert.Contains("ReadHeader", text);
        Assert.DoesNotContain("sub_14000100A", text);
    }

    [Fact]
    public void ClearingANameRestoresWhatAnalysisFound()
    {
        var (analysis, _) = Open(TwoFunctions());
        var symbols = analysis.Symbols;
        symbols.Add(new Symbol(Callee, "ExportedName", SymbolKind.Export), overwrite: true);

        analysis.Annotations.SetName(Callee, "MyName");
        Assert.Equal("MyName", analysis.NameFor(Callee));

        analysis.Annotations.SetName(Callee, null);

        Assert.Equal("ExportedName", analysis.NameFor(Callee));
        Assert.Equal(SymbolKind.Export, symbols.Get(Callee)?.Kind);
    }

    [Fact]
    public void ClearingANameAtAnAddressThatHadNoneLeavesNoSymbolBehind()
    {
        var (analysis, _) = Open(TwoFunctions());
        ulong data = 0x140001100;

        analysis.Annotations.SetName(data, "g_flags");
        Assert.NotNull(analysis.Symbols.Get(data));

        analysis.Annotations.SetName(data, null);

        Assert.Null(analysis.Symbols.Get(data));
        Assert.Equal($"loc_{data:X}", analysis.NameFor(data));
    }

    [Fact]
    public void ANameOutranksTheOneASeedCarries()
    {
        // Discovery seeds arrive with names of their own - "EntryPoint", an export, a CRT helper - and
        // those are passed in when the function is first discovered. A rename has to win over them,
        // whichever happens first.
        var (analysis, _) = Open(TwoFunctions());
        analysis.Annotations.SetName(Callee, "ChosenByHand");

        var discovered = analysis.GetOrDiscoverFunction(Callee, "NameFromASeed");

        Assert.Equal("ChosenByHand", discovered.Name);
        Assert.Equal("ChosenByHand", analysis.NameFor(Callee));
    }

    [Fact]
    public void ARenamedFunctionKeepsItsKind()
    {
        var (analysis, _) = Open(TwoFunctions());
        analysis.GetOrDiscoverFunction(Callee);

        analysis.Annotations.SetName(Callee, "ReadHeader");

        Assert.Equal(SymbolKind.Function, analysis.Symbols.Get(Callee)?.Kind);
    }

    [Fact]
    public void AUserCommentIsShownAgainstTheAddress()
    {
        var (analysis, decompiler) = Open(TwoFunctions());
        analysis.Annotations.SetComment(Caller, "entry from the loader");

        string text = Decompile(analysis, decompiler, Caller);

        Assert.Contains("entry from the loader", text);
        Assert.Equal("entry from the loader", analysis.CommentFor(Caller));
    }

    [Fact]
    public void ARenamedLabelIsUsedByTheGotoThatTargetsIt()
    {
        // Two paths converge on a tail neither dominates, so the output keeps one goto and one label.
        var code = new byte[0x20];
        Array.Fill(code, (byte)0xCC);
        new byte[]
        {
            0x85, 0xC9,       // test ecx, ecx
            0x74, 0x07,       // je +7
            0x85, 0xD2,       // test edx, edx
            0x74, 0x05,       // je +5
            0xFF, 0xC0,       // inc eax
            0xC3,             // ret
            0xFF, 0xC8,       // dec eax
            0xFF, 0xC8,       // dec eax
            0xC3,             // ret
        }.CopyTo(code, 0);

        var (analysis, decompiler) = Open(SyntheticPe.WithSectionData(code));
        string before = Decompile(analysis, decompiler, 0x140001000);

        var label = Regex.Match(before, @"loc_([0-9A-F]+):");
        Assert.True(label.Success, before);
        ulong labelVa = ulong.Parse(label.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

        analysis.Annotations.SetName(labelVa, "both_paths_meet");
        string after = Decompile(analysis, decompiler, 0x140001000);

        Assert.Contains("both_paths_meet:", after);
        Assert.Contains("goto both_paths_meet;", after);
        Assert.DoesNotContain(label.Value, after);
    }

    /// <summary>
    /// x64 frame: mov [rsp+8], rcx ; sub rsp, 0x28 ; mov rax, [rsp+0x30] ; add rsp, 0x28 ; ret - the
    /// argument goes into its home slot and comes back out, so the frame pass names a slot.
    /// </summary>
    private static PeImage FunctionWithAnArgument()
    {
        var code = new byte[0x20];
        Array.Fill(code, (byte)0xCC);
        new byte[]
        {
            0x48, 0x89, 0x4C, 0x24, 0x08,
            0x48, 0x83, 0xEC, 0x28,
            0x48, 0x8B, 0x44, 0x24, 0x30,
            0x48, 0x83, 0xC4, 0x28,
            0xC3,
        }.CopyTo(code, 0);
        return SyntheticPe.WithSectionData(code);
    }

    [Fact]
    public void AStackSlotCanBeNamed()
    {
        var image = FunctionWithAnArgument();
        var analysis = new BinaryAnalysis(image);
        var decompiler = new NativeDecompiler(64, analysis.Symbols, annotations: analysis.Annotations);
        var function = analysis.GetOrDiscoverFunction(0x140001000);

        Assert.Contains("arg_0", decompiler.Decompile(function).Text);

        analysis.Annotations.SetLocalName(0x140001000, "arg_0", "count");
        string text = decompiler.Decompile(function).Text;

        Assert.Contains("uint64_t count", text);
        Assert.DoesNotContain("arg_0", text);
    }

    [Fact]
    public void ClearingASlotNameGivesTheGeneratedOneBack()
    {
        var image = FunctionWithAnArgument();
        var analysis = new BinaryAnalysis(image);
        var decompiler = new NativeDecompiler(64, analysis.Symbols, annotations: analysis.Annotations);
        var function = analysis.GetOrDiscoverFunction(0x140001000);

        analysis.Annotations.SetLocalName(0x140001000, "arg_0", "count");
        analysis.Annotations.SetLocalName(0x140001000, "arg_0", null);

        Assert.Contains("arg_0", decompiler.Decompile(function).Text);
        Assert.Null(analysis.Annotations.LocalNameFor(0x140001000, "arg_0"));
    }

    [Fact]
    public void SlotNamesBelongToTheirOwnFunction()
    {
        // arg_0 exists in most functions; naming one must not name them all.
        var analysis = new BinaryAnalysis(FunctionWithAnArgument());
        analysis.Annotations.SetLocalName(0x140001000, "arg_0", "count");

        Assert.Equal("count", analysis.Annotations.LocalNameFor(0x140001000, "arg_0"));
        Assert.Null(analysis.Annotations.LocalNameFor(0x140002000, "arg_0"));
    }

    [Fact]
    public void RenamingIsUndoneAndRedoneWithoutLosingTheOriginal()
    {
        var (analysis, _) = Open(TwoFunctions());
        analysis.Symbols.Add(new Symbol(Callee, "FromPdb", SymbolKind.Function), overwrite: true);

        for (int i = 0; i < 3; i++)
        {
            analysis.Annotations.SetName(Callee, $"Attempt{i}");
            Assert.Equal($"Attempt{i}", analysis.NameFor(Callee));
            analysis.Annotations.SetName(Callee, null);
            Assert.Equal("FromPdb", analysis.NameFor(Callee));
        }
    }
}
