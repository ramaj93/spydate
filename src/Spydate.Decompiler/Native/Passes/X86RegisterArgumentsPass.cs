using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// Gives x86 calls their register arguments. <c>__thiscall</c> passes <c>this</c> in <c>ecx</c> and
/// <c>__fastcall</c> adds <c>edx</c>, and neither leaves a trace at the call site — so the question is put
/// to the callee instead: a function that reads <c>ecx</c> before writing it is being handed something in
/// it. Only direct calls to functions inside the image can be answered that way; an import or an indirect
/// call is left alone, and marked as such so later passes stay conservative about those registers.
/// </summary>
public sealed class X86RegisterArgumentsPass : IIrPass
{
    private readonly Func<ulong, int> _registerArguments;
    private readonly Dictionary<ulong, int> _cache = new();

    /// <param name="registerArguments">
    /// How many register arguments the function at a VA takes, or -1 when it cannot be analysed.
    /// </param>
    public X86RegisterArgumentsPass(Func<ulong, int> registerArguments)
    {
        ArgumentNullException.ThrowIfNull(registerArguments);
        _registerArguments = registerArguments;
    }

    public string Name => "x86-register-arguments";

    public void Run(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (function.Bitness != 32)
        {
            return;
        }

        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not IrCallStmt statement || statement.Call.Target is not IrSymbol target || target.Va == 0)
                {
                    continue;
                }

                int count = Lookup(target.Va);
                if (count < 0)
                {
                    continue; // an import, or a function that could not be analysed
                }

                var arguments = new List<IrExpr>(statement.Call.Args.Count + count);
                if (count >= 1)
                {
                    arguments.Add(new IrReg("ecx", 32));
                }

                if (count >= 2)
                {
                    arguments.Add(new IrReg("edx", 32));
                }

                arguments.AddRange(statement.Call.Args);
                block.Statements[i] = statement with
                {
                    Call = statement.Call with { Args = arguments, ConventionKnown = true },
                };
            }
        }
    }

    private int Lookup(ulong va)
    {
        if (!_cache.TryGetValue(va, out int count))
        {
            count = _registerArguments(va);
            _cache[va] = count;
        }

        return count;
    }
}
