namespace Spydate.Decompiler.Native.IR;

/// <summary>Bottom-up expression rewriting and read-set collection helpers shared by passes.</summary>
public static class IrRewriter
{
    /// <summary>
    /// Rebuilds <paramref name="expr"/> bottom-up, calling <paramref name="map"/> on every node after its children
    /// have been rewritten. <paramref name="map"/> returns the replacement (or the same node).
    /// </summary>
    public static IrExpr Rewrite(IrExpr expr, Func<IrExpr, IrExpr> map)
    {
        IrExpr rebuilt = expr switch
        {
            IrMem m => Same(m, m.Address, Rewrite(m.Address, map), a => m with { Address = a }),
            IrAddressOf ao => Same(ao, ao.Target, Rewrite(ao.Target, map), t => ao with { Target = t }),
            IrUnary u => Same(u, u.Operand, Rewrite(u.Operand, map), o => u with { Operand = o }),
            IrBinary b => RewriteBinary(b, map),
            IrCast c => Same(c, c.Operand, Rewrite(c.Operand, map), o => c with { Operand = o }),
            IrCall call => RewriteCall(call, map),
            IrCondition cond => RewriteCondition(cond, map),
            IrTernary t => RewriteTernary(t, map),
            _ => expr,
        };

        return map(rebuilt);
    }

    private static IrExpr Same<T>(T node, IrExpr oldChild, IrExpr newChild, Func<IrExpr, IrExpr> rebuild) where T : IrExpr
        => ReferenceEquals(oldChild, newChild) ? node : rebuild(newChild);

    private static IrExpr RewriteBinary(IrBinary b, Func<IrExpr, IrExpr> map)
    {
        var l = Rewrite(b.Left, map);
        var r = Rewrite(b.Right, map);
        return ReferenceEquals(l, b.Left) && ReferenceEquals(r, b.Right) ? b : b with { Left = l, Right = r };
    }

    private static IrExpr RewriteCondition(IrCondition c, Func<IrExpr, IrExpr> map)
    {
        var l = Rewrite(c.Left, map);
        var r = Rewrite(c.Right, map);
        return ReferenceEquals(l, c.Left) && ReferenceEquals(r, c.Right) ? c : c with { Left = l, Right = r };
    }

    private static IrExpr RewriteTernary(IrTernary t, Func<IrExpr, IrExpr> map)
    {
        var c = Rewrite(t.Condition, map);
        var a = Rewrite(t.Then, map);
        var b = Rewrite(t.Else, map);
        return ReferenceEquals(c, t.Condition) && ReferenceEquals(a, t.Then) && ReferenceEquals(b, t.Else) ? t : t with { Condition = c, Then = a, Else = b };
    }

    private static IrExpr RewriteCall(IrCall call, Func<IrExpr, IrExpr> map)
    {
        var target = Rewrite(call.Target, map);
        bool changed = !ReferenceEquals(target, call.Target);
        var args = new IrExpr[call.Args.Count];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = Rewrite(call.Args[i], map);
            changed |= !ReferenceEquals(args[i], call.Args[i]);
        }

        return changed ? call with { Target = target, Args = args } : call;
    }

    /// <summary>Rewrites every expression inside a statement (reads and writes) with <paramref name="map"/>.</summary>
    public static IrStmt RewriteStmt(IrStmt stmt, Func<IrExpr, IrExpr> map, bool includeDestinations = true)
    {
        return stmt switch
        {
            IrAssign a => a with { Dst = includeDestinations ? Rewrite(a.Dst, map) : a.Dst, Src = Rewrite(a.Src, map) },
            IrStore s => s with { Address = Rewrite(s.Address, map), Value = Rewrite(s.Value, map) },
            IrCallStmt c => c with { Call = (IrCall)Rewrite(c.Call, map), Result = c.Result is null ? null : includeDestinations ? Rewrite(c.Result, map) : c.Result },
            IrReturn r => r.Value is null ? r : r with { Value = Rewrite(r.Value, map) },
            IrBranch b => b with { Condition = Rewrite(b.Condition, map) },
            IrSwitch s => s with { Value = Rewrite(s.Value, map) },
            _ => stmt,
        };
    }

    /// <summary>Enumerates all sub-expressions (pre-order, including the root).</summary>
    public static IEnumerable<IrExpr> Descendants(IrExpr expr)
    {
        var stack = new Stack<IrExpr>();
        stack.Push(expr);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            switch (e)
            {
                case IrMem m: stack.Push(m.Address); break;
                case IrAddressOf ao: stack.Push(ao.Target); break;
                case IrUnary u: stack.Push(u.Operand); break;
                case IrBinary b: stack.Push(b.Left); stack.Push(b.Right); break;
                case IrCast c: stack.Push(c.Operand); break;
                case IrCall call:
                    stack.Push(call.Target);
                    foreach (var a in call.Args) { stack.Push(a); }
                    break;
                case IrCondition cond: stack.Push(cond.Left); stack.Push(cond.Right); break;
                case IrTernary t: stack.Push(t.Condition); stack.Push(t.Then); stack.Push(t.Else); break;
            }
        }
    }

    /// <summary>Expressions read by a statement (destinations excluded, but a memory destination's address is a read).</summary>
    public static IEnumerable<IrExpr> Reads(IrStmt stmt)
    {
        switch (stmt)
        {
            case IrAssign a:
                foreach (var e in Descendants(a.Src)) { yield return e; }
                if (a.Dst is IrMem dm)
                {
                    foreach (var e in Descendants(dm.Address)) { yield return e; }
                }

                break;
            case IrStore s:
                foreach (var e in Descendants(s.Address)) { yield return e; }
                foreach (var e in Descendants(s.Value)) { yield return e; }
                break;
            case IrCallStmt c:
                foreach (var e in Descendants(c.Call)) { yield return e; }
                break;
            case IrReturn r when r.Value is not null:
                foreach (var e in Descendants(r.Value)) { yield return e; }
                break;
            case IrBranch b:
                foreach (var e in Descendants(b.Condition)) { yield return e; }
                break;
            case IrSwitch s:
                foreach (var e in Descendants(s.Value)) { yield return e; }
                break;
        }
    }

    /// <summary>The variable (register / temp / local) written by a statement, if any.</summary>
    public static IrExpr? Destination(IrStmt stmt) => stmt switch
    {
        IrAssign { Dst: IrReg or IrTemp or IrLocal } a => a.Dst,
        IrCallStmt { Result: not null } c => c.Result,
        _ => null,
    };

    /// <summary>A named global is a memory read too, so a store must invalidate anything that holds one.</summary>
    public static bool ContainsMemoryRead(IrExpr expr) => Descendants(expr).Any(e => e is IrMem or IrGlobal);

    public static bool ContainsCallOrUnknown(IrExpr expr) => Descendants(expr).Any(e => e is IrCall or IrUnknown);
}
