using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

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
        JNewArray n => n with { Dimensions = n.Dimensions.Select(x => R(x, replace)).ToList(), Elements = n.Elements?.Select(x => R(x, replace)).ToList() },
        JAssignExpr { Target: JLocal } a => a with { Value = R(a.Value, replace) },
        JAssignExpr a => a with { Target = R(a.Target, replace), Value = R(a.Value, replace) },
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
        JYield y => y with { Value = R(y.Value, replace) },
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
        JYield y => [y.Value],
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

    private static bool IsTemp(JLocal local) => local is { Kind: JLocalKind.Temp, Inlinable: true };

    public static void Run(IrFunction function)
    {
        var uses = CountUses(function.AllStatements);
        foreach (var block in function.Blocks)
        {
            var statements = block.Statements;
            for (int i = 0; i < statements.Count; i++)
            {
                while (TryInlineRun(statements, i, uses, IsTemp, out var removed))
                {
                    foreach (int at in removed)
                    {
                        statements.RemoveAt(at);
                    }

                    i -= removed.Count;
                }
            }

            RemoveDead(statements, uses, IsTemp);
        }
    }

    /// <summary>
    /// The same folding on the structured method, where a temporary and its use can end up next to each other
    /// across what were blocks — and where a stack variable assigned once (a folded <c>?:</c>) folds like a
    /// temporary. A statement that tests or dispatches on a value (<c>if</c>, <c>switch</c>, <c>synchronized</c>)
    /// is a use like any other; a loop's test is not, because it runs again.
    /// </summary>
    public static CStmt RunOnTree(CStmt body, Func<string, bool> untabled)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        var definitions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in JavaTree.Descendants(body))
        {
            foreach (var statement in Evaluated(node))
            {
                Count(statement, uses);
                if (statement is IrAssign { Dst: JLocal defined })
                {
                    definitions[defined.Name] = definitions.GetValueOrDefault(defined.Name) + 1;
                }
            }
        }

        // A slot the table does not describe and that is stored once is the compiler's own temporary — the value a
        // return carries past a finally — or, without a table, a variable nobody named: it folds like one.
        bool Foldable(JLocal local) => local.Inlinable
            && (local.Kind == JLocalKind.Temp
                || (local.Kind == JLocalKind.Stack && definitions.GetValueOrDefault(local.Name) == 1)
                || (local.Kind == JLocalKind.Local && untabled(local.Name) && definitions.GetValueOrDefault(local.Name) == 1));

        return JavaTree.Rewrite(body, statement => statement is CSeq seq ? Sequence(seq, uses, Foldable) : statement);
    }

    /// <summary>What a structured statement evaluates as it is reached, as IR statements the counting and folding read.</summary>
    private static IEnumerable<IrStmt> Evaluated(CStmt node) => node switch
    {
        CRaw { Statement: var statement } => [statement],
        CIf conditional => [new IrBranch(conditional.Condition, 0, 0)],
        JSwitch dispatch => [new IrSwitch(dispatch.Value, [])],
        JSynchronized locked => [new JMonitor(true, locked.Lock)],
        JLoop loop => new IrStmt?[]
        {
            loop.Init, loop.Condition is null ? null : new IrBranch(loop.Condition, 0, 0), loop.Update,
            loop.ForEach is { } each ? new IrSwitch(each.Source, []) : null,
        }.OfType<IrStmt>(),
        JTry attempt => attempt.Resources.Select(r => (IrStmt)new IrAssign(r.Variable, r.Init)),
        JAssert assertion => assertion.Message is null ? [new IrBranch(assertion.Condition, 0, 0)] : [new IrBranch(assertion.Condition, 0, 0), new JExprStmt(assertion.Message)],
        _ => [],
    };

    private static CStmt Sequence(CSeq seq, Dictionary<string, int> uses, Func<JLocal, bool> foldable)
    {
        var items = seq.Items.ToList();
        ArrayInitializers(items, uses);
        CopyForward(items, uses);
        var views = items.Select(View).ToList();
        for (int i = 0; i < views.Count; i++)
        {
            while (views[i] is not IrComment && TryInlineRun(views, i, uses, foldable, out var removed, out var rewritten))
            {
                items[i] = Rebuild(items[i], rewritten);
                views[i] = rewritten;
                foreach (int at in removed)
                {
                    items.RemoveAt(at);
                    views.RemoveAt(at);
                }

                i -= removed.Count;
            }
        }

        for (int i = views.Count - 1; i >= 0; i--)
        {
            if (views[i] is IrAssign { Dst: JLocal local, Src: JExpr value } && foldable(local) && uses.GetValueOrDefault(local.Name) == 0)
            {
                if (value is JCall or JNew or JDynamic)
                {
                    items[i] = new CRaw(new JExprStmt(value) { Va = views[i].Va });
                }
                else
                {
                    items.RemoveAt(i);
                }
            }
        }

        return JavaTree.Sequence(items);
    }

    /// <summary>
    /// <c>t = x; v = t;</c> — javac's <c>dup; astore</c>, the value kept on the stack while it is stored — is
    /// <c>v = x;</c>, and the later reads of <c>t</c> read <c>v</c>. Only when every other read of the temporary is
    /// in the statements that follow, and nothing stores to <c>v</c> before the last of them.
    /// </summary>
    private static void CopyForward(List<CStmt> items, Dictionary<string, int> uses)
    {
        for (int i = 0; i + 1 < items.Count; i++)
        {
            if (items[i] is not CRaw { Statement: IrAssign { Dst: JLocal { Kind: JLocalKind.Temp } temp, Src: var value } first }
                || items[i + 1] is not CRaw { Statement: IrAssign { Dst: JLocal { Kind: not JLocalKind.Temp } copy, Src: JLocal source } }
                || source.Name != temp.Name || copy.Name == temp.Name)
            {
                continue;
            }

            int remaining = uses.GetValueOrDefault(temp.Name) - 1;
            int last = i + 1;
            int seen = 0;
            for (int k = i + 2; k < items.Count && seen < remaining; k++)
            {
                int here = Reads(items[k], temp.Name);
                if (here > 0)
                {
                    seen += here;
                    last = k;
                }
            }

            if (seen != remaining || items.Skip(i + 2).Take(last - i - 1).Any(item => Stores(item, copy.Name)))
            {
                continue;
            }

            items[i] = new CRaw(new IrAssign(copy, value) { Va = first.Va });
            items.RemoveAt(i + 1);
            for (int k = i + 1; k < last; k++)
            {
                items[k] = JavaTree.ReplaceLocals(items[k], l => l.Name == temp.Name ? copy : null);
            }

            uses[copy.Name] = uses.GetValueOrDefault(copy.Name) + remaining;
            uses[temp.Name] = 0;
        }
    }

    /// <summary>
    /// <c>t = new int[3]; t[0] = a; t[1] = b; t[2] = c; use(t)</c> — javac's <c>new int[] {a, b, c}</c>, the array kept on
    /// the stack while each element is stored — is that expression again, in the one statement that uses it. The
    /// stores must follow each other with rising indices (javac skips none, but a gap reads as the default), and
    /// nothing that statement evaluates before the array may have an effect the elements could see.
    /// </summary>
    private static void ArrayInitializers(List<CStmt> items, Dictionary<string, int> uses)
    {
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is not CRaw { Statement: IrAssign { Dst: JLocal { Kind: JLocalKind.Temp } array, Src: JNewArray { Dimensions: [JConst { Value: int length }], Elements: null } created } }
                || length < 0 || length > MaxElements)
            {
                continue;
            }

            // Between the allocation and the first store, anything not about the array — moving an allocation later changes nothing.
            int first = i + 1;
            while (first < items.Count && Reads(items[first], array.Name) == 0)
            {
                first++;
            }

            var values = new List<JExpr>();
            int next = first;
            int index = 0;
            while (next < items.Count && items[next] is CRaw { Statement: IrAssign { Dst: JArrayElement { Array: JLocal target, Index: JConst { Value: int at } }, Src: JExpr value } }
                   && target.Name == array.Name && at >= index && at < length && Reads(items[next], array.Name) == 1)
            {
                string elementType = created.ArrayType[1..];
                while (index < at)
                {
                    values.Add(DefaultValue(elementType));
                    index++;
                }

                values.Add(value);
                index++;
                next++;
            }

            if (next >= items.Count || next == first || Reads(items[next], array.Name) != 1 || uses.GetValueOrDefault(array.Name) != values.Count(v => v is not JConst { ConstType: "default" }) + 1)
            {
                continue;
            }

            while (index < length)
            {
                values.Add(DefaultValue(created.ArrayType[1..]));
                index++;
            }

            bool effects = values.Any(v => JavaRewrite.PostOrder(v).Any(e => e is JCall or JNew or JDynamic));
            if (effects && EffectBefore(items[next], array.Name))
            {
                continue;
            }

            var initialised = created with { Elements = values.Select(v => v is JConst { ConstType: "default" } d ? d with { ConstType = created.ArrayType[1..] } : v).ToList() };
            if (initialised.Depth > MaxDepth)
            {
                continue;
            }

            items[next] = JavaTree.ReplaceLocals(items[next], l => l.Name == array.Name ? initialised : null);
            items.RemoveRange(first, next - first);
            items.RemoveAt(i);
            uses[array.Name] = 0;
        }
    }

    private const int MaxElements = 4096;

    /// <summary>The value an element nobody stored keeps; marked so it is not counted as a store.</summary>
    private static JConst DefaultValue(string type) => type switch
    {
        ['L' or '[', ..] => JConst.Null with { ConstType = "default" },
        "J" => new JConst(0L, "default"),
        "F" => new JConst(0f, "default"),
        "D" => new JConst(0d, "default"),
        _ => new JConst(0, "default"),
    };

    /// <summary>Whether the statement evaluates a call before it reads the local.</summary>
    private static bool EffectBefore(CStmt statement, string name)
    {
        foreach (var node in JavaTree.Expressions(statement).SelectMany(JavaRewrite.PostOrder))
        {
            switch (node)
            {
                case JLocal l when l.Name == name:
                    return false;
                case JCall or JNew or JDynamic:
                    return true;
            }
        }

        return false;
    }

    /// <summary>How many times a statement, and everything in it, reads a local.</summary>
    private static int Reads(CStmt statement, string name)
        => JavaTree.Descendants(statement).SelectMany(Evaluated).SelectMany(JavaRewrite.Evaluated).SelectMany(JavaRewrite.PostOrder)
            .Count(e => e is JLocal l && l.Name == name);

    /// <summary>Whether a statement, or anything in it, stores to a local.</summary>
    private static bool Stores(CStmt statement, string name)
        => JavaTree.Descendants(statement).SelectMany(Evaluated).Any(s => s is IrAssign { Dst: JLocal l } && l.Name == name);

    /// <summary>A statement as the IR statement that evaluates what it evaluates; anything else is a barrier.</summary>
    private static IrStmt View(CStmt item) => item switch
    {
        CRaw { Statement: var statement } => statement,
        CIf conditional => new IrBranch(conditional.Condition, 0, 0),
        JSwitch dispatch => new IrSwitch(dispatch.Value, []),
        JSynchronized locked => new JMonitor(true, locked.Lock),

        // A for-each's source is evaluated once, before the loop: a value folds into it like into any statement.
        JLoop { ForEach: { } each } => new IrSwitch(each.Source, []),
        JTry { Resources: [var only] } => new IrAssign(only.Variable, only.Init),
        _ => new IrComment("barrier"),
    };

    private static CStmt Rebuild(CStmt item, IrStmt rewritten) => (item, rewritten) switch
    {
        (CRaw raw, _) => raw with { Statement = rewritten },
        (CIf conditional, IrBranch branch) => conditional with { Condition = branch.Condition },
        (JSwitch dispatch, IrSwitch value) => dispatch with { Value = value.Value },
        (JSynchronized locked, JMonitor monitor) => locked with { Lock = monitor.Lock },
        (JLoop { ForEach: { } each } loop, IrSwitch source) => loop with { ForEach = (each.Variable, source.Value) },
        (JTry { Resources: [var only] } attempt, IrAssign resource) => attempt with { Resources = [(only.Variable, resource.Src)] },
        _ => item,
    };

    private static Dictionary<string, int> CountUses(IEnumerable<IrStmt> statements)
    {
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            Count(statement, uses);
        }

        return uses;
    }

    /// <summary>Adds the reads of every local a statement evaluates.</summary>
    private static void Count(IrStmt statement, Dictionary<string, int> uses)
    {
        foreach (var root in JavaRewrite.Evaluated(statement))
        {
            foreach (var node in JavaRewrite.PostOrder(root))
            {
                if (node is JLocal local)
                {
                    uses[local.Name] = uses.GetValueOrDefault(local.Name) + 1;
                }
            }
        }
    }

    private static bool TryInlineRun(List<IrStmt> statements, int index, Dictionary<string, int> uses, Func<JLocal, bool> foldable, out List<int> removed)
    {
        bool done = TryInlineRun(statements, index, uses, foldable, out removed, out var rewritten);
        if (done)
        {
            statements[index] = rewritten;
        }

        return done;
    }

    /// <summary>
    /// Folds the run of single-use definitions just above <paramref name="index"/> into the statement there. On
    /// success, <paramref name="removed"/> lists the definitions' indices, highest first, for the caller to drop.
    /// </summary>
    private static bool TryInlineRun(List<IrStmt> statements, int index, Dictionary<string, int> uses, Func<JLocal, bool> foldable, out List<int> removed, out IrStmt rewritten)
    {
        removed = [];
        var user = statements[index];
        rewritten = user;
        var order = new List<string>();
        var afterEffect = new HashSet<string>(StringComparer.Ordinal);
        bool effectSeen = false;
        foreach (var root in JavaRewrite.Evaluated(user))
        {
            foreach (var node in JavaRewrite.PostOrder(root))
            {
                switch (node)
                {
                    case JLocal local when foldable(local):
                        order.Add(local.Name);
                        if (effectSeen)
                        {
                            afterEffect.Add(local.Name);
                        }

                        break;
                    case JCall or JNew or JDynamic:
                        effectSeen = true;
                        break;
                }
            }
        }

        // The run of single-use definitions just above the statement, nearest last.
        var run = new List<(int Index, JLocal Temp, JExpr Value)>();
        for (int j = index - 1; j >= 0; j--)
        {
            if (statements[j] is IrAssign { Dst: JLocal temp, Src: JExpr value } && foldable(temp)
                && uses.GetValueOrDefault(temp.Name) == 1 && order.Contains(temp.Name) && !afterEffect.Contains(temp.Name)
                && (value is not JDynamic { Target: not null } || TargetTyped(user, temp.Name)))
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
        var result = JavaRewrite.Replace(user, local => values.GetValueOrDefault(local.Name));
        if (JavaRewrite.Evaluated(result).Any(e => Height(e) > MaxDepth))
        {
            return false;
        }

        rewritten = result;
        foreach (var (i, temp, _) in Enumerable.Reverse(run))
        {
            removed.Add(i);
            uses.Remove(temp.Name);
        }

        return true;
    }

    /// <summary>
    /// Whether a local is read where Java lets a lambda or method reference stand: stored, returned, or passed as an
    /// argument — places a type is expected. <c>f.apply(x)</c> cannot become <c>(x -&gt; y).apply(x)</c>.
    /// </summary>
    private static bool TargetTyped(IrStmt user, string name)
    {
        bool IsIt(IrExpr e) => e is JLocal l && l.Name == name;
        if (user is IrAssign { Src: var src } && IsIt(src) || user is IrReturn { Value: { } value } && IsIt(value))
        {
            return true;
        }

        return JavaRewrite.Evaluated(user).SelectMany(JavaRewrite.PostOrder).Any(e => e switch
        {
            JCall call => call.Args.Any(IsIt),
            JNew created => created.Args.Any(IsIt),
            _ => false,
        });
    }

    /// <summary>A temporary nothing reads is dropped, or kept as a statement when evaluating it did something.</summary>
    private static void RemoveDead(List<IrStmt> statements, Dictionary<string, int> uses, Func<JLocal, bool> foldable)
    {
        for (int i = statements.Count - 1; i >= 0; i--)
        {
            if (statements[i] is IrAssign { Dst: JLocal temp, Src: JExpr value } && foldable(temp) && uses.GetValueOrDefault(temp.Name) == 0)
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
