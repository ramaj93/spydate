using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Statements javac expands into several, put back together: <c>synchronized</c> from its monitor instructions and
/// the handler that releases the lock, and <c>finally</c> from the copies of it the compiler writes onto every way
/// out of the <c>try</c>. Each rewrite checks the whole shape first — every exit released, every exit carrying
/// its copy — and leaves the code as the compiler wrote it when anything is missing.
/// </summary>
internal static class JavaSugar
{
    /// <summary>
    /// <c>monitorenter(x); try { … monitorexit(v); … } catch (any e) { monitorexit(v); throw e; }</c>, where
    /// <c>v</c> is javac's copy of the lock, is <c>synchronized (x) { … }</c>.
    /// </summary>
    public static void Synchronized(List<CStmt> items)
    {
        for (int i = 0; i + 1 < items.Count; i++)
        {
            if (items[i] is not CRaw { Statement: JMonitor { Enter: true, Lock: var lockValue } }
                || items[i + 1] is not JTry { Catches: [{ CatchType: null, Variable: { } caught, Body: var handler }], Finally: null } attempt
                || JavaTree.Items(handler) is not [CRaw { Statement: JMonitor { Enter: false, Lock: JLocal released } }, CRaw { Statement: JThrow { Exception: JLocal thrown } }]
                || thrown.Name != caught)
            {
                continue;
            }

            var body = JavaTree.Rewrite(attempt.Body, s => s is CRaw { Statement: JMonitor { Enter: false, Lock: JLocal l } } && l.Name == released.Name ? CSeq.Empty : s);
            items[i] = new JSynchronized(lockValue, body);
            items.RemoveAt(i + 1);

            // javac's copy of the lock, kept for the release, goes with it.
            if (i > 0 && items[i - 1] is CRaw { Statement: IrAssign { Dst: JLocal copy, Src: var source } }
                && copy.Name == released.Name && JavaEquality.Same(source, lockValue) && !Mentions(body, copy.Name))
            {
                items.RemoveAt(i - 1);
                i--;
            }
        }
    }

    /// <summary>
    /// A try with a handler for anything that runs some code and rethrows — <c>catch (any e) { F; throw e; }</c> —
    /// is a <c>try … finally { F }</c> when every way out of the try and its other handlers runs a copy of
    /// <c>F</c> first: each return, each jump out, and falling off the end (whose copy may sit just after the try).
    /// The copies go, and a try that then holds nothing but an inner try-catch becomes one try-catch-finally.
    /// </summary>
    public static void Finally(List<CStmt> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not JTry { Finally: null } attempt)
            {
                continue;
            }

            int any = attempt.Catches.ToList().FindIndex(c => c.CatchType is null && c.Variable is not null);
            if (any < 0)
            {
                continue;
            }

            var handler = attempt.Catches[any];
            var handlerItems = JavaTree.Items(handler.Body);
            if (handlerItems.Count == 0 || handlerItems[^1] is not CRaw { Statement: JThrow { Exception: JLocal thrown } } || thrown.Name != handler.Variable)
            {
                continue;
            }

            var copy = handlerItems.Take(handlerItems.Count - 1).ToList();
            if (copy.Any(c => c is not (CRaw or CIf or CSeq)) || copy.SelectMany(JavaTree.Descendants).Any(IsJump))
            {
                continue;
            }

