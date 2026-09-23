using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Whether two pieces of lifted code are the same code: the same statements over the same expressions, wherever
/// in the method each came from. javac copies a <c>finally</c> onto every exit; this is how the copies are
/// recognised. Records compare their lists by reference and their addresses by value, so neither will do.
/// Loops, switches and nested tries are never called the same — their labels differ between copies, and a
/// <c>finally</c> that holds one is left as the compiler wrote it.
/// </summary>
internal static class JavaEquality
{
    public static bool Same(IReadOnlyList<CStmt> a, IReadOnlyList<CStmt> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!Same(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool Same(CStmt a, CStmt b) => (a, b) switch
    {
        (CSeq x, CSeq y) => Same(x.Items, y.Items),
        (CRaw x, CRaw y) => Same(x.Statement, y.Statement),
        (CIf x, CIf y) => Same(x.Condition, y.Condition) && Same(x.Then, y.Then)
                          && (x.Else is null ? y.Else is null : y.Else is not null && Same(x.Else, y.Else)),
        _ => false,
    };

    public static bool Same(IrStmt a, IrStmt b) => (a, b) switch
    {
        (IrAssign x, IrAssign y) => Same(x.Dst, y.Dst) && Same(x.Src, y.Src),
        (JExprStmt x, JExprStmt y) => Same(x.Expression, y.Expression),
        (IrReturn x, IrReturn y) => x.Value is null ? y.Value is null : y.Value is not null && Same(x.Value, y.Value),
        (JThrow x, JThrow y) => Same(x.Exception, y.Exception),
        (JMonitor x, JMonitor y) => x.Enter == y.Enter && Same(x.Lock, y.Lock),
        (IrNop, IrNop) => true,
        (IrComment x, IrComment y) => x.Text == y.Text,
        _ => false,
    };

    public static bool Same(IrExpr a, IrExpr b)
    {
        if (a.GetType() != b.GetType() || !Shallow(a, b))
        {
            return false;
        }

        var left = Children(a).ToList();
        var right = Children(b).ToList();
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!Same(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<IrExpr> Children(IrExpr e) => e switch
    {
        JExpr j => j.Children,
        IrCondition c => [c.Left, c.Right],
        IrUnary u => [u.Operand],
        _ => [],
    };

    /// <summary>What the node is, apart from what it is made of.</summary>
    private static bool Shallow(IrExpr a, IrExpr b) => (a, b) switch
    {
        (JLocal x, JLocal y) => x.Name == y.Name,
        (JConst x, JConst y) => Equals(x.Value, y.Value) && x.ConstType == y.ConstType,
        (JField x, JField y) => x.Owner == y.Owner && x.Name == y.Name && x.FieldType == y.FieldType && (x.Instance is null) == (y.Instance is null),
        (JArrayElement x, JArrayElement y) => x.ElementType == y.ElementType,
        (JArrayLength, JArrayLength) => true,
        (JCall x, JCall y) => x.Kind == y.Kind && x.Owner == y.Owner && x.Name == y.Name && x.Descriptor == y.Descriptor && (x.Receiver is null) == (y.Receiver is null),
        (JNew x, JNew y) => x.Owner == y.Owner && x.Descriptor == y.Descriptor,
        (JNewArray x, JNewArray y) => x.ArrayType == y.ArrayType && (x.Elements is null) == (y.Elements is null),
        (JAssignExpr x, JAssignExpr y) => Same(x.Target, y.Target),
        (JBinary x, JBinary y) => x.Op == y.Op && x.ResultType == y.ResultType,
        (JNegate, JNegate) => true,
        (JCast x, JCast y) => x.CastType == y.CastType,
        (JInstanceOf x, JInstanceOf y) => x.TestedType == y.TestedType,
        (JCompare, JCompare) => true,
        (JCaught x, JCaught y) => x.CatchType == y.CatchType,
        (JDynamic x, JDynamic y) => x.Name == y.Name && x.Descriptor == y.Descriptor && x.Target == y.Target && x.Bootstrap == y.Bootstrap
                                    && (x.Concat is null ? y.Concat is null : y.Concat is not null && x.Concat.SequenceEqual(y.Concat)),
        (JLogical x, JLogical y) => x.And == y.And,
        (JConditional, JConditional) => true,
        (JUnknown x, JUnknown y) => x.Description == y.Description,
        (JUninitialized x, JUninitialized y) => x.Owner == y.Owner && x.Id == y.Id,
        (IrCondition x, IrCondition y) => x.Cc == y.Cc,
        (IrUnary x, IrUnary y) => x.Op == y.Op,
        _ => false,
    };
}
