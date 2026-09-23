using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// A Java expression, as the JVM lifter builds it. These derive from the native <see cref="IrExpr"/> so the
/// structurer and its conditions carry them unchanged; the Java emitter is the only thing that prints them.
///
/// <see cref="Type"/> is a JVM descriptor (<c>I</c>, <c>Ljava/lang/String;</c>, <c>[I</c>) or null when the
/// lifter could not tell; <see cref="Depth"/> is the height of the tree, which the lifter keeps bounded so that
/// nothing that walks a tree recursively can be driven off the end of the stack by crafted bytecode.
/// </summary>
public abstract record JExpr : IrExpr
{
    public abstract string? Type { get; }

    public abstract int Depth { get; }

    public override int Bits => Type is "J" or "D" ? 64 : 32;

    /// <summary>True for a value that can be read twice without evaluating anything twice: a local or a constant.</summary>
    public virtual bool IsSimple => false;

    /// <summary>The expressions this one is made of, in the order Java evaluates them.</summary>
    public abstract IEnumerable<IrExpr> Children { get; }

    protected static int DepthOf(IrExpr? e) => e switch
    {
        null => 0,
        JExpr j => j.Depth,
        IrCondition c => 1 + Math.Max(DepthOf(c.Left), DepthOf(c.Right)),
        IrUnary u => 1 + DepthOf(u.Operand),
        _ => 1,
    };

    protected static int DepthOf(IEnumerable<IrExpr> items) => items.Select(DepthOf).DefaultIfEmpty(0).Max();
}

/// <summary>A local variable: a parameter, <c>this</c>, a slot, a temporary the lifter introduced, or a stack variable at a join.</summary>
public sealed record JLocal(string Name, string? LocalType, JLocalKind Kind = JLocalKind.Local) : JExpr
{
    public override string? Type => LocalType;

    public override int Depth => 1;

    public override bool IsSimple => true;

    public override IEnumerable<IrExpr> Children => [];

    /// <summary>False for a temporary that holds a value whose evaluation order the bytecode swapped: moving it back would change that order.</summary>
    public bool Inlinable { get; init; } = true;

    public override string ToString() => Name;
}

public enum JLocalKind
{
    Local,
    Parameter,
    This,

    /// <summary>Introduced by the lifter to keep evaluation order; inlined again where that is safe.</summary>
    Temp,

    /// <summary>A value left on the operand stack across a branch — a conditional expression's result.</summary>
    Stack,
}

/// <summary>
/// A constant. <see cref="Value"/> is an int, long, float, double, string, null, or a <see cref="ClassLiteral"/>;
/// the type decides how it prints — an int 1 whose type is <c>Z</c> is <c>true</c>, one whose type is <c>C</c> is <c>'\u0001'</c>.
/// </summary>
public sealed record JConst(object? Value, string? ConstType) : JExpr
{
    public override string? Type => ConstType;

    public override int Depth => 1;

    public override bool IsSimple => true;

    public override IEnumerable<IrExpr> Children => [];

    public static JConst Null { get; } = new(null, "Ljava/lang/Object;");

    public static JConst Int(int value) => new(value, "I");

    /// <summary>The same constant, read as another type — an int that is really a boolean or a char.</summary>
    public JConst As(string? type) => type is "Z" or "C" or "B" or "S" && Value is int ? this with { ConstType = type } : this;
}

/// <summary>A class literal, <c>String.class</c>.</summary>
public sealed record ClassLiteral(string Descriptor);

/// <summary><c>instance.field</c>, or <c>Owner.field</c> for a static field.</summary>
public sealed record JField(JExpr? Instance, string Owner, string Name, string FieldType) : JExpr
{
    public override string? Type => FieldType;

    public override int Depth => 1 + DepthOf(Instance);

    public override IEnumerable<IrExpr> Children => Instance is null ? [] : [Instance];
}

public sealed record JArrayElement(JExpr Array, JExpr Index, string? ElementType) : JExpr
{
    public override string? Type => ElementType;

    public override int Depth => 1 + Math.Max(Array.Depth, Index.Depth);

    public override IEnumerable<IrExpr> Children => [Array, Index];
}

