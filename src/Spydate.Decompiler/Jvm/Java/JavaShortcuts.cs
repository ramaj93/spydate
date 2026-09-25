using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// The language's shortcuts put back from what javac expands them into: for-each over an array or an
/// <c>Iterable</c>, a switch on a string, <c>assert</c>, try-with-resources, and an assignment used as a value in a
/// loop's test. Each rewrite matches javac's whole expansion — including that the variables javac introduced for
/// it appear nowhere else in the method — and anything short of that stays as it was.
/// </summary>
internal static class JavaShortcuts
{
    /// <summary>
    /// <c>T[] a = e; int n = a.length; for (int i = 0; i &lt; n; i++) { T x = a[i]; … }</c> is <c>for (T x : e)</c>;
    /// <c>Iterator it = e.iterator(); while (it.hasNext()) { T x = (T) it.next(); … }</c> is too.
    /// </summary>
    public static void ForEach(List<CStmt> items, IReadOnlyDictionary<string, int> counts, Func<string, bool> compilerMade, Func<IrExpr, bool> generic)
    {
        for (int k = 0; k < items.Count; k++)
        {
            if (items[k] is not JLoop { Kind: CLoopKind.While, ForEach: null } loop)
            {
                continue;
            }

            var body = JavaTree.Items(loop.Body).ToList();
            if (body.Count == 0 || body[0] is not CRaw { Statement: IrAssign { Dst: JLocal element, Src: var read } })
            {
                continue;
            }

            // Over an array.
            if (k >= 2 && loop is { Init: IrAssign { Dst: JLocal i, Src: JConst { Value: 0 } }, Update: IrAssign { Dst: JLocal i2, Src: JBinary { Op: "+", Left: JLocal i3, Right: JConst { Value: 1 } } } }
                && loop.Condition is IrCondition { Cc: IrCondCode.Less, Left: JLocal i4, Right: JLocal n }
                && items[k - 1] is CRaw { Statement: IrAssign { Dst: JLocal n2, Src: JArrayLength { Array: JLocal a } } }
                && items[k - 2] is CRaw { Statement: IrAssign { Dst: JLocal a2, Src: var source } }
                && read is JArrayElement { Array: JLocal a3, Index: JLocal i5 }
                && Same(i, i2, i3, i4, i5) && Same(n, n2) && Same(a, a2, a3)
                && compilerMade(i.Name) && compilerMade(n.Name) && compilerMade(a.Name)
                && counts.GetValueOrDefault(i.Name) == 5 && counts.GetValueOrDefault(n.Name) == 2 && counts.GetValueOrDefault(a.Name) == 3
                && OnlyInside(element, loop, counts))
            {
                items[k] = loop with { Init = null, Update = null, ForEach = (element, source), Body = JavaTree.Sequence(body.Skip(1)) };
                items.RemoveRange(k - 2, 2);
                k -= 2;
                continue;
            }

            // Over an Iterable.
            if (k >= 1 && loop is { Update: null, Condition: JCall { Name: "hasNext", Args.Count: 0, Receiver: JLocal it } }
                && items[k - 1] is CRaw { Statement: IrAssign { Dst: JLocal it2, Src: JCall { Name: "iterator", Args.Count: 0, Receiver: { } iterable } } }
                && Unwrapped(read) is JCall { Name: "next", Args.Count: 0, Receiver: JLocal it3 }
                && Same(it, it2, it3) && compilerMade(it.Name) && counts.GetValueOrDefault(it.Name) == 3
                && OnlyInside(element, loop, counts)

                // A cast on the element says the source's iterable had a type argument: the one printed must have it too.
                && (read is JCall || generic(iterable)))
            {
                items[k] = loop with { ForEach = (element, iterable), Body = JavaTree.Sequence(body.Skip(1)) };
                items.RemoveAt(k - 1);
                k--;
            }
        }
    }

    /// <summary>What an element read is under the checkcast and unboxing javac puts on it.</summary>
    private static IrExpr Unwrapped(IrExpr read) => read switch
    {
        JCast cast => Unwrapped(cast.Operand),
        JCall { Kind: JCallKind.Virtual, Args.Count: 0, Name: "intValue" or "longValue" or "doubleValue" or "floatValue" or "booleanValue" or "charValue" or "shortValue" or "byteValue", Receiver: { } boxed } => Unwrapped(boxed),
        _ => read,
    };

    private static bool Same(params JLocal[] locals) => locals.All(l => l.Name == locals[0].Name);

