using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>Java's reserved words, which a name from a class file must not be printed as.</summary>
internal static class JavaNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
        "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "package", "private",
        "protected", "public", "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "try", "void", "volatile", "while", "true", "false", "null", "var", "record",
        "yield",
    };

    public static bool IsKeyword(string name) => Keywords.Contains(name);
}

/// <summary>Rebuilds expression trees with some locals replaced, and reads them in Java's evaluation order.</summary>
internal static class JavaRewrite
{
    /// <summary>The expression with every local <paramref name="replace"/> answers for swapped for its answer.</summary>
    public static IrExpr Replace(IrExpr expression, Func<JLocal, JExpr?> replace) => expression switch
    {
        JLocal local => replace(local) ?? local,
        JField f => f with { Instance = f.Instance is null ? null : R(f.Instance, replace) },
        JArrayElement a => a with { Array = R(a.Array, replace), Index = R(a.Index, replace) },
        JArrayLength l => l with { Array = R(l.Array, replace) },
        JCall c => c with { Receiver = c.Receiver is null ? null : R(c.Receiver, replace), Args = c.Args.Select(x => R(x, replace)).ToList() },
        JNew n => n with { Args = n.Args.Select(x => R(x, replace)).ToList() },
        JNewArray n => n with { Dimensions = n.Dimensions.Select(x => R(x, replace)).ToList() },
        JBinary b => b with { Left = R(b.Left, replace), Right = R(b.Right, replace) },
        JNegate n => n with { Operand = R(n.Operand, replace) },
        JCast c => c with { Operand = R(c.Operand, replace) },
        JInstanceOf i => i with { Operand = R(i.Operand, replace) },
        JCompare c => c with { Left = R(c.Left, replace), Right = R(c.Right, replace) },
        JDynamic d => d with { Args = d.Args.Select(x => R(x, replace)).ToList() },
        JConditional c => c with { Condition = Replace(c.Condition, replace), Then = R(c.Then, replace), Else = R(c.Else, replace) },
        JLogical l => l with { Left = Replace(l.Left, replace), Right = Replace(l.Right, replace) },
        IrCondition c => c with { Left = Replace(c.Left, replace), Right = Replace(c.Right, replace) },
        IrUnary u => u with { Operand = Replace(u.Operand, replace) },
        _ => expression,
    };

    private static JExpr R(JExpr expression, Func<JLocal, JExpr?> replace) => (JExpr)Replace(expression, replace);

    /// <summary>The statement with its expressions rewritten. The target of an assignment to a plain local is left alone.</summary>
    public static IrStmt Replace(IrStmt statement, Func<JLocal, JExpr?> replace) => statement switch
    {
        IrAssign { Dst: JLocal } a => a with { Src = Replace(a.Src, replace) },
        IrAssign a => a with { Dst = Replace(a.Dst, replace), Src = Replace(a.Src, replace) },
        JExprStmt e => e with { Expression = R(e.Expression, replace) },
        IrReturn { Value: { } v } r => r with { Value = Replace(v, replace) },
        JThrow t => t with { Exception = R(t.Exception, replace) },
        JMonitor m => m with { Lock = R(m.Lock, replace) },
        IrBranch b => b with { Condition = Replace(b.Condition, replace) },
        IrSwitch s => s with { Value = Replace(s.Value, replace) },
        _ => statement,
    };

    /// <summary>
    /// The expressions a statement evaluates, in the order Java evaluates them: an assignment's target object and
    /// index before its value, a call's receiver before its arguments, every operand before its operator.
    /// </summary>
    public static IEnumerable<IrExpr> Evaluated(IrStmt statement) => statement switch
    {
        IrAssign { Dst: JLocal } a => [a.Src],
        IrAssign { Dst: JField { Instance: { } instance } } a => [instance, a.Src],
        IrAssign { Dst: JArrayElement e } a => [e.Array, e.Index, a.Src],
        IrAssign a => [a.Src],
        JExprStmt e => [e.Expression],
        IrReturn { Value: { } v } => [v],
        JThrow t => [t.Exception],
        JMonitor m => [m.Lock],
        IrBranch b => [b.Condition],
        IrSwitch s => [s.Value],
        _ => [],
    };

    /// <summary>
    /// Every node in evaluation order, operands before the operation that uses them — the order in which a call's
    /// effect happens relative to the reads around it. Iterative, so a deep tree costs heap, not stack.
    /// </summary>
    public static IEnumerable<IrExpr> PostOrder(IrExpr root)
    {
        var stack = new Stack<(IrExpr Node, bool Expanded)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, expanded) = stack.Pop();
            if (expanded)
            {
                yield return node;
                continue;
            }

            stack.Push((node, true));
            var children = node switch
            {
                JExpr j => j.Children,
                IrCondition c => [c.Left, c.Right],
                IrUnary u => [u.Operand],
                _ => [],
            };