public sealed record JArrayLength(JExpr Array) : JExpr
{
    public override string? Type => "I";

    public override int Depth => 1 + Array.Depth;

    public override IEnumerable<IrExpr> Children => [Array];
}

public enum JCallKind
{
    Virtual,
    Static,
    Special,
    Interface,
}

/// <summary>A method call. A static call has no receiver; an <c>invokespecial</c> on <c>this</c> of another class is a <c>super.</c> call.</summary>
public sealed record JCall(JCallKind Kind, JExpr? Receiver, string Owner, string Name, string Descriptor, IReadOnlyList<JExpr> Args) : JExpr
{
    public override string? Type => ReturnType(Descriptor);

    public override int Depth => 1 + Math.Max(DepthOf(Receiver), DepthOf(Args));

    public override IEnumerable<IrExpr> Children => Receiver is null ? Args : Args.Prepend(Receiver);

    public static string? ReturnType(string descriptor)
    {
        int close = descriptor.LastIndexOf(')');
        return close < 0 || close + 1 >= descriptor.Length ? null : descriptor[(close + 1)..];
    }
}

/// <summary><c>new Owner(args)</c>, folded from <c>new</c>, <c>dup</c> and the <c>&lt;init&gt;</c> call.</summary>
public sealed record JNew(string Owner, string Descriptor, IReadOnlyList<JExpr> Args) : JExpr
{
    public override string? Type => $"L{Owner};";

    public override int Depth => 1 + DepthOf(Args);

    public override IEnumerable<IrExpr> Children => Args;
}

/// <summary>
/// An object after <c>new</c> and before its constructor has run. Never printed on its own when the bytecode is
/// what a compiler writes: the constructor call replaces it with a <see cref="JNew"/> wherever it is on the stack.
/// </summary>
public sealed record JUninitialized(string Owner, int Id) : JExpr
{
    public override string? Type => $"L{Owner};";

    public override int Depth => 1;

    public override bool IsSimple => true;

    public override IEnumerable<IrExpr> Children => [];
}

/// <summary><c>new T[n]</c>, or <c>new T[a][b]</c>; <see cref="ArrayType"/> is the array's own descriptor.</summary>
public sealed record JNewArray(string ArrayType, IReadOnlyList<JExpr> Dimensions) : JExpr
{
    public override string? Type => ArrayType;

    public override int Depth => 1 + DepthOf(Dimensions);

    public override IEnumerable<IrExpr> Children => Dimensions;
}

/// <summary>A binary operator, written as Java writes it: <c>+</c>, <c>&gt;&gt;&gt;</c>, <c>&amp;</c>.</summary>
public sealed record JBinary(string Op, JExpr Left, JExpr Right, string? ResultType) : JExpr
{
    public override string? Type => ResultType;

    public override int Depth => 1 + Math.Max(Left.Depth, Right.Depth);

    public override IEnumerable<IrExpr> Children => [Left, Right];
}

/// <summary>Negation, <c>-x</c>.</summary>
public sealed record JNegate(JExpr Operand) : JExpr
{
    public override string? Type => Operand.Type;

    public override int Depth => 1 + Operand.Depth;

    public override IEnumerable<IrExpr> Children => [Operand];
}

public sealed record JCast(string CastType, JExpr Operand) : JExpr
{
    public override string? Type => CastType;

    public override int Depth => 1 + Operand.Depth;

    public override IEnumerable<IrExpr> Children => [Operand];
}

public sealed record JInstanceOf(JExpr Operand, string TestedType) : JExpr
{
    public override string? Type => "Z";

    public override int Depth => 1 + Operand.Depth;

    public override IEnumerable<IrExpr> Children => [Operand];
}

/// <summary>
/// <c>lcmp</c>, <c>fcmpl</c> and the rest: -1, 0 or 1 by how two values compare. A branch on its result folds it
/// into an ordinary comparison of the two; one used any other way prints as <c>Long.compare(a, b)</c>.
/// </summary>
public sealed record JCompare(JExpr Left, JExpr Right) : JExpr
{
    public override string? Type => "I";

