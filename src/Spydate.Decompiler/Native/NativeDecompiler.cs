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

    public NativeDecompiler(int bitness, SymbolTable? symbols = null, IReadOnlyList<IIrPass>? passes = null, GlobalNames? names = null)
    {
        _bitness = bitness;
        _symbols = symbols;
        _passes = passes ?? DefaultPasses(names);
    }

    public NativeDecompiler(BinaryAnalysis analysis)
        : this(analysis.Image.Bitness, analysis.Symbols, names: GlobalNames.For(analysis))
    {
    }

    /// <summary>
    /// The standard pipeline. Naming runs before copy propagation so a named global or a string literal
    /// is what gets forwarded into the expression that uses it.
    /// </summary>
    public static IReadOnlyList<IIrPass> DefaultPasses(GlobalNames? names = null)
    {
        var passes = new List<IIrPass> { new StackFramePass() };
        if (names is not null)
        {
            passes.Add(new GlobalNamingPass(names));
        }

        passes.Add(new CopyPropagationPass());
        passes.Add(new AlgebraicSimplificationPass());
        passes.Add(new DeadCodeEliminationPass());
        return passes;
    }

    public DecompiledFunction Decompile(Function function)
    {
        var lifter = new X86Lifter(_bitness, _symbols);
        var ir = lifter.Lift(function);
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

        var text = new PseudoCEmitter().Emit(ir);
        var warnings = ir.Warnings.Concat(function.Notes).ToList();
        return new DecompiledFunction(function, ir, text, warnings);
    }
}