    /// <summary>Whether every mention of a variable is inside the loop — so the loop can declare it.</summary>
    private static bool OnlyInside(JLocal variable, JLoop loop, IReadOnlyDictionary<string, int> counts)
        => JavaTree.CountLocals(loop).GetValueOrDefault(variable.Name) == counts.GetValueOrDefault(variable.Name);

    /// <summary>
    /// <c>int t = -1; switch (s.hashCode()) { case 108300: if (s.equals("mon")) t = 0; … } switch (t) { case 0: … }</c> is
    /// <c>switch (s) { case "mon": … }</c>: the first switch only picks a number per string, the second is the source's.
    /// </summary>
    public static void StringSwitch(List<CStmt> items, IReadOnlyDictionary<string, int> counts, Func<string, bool> compilerMade)
    {
        for (int k = 1; k + 1 < items.Count; k++)
        {
            if (items[k] is not JSwitch { Value: JCall { Name: "hashCode", Descriptor: "()I", Args.Count: 0, Receiver: JLocal s } } hashes
                || items[k - 1] is not CRaw { Statement: IrAssign { Dst: JLocal t, Src: JConst { Value: -1 } } }
                || items[k + 1] is not JSwitch { Value: JLocal t2, Names: null } cases || t2.Name != t.Name || !compilerMade(t.Name))
            {
                continue;
            }

            var names = new Dictionary<int, string>();
            var equals = new List<string>();
            bool understood = hashes.Cases.All(arm => JavaTree.Items(arm.Body).All(item => Picks(item, s, t, hashes.Label, names, equals)));
            int equalsCalls = equals.Count;
            if (!understood || names.Count == 0 || counts.GetValueOrDefault(t.Name) != names.Count + 2)
            {
                continue;
            }

            IrExpr value = s;
            int removeFrom = k - 1;
            if (k >= 2 && items[k - 2] is CRaw { Statement: IrAssign { Dst: JLocal s2, Src: var source } } && s2.Name == s.Name
                && compilerMade(s.Name) && counts.GetValueOrDefault(s.Name) == equalsCalls + 2)
            {
                value = source;
                removeFrom = k - 2;
            }

            items[k + 1] = cases with { Value = value, Names = names };
            items.RemoveRange(removeFrom, k + 1 - removeFrom);
            k = removeFrom;
        }
    }

    /// <summary>One statement of a hash arm: <c>if (s.equals("lit")) { t = k; }</c>, possibly chained, or a break.</summary>
    private static bool Picks(CStmt item, JLocal s, JLocal t, int label, Dictionary<int, string> names, List<string> equals)
    {
        switch (item)
        {
            case JBreak b when b.Label == label:
                return true;
            case CIf { Condition: JCall { Name: "equals", Args: [JConst { Value: string literal }], Receiver: JLocal receiver } } conditional when receiver.Name == s.Name:
                var then = JavaTree.Items(conditional.Then).Where(x => !(x is JBreak b && b.Label == label)).ToList();
                if (then is not [CRaw { Statement: IrAssign { Dst: JLocal target, Src: JConst { Value: int number } } }] || target.Name != t.Name)
                {
                    return false;
                }

                names[number] = JavaEmitter.Quote(literal);
                equals.Add(literal);
                return conditional.Else is null || JavaTree.Items(conditional.Else).All(e => Picks(e, s, t, label, names, equals));
            default:
                return JavaTree.IsEmpty(item);
        }
    }