    public override int Depth => 1 + Math.Max(Left.Depth, Right.Depth);

    public override IEnumerable<IrExpr> Children => [Left, Right];
}

/// <summary>The exception a handler was entered with, before it is stored: what <c>catch (T e)</c> names.</summary>
public sealed record JCaught(string? CatchType) : JExpr
{
    public override string? Type => CatchType is null ? "Ljava/lang/Throwable;" : $"L{CatchType};";

    public override int Depth => 1;

    public override IEnumerable<IrExpr> Children => [];
}

/// <summary>
/// An <c>invokedynamic</c>. <see cref="Concat"/> is set when it is a string concatenation whose recipe was
/// read (<c>"a" + x + "b"</c>); <see cref="Target"/> when it is a lambda or method reference whose body is known.
/// </summary>
public sealed record JDynamic(string Name, string Descriptor, IReadOnlyList<JExpr> Args) : JExpr
{
    public override string? Type => JCall.ReturnType(Descriptor);

    public override int Depth => 1 + DepthOf(Args);

    public override IEnumerable<IrExpr> Children => Args;

    /// <summary>For a concatenation: literal text and argument positions, in order.</summary>
    public IReadOnlyList<object>? Concat { get; init; }

    /// <summary>For a lambda or method reference: the method that implements it, as owner, name and descriptor.</summary>
    public (string Owner, string Name, string Descriptor)? Target { get; init; }

    /// <summary>The bootstrap method, for a call site that is neither: <c>SwitchBootstraps.typeSwitch</c>.</summary>
    public string? Bootstrap { get; init; }
}

/// <summary><c>a &amp;&amp; b</c> or <c>a || b</c>, rebuilt from the branch ladders a compiler turns them into.</summary>
public sealed record JLogical(bool And, IrExpr Left, IrExpr Right) : JExpr
{
    public override string? Type => "Z";

    public override int Depth => 1 + Math.Max(DepthOf(Left), DepthOf(Right));

    public override IEnumerable<IrExpr> Children => [Left, Right];

    /// <summary>The negation, pushed inward by De Morgan so it reads without a <c>!( )</c> around it.</summary>
    public static IrExpr Not(IrExpr condition) => condition switch
    {
        JLogical l => new JLogical(!l.And, Not(l.Left), Not(l.Right)),
        _ => Native.Structuring.CStmts.Invert(condition),
    };
}

/// <summary><c>cond ? a : b</c>.</summary>
public sealed record JConditional(IrExpr Condition, JExpr Then, JExpr Else) : JExpr
{
    public override string? Type => Then.Type ?? Else.Type;

    public override int Depth => 1 + Math.Max(DepthOf(Condition), Math.Max(Then.Depth, Else.Depth));

    public override IEnumerable<IrExpr> Children => [Condition, Then, Else];
}

/// <summary>What the lifter pushes when it has nothing honest to push: a stack that underflowed, an unreadable constant.</summary>
public sealed record JUnknown(string Description) : JExpr
{
    public override string? Type => null;

    public override int Depth => 1;

    public override IEnumerable<IrExpr> Children => [];
}

// --- statements -----------------------------------------------------------------

/// <summary>An expression evaluated for its effect: a call, a constructor.</summary>
public sealed record JExprStmt(JExpr Expression) : IrStmt;

public sealed record JThrow(JExpr Exception) : IrStmt;

/// <summary><c>monitorenter</c> / <c>monitorexit</c> — what a <c>synchronized</c> block compiles to.</summary>
public sealed record JMonitor(bool Enter, JExpr Lock) : IrStmt;

/// <summary>
/// A region that was structured on its own — a try with its handlers — and collapsed into one statement of the
/// block that replaced it, so the enclosing graph sees one block where there were many.
/// </summary>
public sealed record JRegion(CStmt Body) : IrStmt;

/// <summary>A try block and its handlers, already structured.</summary>
public sealed record JTry(CStmt Body, IReadOnlyList<JCatch> Catches) : CStmt;

/// <summary>One handler: what it catches (null for any — a <c>finally</c>), what the exception is called, and its body.</summary>
public sealed record JCatch(string? CatchType, string? Variable, CStmt Body);
