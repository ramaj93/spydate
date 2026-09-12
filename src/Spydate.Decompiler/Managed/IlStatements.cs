using System.Reflection.Metadata;

namespace Spydate.Decompiler.Managed;

/// <summary>
/// Where one statement's IL begins and ends.
///
/// A statement boundary in IL is a point where the evaluation stack is empty, and that is not a
/// convention — it is what the language guarantees and what the JIT records. Everything between two
/// such points is one expression being built up and consumed: the receiver pushed, the arguments
/// pushed, the call made, the result stored. Nothing outside that method can observe the middle of
/// it, which is why the runtime will not bind a breakpoint there and why stepping through it one IL
/// instruction at a time shows a reader four or five stops that are all the same line of C#.
///
/// So this needs no decompiler and no symbols. It reads the IL, which is the same thing the debugger
/// and the reader are both looking at.
/// </summary>
public static class IlStatements
{
    /// <summary>
    /// The statement containing an offset: from the last empty stack at or before it, to the next
    /// empty stack after it.
    ///
    /// The end is exclusive, which is what <c>ICorDebugStepper::StepRange</c> wants — it runs until
    /// execution leaves the range, so the first instruction of the next statement is where it stops.
    ///
    /// Null when the offset is not at an instruction boundary, or when a signature in the way could
    /// not be read. Both mean the same thing to a caller: nobody can say where this statement ends,
    /// and a range guessed at would step over something else entirely.
    /// </summary>
    public static (int From, int To)? Containing(ManagedBody body, MetadataReader metadata, int offset)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (offset < 0 || offset >= body.Il.Length)
        {
            return null;
        }

        var stack = new IlStack(metadata);
        int depth = 0;
        int from = 0;
        bool reached = false;

        foreach (var instruction in Il.Walk(body.Il))
        {
            // A nop is never a boundary, and this is the difference between stepping statements and
            // stepping IL on anything built for debugging. A Debug build puts a nop between every
            // pair of statements, and the stack is empty at it — so a statement ended one byte
            // early, the step landed on the nop, and the reader pressed the key again to move a
            // single byte onto the line they were expecting. The nop belongs to the statement it
            // follows, which is what treating it as not-a-boundary makes it.
            bool boundary = instruction.Op.Value != Il.Nop;

            if (instruction.Offset > offset)
            {
                // Past it without having landed on it, so the offset was inside an instruction
                // rather than at one. Nothing starts there and no range should be handed back for
                // it — checked before the range is returned, because the walk otherwise sails past
                // and answers about the statement the operand byte happens to sit in.
                if (!reached)
                {
                    return null;
                }

                if (depth == 0 && boundary)
                {
                    return (from, instruction.Offset);
                }
            }
            else
            {
                if (depth == 0 && boundary)
                {
                    from = instruction.Offset;
                }

                if (instruction.Offset == offset)
                {
                    reached = true;
                }
            }

            if (stack.Delta(instruction, body.Il, body.Method) is not { } delta)
            {
                return null;
            }

            // A depth below zero means this straight-line reading has lost its place — a branch
            // landed somewhere it did not account for — and every boundary after that is a guess.
            depth += delta;
            if (depth < 0)
            {
                return null;
            }
        }

        // The last statement of a method ends where the method does.
        return reached ? (from, body.Il.Length) : null;
    }
}