            foreach (var child in children.Reverse())
            {
                stack.Push((child, false));
            }
        }
    }
}

/// <summary>
/// Puts the lifter's temporaries back into the expressions that use them, where doing so changes neither what is
/// evaluated nor in which order. A temporary is folded into the statement right after the run of definitions it
/// belongs to, when it is used once, there, in the order the run defined it, with no call evaluated ahead of it —
/// the shape <c>foo(a(), b())</c> leaves after its arguments were spilled one by one. Anything else keeps its
/// temporary, which reads plainly and is never wrong.
/// </summary>
internal static class JavaInliner
{
    /// <summary>An inlined tree is kept below this height, so inlining cannot undo the lifter's bound.</summary>
    private const int MaxDepth = 64;

    public static void Run(IrFunction function)
    {
        var uses = CountUses(function);
        foreach (var block in function.Blocks)
        {
            var statements = block.Statements;
            for (int i = 0; i < statements.Count; i++)
            {
                while (TryInlineRun(statements, i, uses, out int removed))
                {
                    i -= removed;
                }
            }

            RemoveDead(statements, uses);
        }
    }

    private static Dictionary<string, int> CountUses(IrFunction function)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var statement in function.AllStatements)
        {
            foreach (var root in JavaRewrite.Evaluated(statement))
            {
                foreach (var node in JavaRewrite.PostOrder(root))
                {
                    if (node is JLocal { Kind: JLocalKind.Temp } temp)
                    {
                        uses[temp.Name] = uses.GetValueOrDefault(temp.Name) + 1;
                    }
                }
            }
        }

        return uses;
    }

    private static bool TryInlineRun(List<IrStmt> statements, int index, Dictionary<string, int> uses, out int removed)
    {
        removed = 0;
        var user = statements[index];
        var order = new List<string>();
        var afterEffect = new HashSet<string>(StringComparer.Ordinal);
        bool effectSeen = false;
        foreach (var root in JavaRewrite.Evaluated(user))
        {
            foreach (var node in JavaRewrite.PostOrder(root))
            {
                switch (node)
                {
                    case JLocal { Kind: JLocalKind.Temp } temp:
                        order.Add(temp.Name);
                        if (effectSeen)
                        {
                            afterEffect.Add(temp.Name);
                        }

                        break;
                    case JCall or JNew or JDynamic:
                        effectSeen = true;
                        break;
                }
            }
        }

        // The run of single-use temporary definitions just above the statement, nearest last.
        var run = new List<(int Index, JLocal Temp, JExpr Value)>();
        for (int j = index - 1; j >= 0; j--)
        {
            if (statements[j] is IrAssign { Dst: JLocal { Kind: JLocalKind.Temp, Inlinable: true } temp, Src: JExpr value }
                && uses.GetValueOrDefault(temp.Name) == 1 && order.Contains(temp.Name) && !afterEffect.Contains(temp.Name))
            {
                run.Insert(0, (j, temp, value));
                continue;
            }

            break;
        }

        if (run.Count == 0)
        {
            return false;
        }

        // Defined in the order they are used, or not at all: moving one past another would reorder their evaluation.
        var used = order.Where(name => run.Any(r => r.Temp.Name == name)).ToList();
        if (!used.SequenceEqual(run.Select(r => r.Temp.Name)))
        {
            return false;
        }

        var values = run.ToDictionary(r => r.Temp.Name, r => r.Value, StringComparer.Ordinal);
        var rewritten = JavaRewrite.Replace(user, local => values.GetValueOrDefault(local.Name));
        if (JavaRewrite.Evaluated(rewritten).Any(e => Height(e) > MaxDepth))
        {
            return false;
        }

        statements[index] = rewritten;
        foreach (var (i, temp, _) in Enumerable.Reverse(run))
        {
            statements.RemoveAt(i);
            uses.Remove(temp.Name);
        }

        removed = run.Count;
        return true;
    }

    /// <summary>A temporary nothing reads is dropped, or kept as a statement when evaluating it did something.</summary>
    private static void RemoveDead(List<IrStmt> statements, Dictionary<string, int> uses)
    {
        for (int i = statements.Count - 1; i >= 0; i--)
        {
            if (statements[i] is IrAssign { Dst: JLocal { Kind: JLocalKind.Temp } temp, Src: JExpr value } && uses.GetValueOrDefault(temp.Name) == 0)
            {
                if (value is JCall or JNew or JDynamic)
                {
                    statements[i] = new JExprStmt(value) { Va = statements[i].Va };
                }
                else
                {
                    statements.RemoveAt(i);
                }
            }
        }
    }

    private static int Height(IrExpr expression) => expression switch
    {
        JExpr j => j.Depth,
        IrCondition c => 1 + Math.Max(Height(c.Left), Height(c.Right)),
        IrUnary u => 1 + Height(u.Operand),
        _ => 1,
    };
}
