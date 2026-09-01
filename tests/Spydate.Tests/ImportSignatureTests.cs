using Spydate.Core.PE;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// Reading what a function takes out of the function itself: <c>ret N</c> on x86, and which argument
/// registers the callee consumes on x64. The real-DLL cases use argument counts that are fixed by the
/// Win32 ABI, so they check the reader against something that cannot quietly drift.
/// </summary>
public class ImportSignatureTests
{
    private const string User32X86 = @"C:\Windows\SysWOW64\user32.dll";
    private const string Kernel32X86 = @"C:\Windows\SysWOW64\kernel32.dll";
    private const string UcrtX64 = @"C:\Windows\System32\ucrtbase.dll";

    private static Function Build(byte[] code, int bitness, ulong baseVa = 0x1000)
    {
        var symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, baseVa, bitness);
        return new FunctionDiscovery(source, new X86Disassembler(bitness, symbols), symbols).Discover(baseVa);
    }

    // ------------------------------------------------------------------
    // Reading the signature out of code
    // ------------------------------------------------------------------

    [Fact]
    public void AStdcallReturnStatesTheArgumentCount()
    {
        // mov eax, 1 ; ret 0Ch  — three arguments removed by the callee.
        var signature = CalleeSignatures.FromCode(Build(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC2, 0x0C, 0x00 }, 32), 32);

        Assert.Equal(SignatureSource.StackCleanup, signature.Source);
        Assert.Equal(3, signature.ArgumentCount);
        Assert.Equal(12, signature.StackCleanupBytes);
    }

    [Fact]
    public void ACdeclReturnSettlesTheCleanupButNotTheCount()
    {
        // A cdecl function with four arguments returns exactly like one with none, so only the cleanup
        // is known. Claiming zero arguments here would empty every call to it.
        var signature = CalleeSignatures.FromCode(Build(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }, 32), 32);

        Assert.True(signature.HasStackCleanup);
        Assert.Equal(0, signature.StackCleanupBytes);
        Assert.False(signature.HasArgumentCount);
    }

    [Fact]
    public void ReturnsThatDisagreeClaimNothing()
    {
        // test ecx,ecx ; je +3 ; ret 4 ; ret 8 — one "function" that is really two.
        var code = new byte[] { 0x85, 0xC9, 0x74, 0x03, 0xC2, 0x04, 0x00, 0xC2, 0x08, 0x00 };

        Assert.Equal(SignatureSource.None, CalleeSignatures.FromCode(Build(code, 32), 32).Source);
    }

    [Fact]
    public void AnUninitialisedSignatureMeansUnknown()
    {
        // A record struct read out of a default field skips the constructor, so the -1 defaults are not
        // there. Everything asks HasX, and HasX has to survive that: reading it as "takes no arguments"
        // silently empties every call in the program.
        CalleeSignature blank = default;

        Assert.False(blank.HasArgumentCount);
        Assert.False(blank.HasStackCleanup);
        Assert.False(blank.IsFloat(0));
        Assert.Equal(CalleeSignature.Unknown, blank);
    }

    [Fact]
    public void AnX64CalleeThatReadsAnXmmRegisterTakesAFloatThere()
    {
        // addsd xmm1, xmm1 ; ret — the second slot arrives in xmm1, so it is a float.
        var signature = CalleeSignatures.FromCode(Build(new byte[] { 0xF2, 0x0F, 0x58, 0xC9, 0xC3 }, 64), 64);

        Assert.Equal(SignatureSource.RegisterUse, signature.Source);
        Assert.Equal(2, signature.ArgumentCount);
        Assert.False(signature.IsFloat(0));
        Assert.True(signature.IsFloat(1));
    }

    [Fact]
    public void ProducingAFloatIsNotTakingOne()
    {
        // cvtsi2sd xmm0, rcx ; ret. Scalar SSE keeps the upper lanes of its destination, so the encoding
        // reads xmm0 — but the value is produced, not consumed. Counting that read would make almost
        // every function that touches a float appear to take one.
        var signature = CalleeSignatures.FromCode(Build(new byte[] { 0xF2, 0x48, 0x0F, 0x2A, 0xC1, 0xC3 }, 64), 64);

        Assert.Equal(1, signature.ArgumentCount);   // rcx is read
        Assert.False(signature.IsFloat(0));
        Assert.Equal(0u, signature.FloatMask);
    }

    [Fact]
    public void ZeroingAnXmmRegisterIsNotReadingIt()
    {
        // xorps xmm0, xmm0 ; ret
        var signature = CalleeSignatures.FromCode(Build(new byte[] { 0x0F, 0x57, 0xC0, 0xC3 }, 64), 64);

        Assert.Equal(SignatureSource.None, signature.Source);
    }

    // ------------------------------------------------------------------
    // Resolving imports against the DLLs on disk
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("user32.dll", "MessageBoxW", 4)]
    [InlineData("user32.dll", "SetWindowPos", 7)]
    [InlineData("user32.dll", "BeginPaint", 2)]
    [InlineData("user32.dll", "InvalidateRect", 3)]
    [InlineData("kernel32.dll", "CreateFileW", 7)]
    [InlineData("kernel32.dll", "ReadFile", 5)]
    [InlineData("kernel32.dll", "CloseHandle", 1)]
    [InlineData("kernel32.dll", "GetProcAddress", 2)]
    [InlineData("gdi32.dll", "TextOutW", 5)]
    public void TheDllStatesHowManyArgumentsAnImportTakes(string module, string export, int expected)
    {
        if (!File.Exists(User32X86))
        {
            return;
        }

        // These counts are the Win32 ABI's, not ours: SetWindowPos takes seven parameters and its x86
        // build therefore ends in `ret 1Ch`. Nothing in the product knows that; this asserts the reader
        // arrives at it from the bytes.
        var signature = new ImportSignatures(32, new[] { @"C:\Windows\SysWOW64" }).Lookup(module, export);

        Assert.Equal(expected, signature.ArgumentCount);
        Assert.Equal(expected * 4, signature.StackCleanupBytes);
    }

    [Fact]
    public void AnExportThatIsOnlyAJumpIntoAnotherModuleIsFollowed()
    {
        if (!File.Exists(Kernel32X86))
        {
            return;
        }

        // kernel32!CloseHandle is one instruction: `jmp [api-ms-win-core-handle-l1-1-0!CloseHandle]`.
        // Reading that as a function with no arguments would lose most of the Win32 API, since almost
        // all of it is exported this way.
        var image = PeImage.Load(Kernel32X86);
        var entry = image.Exports!.Entries.First(e => e.Name == "CloseHandle");
        var symbols = SymbolTable.FromImage(image);
        var thunk = new FunctionDiscovery(new PeCodeSource(image), new X86Disassembler(32, symbols), symbols)
            .Discover(image.RvaToVa(entry.Rva));

        Assert.Equal(1, thunk.InstructionCount);
        Assert.Equal(SignatureSource.None, CalleeSignatures.FromCode(thunk, 32).Source);   // on its own it says nothing
        Assert.Equal(1, new ImportSignatures(32, new[] { @"C:\Windows\SysWOW64" }).Lookup("kernel32.dll", "CloseHandle").ArgumentCount);
    }

    [Fact]
    public void AnX64MathImportDeclaresItsFloatArguments()
    {
        if (!File.Exists(UcrtX64))
        {
            return;
        }

        var signatures = new ImportSignatures(64, new[] { @"C:\Windows\System32" });

        var sqrt = signatures.Lookup("ucrtbase.dll", "sqrt");
        Assert.Equal(1, sqrt.ArgumentCount);
        Assert.True(sqrt.IsFloat(0));

        // ldexp(double, int): the first slot is a float and the second is not, which is exactly the
        // distinction a call site cannot make on its own.
        var ldexp = signatures.Lookup("ucrtbase.dll", "ldexp");
        Assert.Equal(2, ldexp.ArgumentCount);
        Assert.True(ldexp.IsFloat(0));
        Assert.False(ldexp.IsFloat(1));

        Assert.False(signatures.Lookup("ucrtbase.dll", "strlen").IsFloat(0));
    }

    [Fact]
    public void EveryX86CountAgreesWithTheBytesItRemoves()
    {
        if (!Corpus.Has(Corpus.NotepadX86))
        {
            return;
        }

        var image = Corpus.Image(Corpus.NotepadX86);
        var signatures = ImportSignatures.For(image);

        int resolved = 0, total = 0;
        foreach (var module in image.Imports)
        {
            foreach (var function in module.Functions.Where(f => f.Name is not null))
            {
                total++;
                var signature = signatures.Lookup(module.Name, function.Name!);
                if (signature.Source == SignatureSource.None)
                {
                    continue;
                }

                resolved++;
                if (signature.HasArgumentCount)
                {
                    Assert.Equal(signature.ArgumentCount * 4, signature.StackCleanupBytes);
                }
            }
        }

        // Most imports of a modern binary are api set names, so this number is also a check that the
        // schema is being read: without it, four fifths of these resolve to nothing.
        Assert.True(resolved > total * 8 / 10, $"only {resolved} of {total} imports resolved");
    }

    [Fact]
    public void AMissingDllIsNotAFailure()
    {
        var signatures = new ImportSignatures(32, new[] { Path.Combine(Path.GetTempPath(), "spydate-no-such-directory") });

        Assert.Equal(CalleeSignature.Unknown, signatures.Lookup("kernel32.dll", "CloseHandle"));
        Assert.Contains(signatures.Modules, m => !m.IsUsable && m.Name == "kernel32.dll");
    }

    [Theory]
    [InlineData(@"..\..\..\windows\system32\kernel32.dll")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData("")]
    public void AModuleNameIsNeverTreatedAsAPath(string module)
    {
        // Import descriptors are untrusted input: the name is used to build a path, so only a bare file
        // name may ever reach the filesystem.
        var signatures = new ImportSignatures(32, new[] { @"C:\Windows\SysWOW64" });

        Assert.Equal(CalleeSignature.Unknown, signatures.Lookup(module, "CloseHandle"));
    }

    [Fact]
    public void AnOrdinalImportNamesNothingToLookUp()
    {
        var signatures = new ImportSignatures(32, new[] { @"C:\Windows\SysWOW64" });

        Assert.Equal(CalleeSignature.Unknown, signatures.LookupSymbol("kernel32.dll!#42"));
        Assert.Equal(CalleeSignature.Unknown, signatures.LookupSymbol("no-bang-here"));
    }

    // ------------------------------------------------------------------
    // What the signature changes in the output
    // ------------------------------------------------------------------

    [Fact]
    public void PushesAboveTheCalleesArgumentCountBelongToSomeoneElse()
    {
        // push 1 ; push 2 ; push 3 ; call ; ret. The callee takes two, so the third push is an argument
        // of an outer call still on the stack, not a third argument of this one.
        var code = new byte[] { 0x6A, 0x01, 0x6A, 0x02, 0x6A, 0x03, 0xE8, 0x00, 0x00, 0x00, 0x00, 0xC3 };
        var target = 0x100BUL;

        string withCallee = Decompile(code, 32, va => va == target
            ? new CalleeSignature { ArgumentCount = 2, StackCleanupBytes = 8, Source = SignatureSource.StackCleanup }
            : CalleeSignature.Unknown);

        Assert.Contains("(3, 2)", withCallee);
        Assert.DoesNotContain("(3, 2, 1)", withCallee);

        // Without the callee to ask, the call site alone suggests all three.
        Assert.Contains("(3, 2, 1)", Decompile(code, 32, _ => CalleeSignature.Unknown));
    }

    [Fact]
    public void AFloatArgumentIsLookedForInTheRegisterItArrivesIn()
    {
        // mov rcx, 1 ; addsd xmm1, xmm1 ; call ; ret
        var code = new byte[] { 0x48, 0xC7, 0xC1, 0x01, 0x00, 0x00, 0x00, 0xF2, 0x0F, 0x58, 0xC9, 0xE8, 0x00, 0x00, 0x00, 0x00, 0xC3 };
        var target = 0x1010UL;

        string typed = Decompile(code, 64, va => va == target
            ? new CalleeSignature { ArgumentCount = 2, FloatMask = 0b10, Source = SignatureSource.RegisterUse }
            : CalleeSignature.Unknown);

        // The value reaches the call, propagated through the arithmetic that produced it.
        Assert.Contains("(1, xmm1 + xmm1)", typed);

        // Looking for rdx in the second slot instead finds nothing, so the argument disappears - which
        // is how every float argument used to be lost.
        Assert.Contains("sub_1010(1)", Decompile(code, 64, _ => CalleeSignature.Unknown));
    }

    [Fact]
    public void NoCallIsGivenMoreArgumentsThanTheCalleeTakes()
    {
        if (!Corpus.Has(Corpus.NotepadX86))
        {
            return;
        }

        // The guarantee the whole thing rests on: a stdcall callee states its stack argument count
        // exactly, so a call to it can never end up pushing more. Before, a run of pushes belonging to an
        // outer call could be swept into an inner one, and nothing said otherwise.
        //
        // Only imports are checked. A call to a function inside the image can also carry ecx and edx,
        // which `ret N` says nothing about, so there the two counts are not comparable.
        var analysis = Corpus.Analysed(Corpus.NotepadX86);
        var decompiler = new NativeDecompiler(analysis);

        int checkedCalls = 0, described = 0;
        foreach (var function in analysis.Functions.OrderBy(f => f.EntryVa).Take(400))
        {
            foreach (var block in decompiler.Decompile(function).Ir.Blocks)
            {
                foreach (var statement in block.Statements)
                {
                    if (statement is not Decompiler.Native.IR.IrCallStmt { Call.Target: Decompiler.Native.IR.IrSymbol target } call
                        || target.Va == 0)
                    {
                        continue;
                    }

                    if (analysis.Image.SectionFromVa(target.Va) is not { IsExecutable: false })
                    {
                        continue;   // not an IAT slot, so not an import
                    }

                    var signature = analysis.SignatureFor(target.Va);
                    if (!signature.HasArgumentCount)
                    {
                        continue;
                    }

                    checkedCalls++;
                    if (call.Call.Args.Count > 0)
                    {
                        described++;
                    }

                    Assert.True(
                        call.Call.Args.Count <= signature.ArgumentCount,
                        $"{function.Name} calls {target.Name} with {call.Call.Args.Count} arguments, but it takes {signature.ArgumentCount}");
                }
            }
        }

        Assert.True(checkedCalls > 100, $"only {checkedCalls} import calls had a known callee");
        Assert.True(described > checkedCalls / 2, $"only {described} of {checkedCalls} calls recovered any argument");
    }

    private static string Decompile(byte[] code, int bitness, Func<ulong, CalleeSignature> signatures)
    {
        var symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, 0x1000, bitness);
        var function = new FunctionDiscovery(source, new X86Disassembler(bitness, symbols), symbols).Discover(0x1000);
        return new NativeDecompiler(bitness, symbols, signatureFor: signatures).Decompile(function).Text;
    }
}
