using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// Decides whether the function returns anything. <c>ret</c> lifts to "return the accumulator" because
/// that is what the instruction does, but a function that never writes the accumulator is returning
/// whatever its caller left there — which is to say, nothing.
///
/// The test is only sound after dead-code elimination has run: a call result the function passes straight
/// through is read by the return, so it stays, and the function is correctly seen to produce a value.
/// A tail call (<c>return foo();</c>) is a value too, and is left alone.
/// </summary>
public sealed class ReturnValuePass : IIrPass
{
    public string Name => "return-value";

    public void Run(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        string accumulator = RegisterAliases.CanonicalOf(function.Bitness == 64 ? "rax" : "eax");
        bool written = false;
        bool onlyBareReturns = true;

        foreach (var statement in function.AllStatements)
        {
            if (IrRewriter.Destination(statement) is IrReg register && RegisterAliases.CanonicalOf(register.Name) == accumulator)
            {
                written = true;
                break;
            }

            // Anything but "return the accumulator" is a value the function really does produce: a tail
            // call, an expression, or another register propagation left in place.
            if (statement is IrReturn { Value: { } value }
                && (value is not IrReg returned || RegisterAliases.CanonicalOf(returned.Name) != accumulator))
            {
                onlyBareReturns = false;
            }
        }

        if (written || !onlyBareReturns)
        {
            return;
        }

        function.ReturnsValue = false;
        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is IrReturn { Value: IrReg } bare)
                {
                    block.Statements[i] = bare with { Value = null };
                }
            }
        }
    }
}