    /// <summary>
    /// A switch whose every arm ends by giving one stack value — <c>s = v; break;</c> — or throws, followed by the one
    /// use of that value, is a switch expression: <c>return switch (k) { case 1 -&gt; 10; … };</c>. A value javac kept
    /// on the stack is what says so; a local of the source's own assigned in each arm keeps its switch statement.
    /// </summary>
    public static void SwitchExpression(List<CStmt> items, IReadOnlyDictionary<string, int> counts)
    {
        for (int i = 0; i + 1 < items.Count; i++)
        {
            if (items[i] is not JSwitch dispatch || dispatch.Cases.Count == 0)
            {
                continue;
            }

            JLocal? variable = null;
            var arms = new List<JSwitchArm>();
            bool understood = true;
            for (int c = 0; c < dispatch.Cases.Count && understood; c++)
            {
                var body = JavaTree.Items(dispatch.Cases[c].Body).Where(x => !JavaTree.IsEmpty(x)).ToList();
                if (body.Count > 0 && body[^1] is JBreak end && end.Label == dispatch.Label)
                {
                    body.RemoveAt(body.Count - 1);
                }
                else if (c < dispatch.Cases.Count - 1 && !(body.Count > 0 && body[^1] is CRaw { Statement: JThrow }))
                {
                    understood = false;
                    break;
                }

                JExpr? result = null;
                if (body.Count > 0 && body[^1] is CRaw { Statement: IrAssign { Dst: JLocal { Kind: JLocalKind.Stack } stored, Src: JExpr value } })
                {
                    if (variable is not null && variable.Name != stored.Name)
                    {
                        understood = false;
                        break;
                    }

                    variable = stored;
                    result = value;
                    body.RemoveAt(body.Count - 1);
                }
                else if (body.Count == 0 || body[^1] is not CRaw { Statement: JThrow })
                {
                    understood = false;
                    break;
                }

                // An arm of a switch expression cannot leave it by a jump or a return.
                if (body.SelectMany(JavaTree.Descendants).Any(d => d is JBreak or JContinue or JExit or CRaw { Statement: IrReturn }))
                {
                    understood = false;
                    break;
                }

                if (result is not null)
                {
                    body.Add(new CRaw(new JYield(result)));
                }

                arms.Add(new JSwitchArm(dispatch.Cases[c].Labels, new CSeq(body)));
            }

            if (!understood || variable is null || counts.GetValueOrDefault(variable.Name) != arms.Count(a => a.Body.Items.LastOrDefault() is CRaw { Statement: JYield }) + 1
                || JavaTree.CountLocals(items[i + 1]).GetValueOrDefault(variable.Name) != 1)
            {
                continue;
            }

            var expression = new JSwitchExpr(dispatch.Value, arms, dispatch.Va) { Names = dispatch.Names, Patterns = dispatch.Patterns };
            items[i] = new CRaw(new IrAssign(variable, expression) { Va = dispatch.Va });
        }
    }

    /// <summary>
    /// <c>if (!$assertionsDisabled &amp;&amp; !c) throw new AssertionError(m);</c> is <c>assert c : m;</c> — the field is the
    /// class's switch for assertions, which the JVM turns off by default.
    /// </summary>
    public static void Assert(List<CStmt> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not CIf { Else: null, Then: var then } conditional
                || JavaTree.Items(then) is not [CRaw { Statement: JThrow { Exception: JNew { Owner: "java/lang/AssertionError", Args: var args } } }]
                || args.Count > 1)
            {
                continue;
            }

            IrExpr? condition = conditional.Condition switch
            {
                JLogical { And: true, Left: var left, Right: var right } when AssertionsEnabled(left) => JLogical.Not(right),
                var only when AssertionsEnabled(only) => new JConst(0, "Z"),
                _ => null,
            };

            if (condition is not null)
            {
                items[i] = new JAssert(condition, args.FirstOrDefault());
            }
        }
    }

    /// <summary><c>!$assertionsDisabled</c>, however the lifter spelled the test of that boolean.</summary>
    private static bool AssertionsEnabled(IrExpr test) => test switch
    {
        IrUnary { Op: IrUnaryOp.LogicalNot, Operand: JField { Name: "$assertionsDisabled", Instance: null } } => true,
        IrCondition { Cc: IrCondCode.Equal, Left: JField { Name: "$assertionsDisabled", Instance: null }, Right: JConst { Value: 0 } } => true,
        _ => false,
    };

    /// <summary>
    /// <c>while (true) { x = next(); if (x == null) break; … }</c> is <c>while ((x = next()) != null) { … }</c> — the
    /// loop reading a value and testing it at once, as <c>readLine</c> loops are written.
    /// </summary>
    public static JLoop? AssignedInTest(JLoop loop)
    {
        var items = JavaTree.Items(loop.Body);
        if (loop.Kind != CLoopKind.Forever || items.Count < 2
            || items[0] is not CRaw { Statement: IrAssign { Dst: JLocal { Kind: JLocalKind.Local } variable, Src: JExpr value } }
            || items[1] is not CIf { Then: JBreak exit, Else: null, Condition: var test } || exit.Label != loop.Label
            || !JavaRewrite.PostOrder(value).Any(e => e is JCall or JNew or JDynamic)
            || JavaRewrite.PostOrder(test).Count(e => e is JLocal l && l.Name == variable.Name) != 1
            || JavaRewrite.PostOrder(value).Any(e => e is JLocal l && l.Name == variable.Name))
        {
            return null;
        }

        var assigned = new JAssignExpr(variable, value);
        var condition = JavaRewrite.Replace(JLogical.Not(test), l => l.Name == variable.Name ? assigned : null);
        return loop with { Kind = CLoopKind.While, Condition = condition, Body = JavaTree.Sequence(items.Skip(2)) };
    }
}
