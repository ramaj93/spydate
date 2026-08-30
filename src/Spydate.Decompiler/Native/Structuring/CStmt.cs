using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Structuring;

/// <summary>
/// A statement in the structured form of a function body: the shape the emitter prints, with braces and
/// keywords instead of a flat list of blocks. Anything the structurer cannot express keeps its
/// <see cref="CGoto"/> and the label it needs, so the tree always describes the whole function.
/// </summary>
public abstract record CStmt;

/// <summary>Statements in order.</summary>
public sealed record CSeq(IReadOnlyList<CStmt> Items) : CStmt
{
    public static readonly CSeq Empty = new(Array.Empty<CStmt>());
}

/// <summary>An IR statement carried through unchanged.</summary>
public sealed record CRaw(IrStmt Statement) : CStmt;

/// <summary>Start of a block. Printed only when some <see cref="CGoto"/> targets it.</summary>
public sealed record CLabel(ulong Va) : CStmt;

/// <summary><c>goto</c>, kept for edges no structure covers. <paramref name="External"/> marks a tail jump out of the function.</summary>
public sealed record CGoto(ulong Va, bool External = false) : CStmt;

/// <param name="Va">Address of the test, so the line can be commented and lined up with the disassembly.</param>
public sealed record CIf(IrExpr Condition, CStmt Then, CStmt? Else, ulong Va = 0) : CStmt;

public enum CLoopKind
{
    /// <summary>Test before the body.</summary>
    While,
    /// <summary>Test after the body.</summary>
    DoWhile,
    /// <summary>No test: the loop is left by <c>break</c>, <c>return</c> or a goto.</summary>
    Forever,
}

public sealed record CLoop(CLoopKind Kind, IrExpr? Condition, CStmt Body, ulong Va = 0) : CStmt;

/// <summary>One arm of a <see cref="CSwitch"/>; several indices can share a body.</summary>
public sealed record CCase(IReadOnlyList<int> Labels, CStmt Body);

/// <summary>
/// Dispatch on a value. Arms are in address order, so an arm that runs off its end falls into the next
/// one exactly as C says it does; every other exit carries its own <c>break</c> or <c>goto</c>.
/// </summary>
public sealed record CSwitch(IrExpr Value, IReadOnlyList<CCase> Cases, ulong Va = 0) : CStmt;

public sealed record CBreak : CStmt;

public sealed record CContinue : CStmt;

public static class CStmts
{
    /// <summary>Flattens nested sequences and drops empty ones.</summary>
    public static CStmt Sequence(List<CStmt> items)
    {
        var flat = new List<CStmt>(items.Count);
        foreach (var item in items)
        {
            switch (item)
            {
                case CSeq { Items.Count: 0 }:
                    break;
                case CSeq inner:
                    flat.AddRange(inner.Items);
                    break;
                default:
                    flat.Add(item);
                    break;
            }
        }

        return flat.Count == 1 ? flat[0] : new CSeq(flat);
    }

    /// <summary>True when the statement prints nothing (labels alone do not make a body non-empty).</summary>
    public static bool IsEmpty(CStmt? stmt) => stmt switch
    {
        null => true,
        CSeq s => s.Items.All(IsEmpty),
        CLabel => true,
        CRaw { Statement: IrNop } => true,
        _ => false,
    };

    /// <summary>Logical negation, folded into the condition code when the expression is a comparison.</summary>
    public static IrExpr Invert(IrExpr condition) => condition switch
    {
        IrCondition c => c with { Cc = IrTypes.Invert(c.Cc) },
        IrUnary { Op: IrUnaryOp.LogicalNot } u => u.Operand,
        _ => new IrUnary(IrUnaryOp.LogicalNot, condition),
    };

    /// <summary>Walks the tree, parents before children.</summary>
    public static IEnumerable<CStmt> Descendants(CStmt stmt)
    {
        yield return stmt;
        switch (stmt)
        {
            case CSeq s:
                foreach (var item in s.Items)
                {
                    foreach (var d in Descendants(item))
                    {
                        yield return d;
                    }
                }

                break;
            case CIf i:
                foreach (var d in Descendants(i.Then))
                {
                    yield return d;
                }

                if (i.Else is not null)
                {
                    foreach (var d in Descendants(i.Else))
                    {
                        yield return d;
                    }
                }

                break;
            case CLoop l:
                foreach (var d in Descendants(l.Body))
                {
                    yield return d;
                }

                break;
            case CSwitch s:
                foreach (var arm in s.Cases)
                {
                    foreach (var d in Descendants(arm.Body))
                    {
                        yield return d;
                    }
                }

                break;
        }
    }
}