            if (Rewrite(attempt, any, copy, items, i) is { } rewritten)
            {
                items[i] = rewritten.Try;
                items.RemoveRange(i + 1, rewritten.RemovedAfter);
            }
        }
    }

    private static (JTry Try, int RemovedAfter)? Rewrite(JTry attempt, int any, List<CStmt> copy, List<CStmt> items, int index, bool merge = true)
    {
        var inside = JavaTree.Descendants(attempt).Select(Label).Where(l => l != 0).ToHashSet();
        var others = attempt.Catches.Where((_, k) => k != any).ToList();

        // Falling off the end of the body runs a copy — at the end of the body, or just after the try when the
        // compiler left it outside the range (then no handler may fall through to it as well).
        var body = attempt.Body;
        int removedAfter = 0;
        if (!JavaTree.NeverFallsThrough(body))
        {
            if (Tail(body, copy) is { } trimmed)
            {
                body = trimmed;
            }
            else if (copy.Count > 0 && index + copy.Count < items.Count + 1 && items.Count - index - 1 >= copy.Count
                     && JavaEquality.Same(items.Skip(index + 1).Take(copy.Count).ToList(), copy)
                     && others.All(c => JavaTree.NeverFallsThrough(c.Body)))
            {
                removedAfter = copy.Count;
            }
            else if (copy.Count > 0)
            {
                return null;
            }
        }

        if (Strip(body, copy, inside) is not { } stripped)
        {
            return null;
        }

        var catches = new List<JCatch>(others.Count);
        foreach (var other in others)
        {
            var handlerBody = other.Body;
            if (!JavaTree.NeverFallsThrough(handlerBody) && copy.Count > 0)
            {
                if (Tail(handlerBody, copy) is not { } trimmed)
                {
                    return null;
                }

                handlerBody = trimmed;
            }

            if (Strip(handlerBody, copy, inside) is not { } strippedHandler)
            {
                return null;
            }

            catches.Add(other with { Body = strippedHandler });
        }

        var always = JavaTree.Sequence(copy);
        var result = merge && catches.Count == 0 && stripped is JTry { Finally: null } inner
            ? inner with { Finally = always }
            : new JTry(stripped, catches) { Finally = always };
        return (result, removedAfter);
    }

    /// <summary>
    /// <c>R r = init; try { … } catch (Throwable t) { try { r.close(); } catch (Throwable x) { t.addSuppressed(x); } throw t; }</c>,
    /// with <c>r.close()</c> before every way out of the try (each behind <c>if (r != null)</c> when the resource
    /// may be null), is <c>try (R r = init) { … }</c> — javac 11's expansion of it. A try around it whose body is
    /// nothing else takes its catches and finally into it: <c>try (R r = init) { … } catch (E e) { … }</c>.
    /// </summary>
    public static void TryWithResources(List<CStmt> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            // The resources' try inside another try, as javac nests one with catch clauses.
            if (items[i] is JTry { Resources.Count: 0 } outer && JavaTree.Items(outer.Body) is [JTry { Resources.Count: > 0, Catches.Count: 0, Finally: null } withResources])
            {
                items[i] = withResources with { Catches = outer.Catches, Finally = outer.Finally };
                continue;
            }

            if (Java8Resources(items, i))
            {
                continue;
            }

            if (i + 1 >= items.Count
                || items[i] is not CRaw { Statement: IrAssign { Dst: JLocal resource, Src: var init } }
                || items[i + 1] is not JTry { Finally: null, Resources.Count: 0 } attempt)
            {
                continue;
            }

            int handler = -1;
            List<CStmt>? copy = null;
            for (int c = 0; c < attempt.Catches.Count && handler < 0; c++)
            {
                if (ClosesAndRethrows(attempt.Catches[c], resource) is { } close)
                {
                    handler = c;
                    copy = close;
                }
            }

            if (handler < 0 || Rewrite(attempt, handler, copy!, items, i + 1, merge: false) is not { } rewritten)
            {
                continue;
            }

            items[i] = new JTry(rewritten.Try.Body, rewritten.Try.Catches) { Resources = [(resource, init)] };
            items.RemoveAt(i + 1);
            items.RemoveRange(i + 1, rewritten.RemovedAfter);
        }
    }

    /// <summary>
    /// javac 8's expansion of a try-with-resources: <c>R r = init; Throwable primary = null; try { … } catch (Throwable t)
    /// { primary = t; throw t; } finally { if (r != null) { if (primary != null) { try { r.close(); } catch (Throwable x)
    /// { primary.addSuppressed(x); } } else { r.close(); } } }</c> — its finally either rebuilt already, or still a
    /// handler for anything with each exit's copy of it, which a shrinker may have cut down to the plain close.
    /// </summary>
    private static bool Java8Resources(List<CStmt> items, int i)
    {
        if (i + 2 >= items.Count
            || items[i] is not CRaw { Statement: IrAssign { Dst: JLocal resource, Src: var init } }
            || items[i + 1] is not CRaw { Statement: IrAssign { Dst: JLocal primary, Src: JConst { Value: null } } }
            || items[i + 2] is not JTry { Resources.Count: 0 } attempt)
        {
            return false;
        }

        int recorder = attempt.Catches.ToList().FindIndex(c => c.CatchType == "java/lang/Throwable" && Records(c, primary));
        if (recorder < 0)
        {
            return false;
        }

        JTry? rewritten = null;
        if (attempt.Finally is { } always && ClosesWithPrimary(JavaTree.Items(always), resource, primary) is not null)
        {
            rewritten = attempt;
        }
        else if (attempt.Finally is null)
        {
            int any = attempt.Catches.ToList().FindIndex(c => c.CatchType is null && c.Variable is not null);
            if (any >= 0 && JavaTree.Items(attempt.Catches[any].Body) is { Count: >= 2 } handler
                && handler[^1] is CRaw { Statement: JThrow { Exception: JLocal thrown } } && thrown.Name == attempt.Catches[any].Variable
                && ClosesWithPrimary(handler.Take(handler.Count - 1).ToList(), resource, primary) is { } close)
            {
                // Each exit's copy: the finally itself, or what a shrinker left of it once primary is known null there.
                var full = handler.Take(handler.Count - 1).ToList();
                var guarded = new List<CStmt> { new CIf(new IrCondition(IrCondCode.NotEqual, resource, JConst.Null), close, null) };
                foreach (var copy in new[] { full, [close], guarded })
                {
                    if (Rewrite(attempt, any, copy, items, i + 2, merge: false) is { } done)
                    {
                        rewritten = done.Try with { Finally = null };
                        items.RemoveRange(i + 3, done.RemovedAfter);
                        break;
                    }
                }
            }
        }

        if (rewritten is null)
        {
            return false;
        }

        var catches = rewritten.Catches.Where(c => !(c.CatchType == "java/lang/Throwable" && Records(c, primary)) && c.CatchType is not null).ToList();
        items[i] = new JTry(rewritten.Body, catches) { Resources = [(resource, init)] };
        items.RemoveRange(i + 1, 2);
        return true;
    }

    /// <summary>javac 8's handler that notes the primary exception: <c>catch (Throwable t) { primary = t; throw t; }</c>, with any copies of <c>t</c> between.</summary>
    private static bool Records(JCatch handler, JLocal primary)
    {
        if (handler.Variable is not { } caught)
        {
            return false;
        }

        var aliases = new HashSet<string>(StringComparer.Ordinal) { caught };
        bool noted = false;
        foreach (var item in JavaTree.Items(handler.Body))
        {
            switch (item)
            {
                case CRaw { Statement: IrAssign { Dst: JLocal target, Src: JLocal source } } when aliases.Contains(source.Name):
                    aliases.Add(target.Name);
                    noted |= target.Name == primary.Name;
                    break;
                case CRaw { Statement: JThrow { Exception: JLocal thrown } } when aliases.Contains(thrown.Name):
                    return noted;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>The close javac 8 writes: <c>if (primary != null) { try { r.close(); } catch (Throwable x) { primary.addSuppressed(x); } } else { r.close(); }</c>, perhaps inside <c>if (r != null)</c>.</summary>
    private static CStmt? ClosesWithPrimary(IReadOnlyList<CStmt> code, JLocal resource, JLocal primary)
    {
        if (code is [CIf { Else: null } guard] && IsNotNull(guard.Condition, resource))
        {
            code = JavaTree.Items(guard.Then);
        }

        if (code is not [CIf { Else: { } otherwise } choice] || !IsNotNull(choice.Condition, primary))
        {
            return null;
        }

        bool plainClose = JavaTree.Items(otherwise) is [CRaw { Statement: JExprStmt { Expression: JCall { Name: "close", Args.Count: 0, Receiver: JLocal closed } } }] && closed.Name == resource.Name;
        bool suppressing = JavaTree.Items(choice.Then) is [JTry { Catches: [{ CatchType: "java/lang/Throwable", Variable: { } secondary } suppress] } closeTry]
            && JavaTree.Items(closeTry.Body) is [CRaw { Statement: JExprStmt { Expression: JCall { Name: "close", Receiver: JLocal inner } } }] && inner.Name == resource.Name
            && JavaTree.Items(suppress.Body) is [CRaw { Statement: JExprStmt { Expression: JCall { Name: "addSuppressed", Receiver: JLocal onto, Args: [JLocal suppressed] } } }]
            && onto.Name == primary.Name && suppressed.Name == secondary;
        return plainClose && suppressing ? JavaTree.Items(otherwise)[0] : null;
    }

    /// <summary>
    /// For javac's handler of a try-with-resources — close the resource, keep what closing threw as suppressed,
    /// rethrow — the close each exit runs: <c>r.close();</c>, or <c>if (r != null) r.close();</c>. Null otherwise.
    /// </summary>
    private static List<CStmt>? ClosesAndRethrows(JCatch handler, JLocal resource)
    {
        if (handler is not { CatchType: "java/lang/Throwable", Variable: { } primary }
            || JavaTree.Items(handler.Body) is not [var closing, CRaw { Statement: JThrow { Exception: JLocal thrown } }] || thrown.Name != primary)
        {
            return null;
        }

        CIf? guard = null;
        if (closing is CIf { Else: null } nullCheck && IsNotNull(nullCheck.Condition, resource) && JavaTree.Items(nullCheck.Then) is [var inner])
        {
            guard = nullCheck;
            closing = inner;
        }

        if (closing is not JTry { Finally: null, Resources.Count: 0, Catches: [{ CatchType: "java/lang/Throwable", Variable: { } secondary } suppress] } closeTry
            || JavaTree.Items(closeTry.Body) is not [CRaw { Statement: JExprStmt { Expression: JCall { Name: "close", Args.Count: 0, Receiver: JLocal closed } } } close]
            || closed.Name != resource.Name
            || JavaTree.Items(suppress.Body) is not [CRaw { Statement: JExprStmt { Expression: JCall { Name: "addSuppressed", Receiver: JLocal onto, Args: [JLocal suppressed] } } }]
            || onto.Name != primary || suppressed.Name != secondary)
        {
            return null;
        }

        return guard is null ? [close] : [new CIf(guard.Condition, close, null, guard.Va)];
    }

    private static bool IsNotNull(IrExpr condition, JLocal resource)
        => condition is IrCondition { Cc: IrCondCode.NotEqual, Left: JLocal l, Right: JConst { Value: null } } && l.Name == resource.Name;

    /// <summary>The statement without the copy at its end, or null when it does not end with one.</summary>
    private static CStmt? Tail(CStmt statement, List<CStmt> copy)
    {
        var items = JavaTree.Items(statement);
        if (items.Count < copy.Count || !JavaEquality.Same(items.Skip(items.Count - copy.Count).ToList(), copy))
        {
            return null;
        }

        return JavaTree.Sequence(items.Take(items.Count - copy.Count));
    }

    /// <summary>
    /// The statement with the copy removed from in front of every exit — a return, or a jump to a label outside the
    /// try — or null when an exit lacks one. A throw needs none: the handler for anything runs the code then.
    /// </summary>
    private static CStmt? Strip(CStmt statement, List<CStmt> copy, HashSet<int> inside)
    {
        if (copy.Count == 0)
        {
            return statement;
        }

        switch (statement)
        {
            case CSeq seq:
            {
                var kept = new List<CStmt>(seq.Items.Count);
                foreach (var item in seq.Items)
                {
                    if (IsExit(item, inside))
                    {
                        if (kept.Count < copy.Count || !JavaEquality.Same(kept.Skip(kept.Count - copy.Count).ToList(), copy))
                        {
                            return null;
                        }

                        kept.RemoveRange(kept.Count - copy.Count, copy.Count);
                        kept.Add(item);
                        continue;
                    }

                    if (Strip(item, copy, inside) is not { } stripped)
                    {
                        return null;
                    }

                    kept.Add(stripped);
                }

                return new CSeq(kept);
            }

            case var exit when IsExit(exit, inside):
                return null;
            case CIf conditional:
            {
                var then = Strip(conditional.Then, copy, inside);
                var otherwise = conditional.Else is null ? null : Strip(conditional.Else, copy, inside);
                return then is null || (conditional.Else is not null && otherwise is null) ? null : conditional with { Then = then, Else = otherwise };
            }

            case JBlock block:
                return Strip(block.Body, copy, inside) is { } b ? block with { Body = b } : null;
            case JLoop loop:
                return Strip(loop.Body, copy, inside) is { } l ? loop with { Body = l } : null;
            case JSynchronized locked:
                return Strip(locked.Body, copy, inside) is { } s ? locked with { Body = s } : null;
            case JSwitch dispatch:
            {
                var cases = new List<CCase>(dispatch.Cases.Count);
                foreach (var arm in dispatch.Cases)
                {
                    if (Strip(arm.Body, copy, inside) is not { } a)
                    {
                        return null;
                    }

                    cases.Add(arm with { Body = a });
                }

                return dispatch with { Cases = cases };
            }

            case JTry nested:
            {
                if (Strip(nested.Body, copy, inside) is not { } tried)
                {
                    return null;
                }

                var handlers = new List<JCatch>(nested.Catches.Count);
                foreach (var handler in nested.Catches)
                {
                    if (Strip(handler.Body, copy, inside) is not { } h)
                    {
                        return null;
                    }

                    handlers.Add(handler with { Body = h });
                }

                return nested with { Body = tried, Catches = handlers };
            }

            default:
                return statement;
        }
    }

    private static bool IsExit(CStmt statement, HashSet<int> inside) => statement switch
    {
        CRaw { Statement: IrReturn } => true,
        JBreak jump => !inside.Contains(jump.Label),
        JContinue next => !inside.Contains(next.Label),
        _ => false,
    };

    private static bool IsJump(CStmt statement) => statement is JBreak or JContinue or JExit or CGoto or CBreak or CContinue or CRaw { Statement: IrReturn };

    private static int Label(CStmt statement) => statement switch
    {
        JBlock b => b.Label,
        JLoop l => l.Label,
        JSwitch s => s.Label,
        _ => 0,
    };

    private static bool Mentions(CStmt body, string name)
        => JavaTree.Descendants(body).OfType<CRaw>().Any(r => JavaRewrite.Evaluated(r.Statement).Concat(r.Statement is IrAssign a ? [a.Dst] : [])
            .SelectMany(JavaRewrite.PostOrder).Any(e => e is JLocal l && l.Name == name));
}
