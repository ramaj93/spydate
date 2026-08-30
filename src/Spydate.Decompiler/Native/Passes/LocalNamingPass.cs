using Spydate.Core.Project;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// Replaces the generated name of a stack slot with the one the user gave it. The slots belong to the
/// function, not to an address, so they are keyed by the generated name (<c>arg_0</c>, <c>local_18</c>)
/// under the function's own annotation — which is also how a reader refers to them.
///
/// Runs last: everything before it reasons about slots by identity, and the frame pass has to have
/// invented the names before there is anything to replace.
/// </summary>
public sealed class LocalNamingPass : IIrPass
{
    private readonly AnnotationStore _annotations;

    public LocalNamingPass(AnnotationStore annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        _annotations = annotations;
    }

    public string Name => "local-naming";

    public void Run(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        var names = _annotations.LocalNamesFor(function.EntryVa);
        if (names.Count == 0)
        {
            return;
        }

        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                block.Statements[i] = IrRewriter.RewriteStmt(block.Statements[i], e => Rename(e, names));
            }
        }

        // The declaration list is keyed by name, so it has to be rebuilt rather than edited in place.
        var declared = function.Locals.Values.ToList();
        function.Locals.Clear();
        foreach (var local in declared)
        {
            var renamed = Rename(local, names);
            function.Locals[((IrLocal)renamed).Name] = (IrLocal)renamed;
        }
    }

    private static IrExpr Rename(IrExpr expr, IReadOnlyDictionary<string, string> names)
        => expr is IrLocal local && names.TryGetValue(local.Name, out string? chosen)
            ? local with { Name = chosen }
            : expr;
}
