using Spydate.Core.Project;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native.CodeGen;
using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Lifting;
using Spydate.Decompiler.Native.Passes;
using Spydate.Disassembly;

namespace Spydate.Decompiler.Native;

/// <summary>Result of decompiling one native function.</summary>
public sealed record DecompiledFunction(Function Function, IrFunction Ir, string Text, IReadOnlyList<string> Warnings);

/// <summary>Native decompilation pipeline: lift → passes → pseudo-C.</summary>
public sealed class NativeDecompiler
{
    private readonly int _bitness;
    private readonly SymbolTable? _symbols;
    private readonly IReadOnlyList<IIrPass> _passes;
    private readonly Func<ulong, int>? _registerArguments;
    private readonly AnnotationStore? _annotations;

    public NativeDecompiler(
        int bitness,
        SymbolTable? symbols = null,
        IReadOnlyList<IIrPass>? passes = null,
        GlobalNames? names = null,
        Func<ulong, int>? registerArguments = null,
        AnnotationStore? annotations = null,
        Func<ulong, CalleeSignature>? signatureFor = null)
    {
        _bitness = bitness;
        _symbols = symbols;
        _registerArguments = registerArguments;
        _annotations = annotations;
        _passes = passes ?? DefaultPasses(names, registerArguments, annotations, signatureFor);
    }

    public NativeDecompiler(BinaryAnalysis analysis)
        : this(
            analysis.Image.Bitness,
            analysis.Symbols,
            names: GlobalNames.For(analysis),
            registerArguments: va => RegisterArgumentsFor(analysis, va),
            annotations: analysis.Annotations,
            signatureFor: analysis.SignatureFor)
    {
    }

    /// <summary>
    /// How many register arguments the function at <paramref name="va"/> takes, or -1 when it is not a
    /// function this analysis can read - an import thunk, or an address outside the code.
    /// </summary>
    private static int RegisterArgumentsFor(BinaryAnalysis analysis, ulong va)
    {
        if (analysis.Image.Bitness != 32 || analysis.Image.SectionFromVa(va) is not { IsExecutable: true })
        {
            return -1;
        }

        try
        {
            return RegisterUse.FastcallArgumentCount(analysis.GetOrDiscoverFunction(va));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return -1;
        }
    }

    /// <summary>
    /// The standard pipeline. Naming runs before copy propagation so a named global or a string literal
    /// is what gets forwarded into the expression that uses it.
    /// </summary>
    public static IReadOnlyList<IIrPass> DefaultPasses(
        GlobalNames? names = null,
        Func<ulong, int>? registerArguments = null,
        AnnotationStore? annotations = null,
        Func<ulong, CalleeSignature>? signatureFor = null)
    {
        var passes = new List<IIrPass> { new StackFramePass(signatureFor) };
        if (registerArguments is not null)
        {
            passes.Add(new X86RegisterArgumentsPass(registerArguments));
        }

        if (names is not null)
        {
            passes.Add(new GlobalNamingPass(names));
        }

        passes.Add(new CopyPropagationPass());
        passes.Add(new AlgebraicSimplificationPass());
        passes.Add(new DeadCodeEliminationPass());
        passes.Add(new ReturnValuePass());
        if (annotations is not null)
        {
            passes.Add(new LocalNamingPass(annotations));
        }

        return passes;
    }

    public DecompiledFunction Decompile(Function function)
    {
        var lifter = new X86Lifter(_bitness, _symbols);
        var ir = lifter.Lift(function);

        // A function that reads ecx before writing it was handed something in it; the same analysis that
        // gives calls their register arguments gives this one its register parameters.
        if (_registerArguments?.Invoke(function.EntryVa) is > 0 and var registers)
        {
            ir.RegisterParameters = registers;
        }
        foreach (var pass in _passes)
        {
            try
            {
                pass.Run(ir);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
            {
                ir.Warnings.Add($"pass '{pass.Name}' failed: {ex.Message}");
            }
        }

        var text = new PseudoCEmitter { Annotations = _annotations }.Emit(ir);
        var warnings = ir.Warnings.Concat(function.Notes).ToList();
        return new DecompiledFunction(function, ir, text, warnings);
    }
}
