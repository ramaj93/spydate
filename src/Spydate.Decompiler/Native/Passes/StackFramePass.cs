using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// Simulates the stack pointer through the function (pushes, pops, <c>sub/add rsp</c>, frame-pointer setup)
/// so that every stack access can be expressed relative to the entry stack pointer and named:
/// <c>local_XX</c> below the return address, <c>arg_XX</c> above it. Stack-pointer bookkeeping statements are
/// removed, <c>lea</c> of a stack slot becomes <c>&amp;local_XX</c>, and calls receive their arguments:
/// on x64 the contiguous prefix of <c>rcx, rdx, r8, r9</c> defined since the previous call in the block;
/// on x86 the values pushed since the previous call (cdecl/stdcall convention).
/// </summary>
public sealed class StackFramePass : IIrPass
{
    public string Name => "stack-frame";

    private static readonly string[] Win64ArgRegs = { "rcx", "rdx", "r8", "r9" };

    public void Run(IrFunction function)
    {
        var ctx = new Context(function);
        ctx.ComputeDepths();
        ctx.RewriteAndCleanup();
        ctx.RecoverCallArguments();
        ctx.ElideSavedRegisters();
        ctx.RestoreAliasSetupsStillInUse();
        ctx.RemoveNops();
    }

    private sealed class Context
    {
        private readonly IrFunction _fn;
        private readonly int _ptr;
        private readonly string _sp;

        /// <summary>Frame-pointer aliases: register → frame offset it points at (rbp after "mov rbp, rsp", r11, …).</summary>
        private sealed record Aliases(Dictionary<string, long> Map)
        {
            public static readonly Aliases Empty = new(new Dictionary<string, long>(StringComparer.Ordinal));

            public long? Of(string reg) => Map.TryGetValue(reg, out long d) ? d : null;

            public Aliases With(string reg, long depth)
            {
                var m = new Dictionary<string, long>(Map, StringComparer.Ordinal) { [reg] = depth };
                return new Aliases(m);
            }

            public Aliases Without(string reg)
            {
                if (!Map.Keys.Any(k => RegisterAliases.Overlap(k, reg)))
                {
                    return this;
                }

                var m = new Dictionary<string, long>(Map, StringComparer.Ordinal);
                foreach (var k in Map.Keys.Where(k => RegisterAliases.Overlap(k, reg)).ToList())
                {
                    m.Remove(k);
                }

                return new Aliases(m);
            }

            public bool SameAs(Aliases other) => Map.Count == other.Map.Count && Map.All(kv => other.Map.TryGetValue(kv.Key, out long v) && v == kv.Value);
        }

        // State *before* each statement; depth null = unknown.
        private readonly Dictionary<IrBlock, long?[]> _depthBefore = new();
        private readonly Dictionary<IrBlock, Aliases[]> _aliasesBefore = new();
        private readonly Dictionary<IrBlock, long?> _entryDepth = new();
        private readonly Dictionary<IrBlock, Aliases> _entryAliases = new();
        private readonly HashSet<IrStmt> _removable = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IrStmt, string> _aliasSetups = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IrStmt, IrStmt> _removedOriginals = new(ReferenceEqualityComparer.Instance);

        public Context(IrFunction fn)
        {
            _fn = fn;
            _ptr = fn.Bitness / 8;
            _sp = fn.Bitness == 64 ? "rsp" : "esp";
        }

        // ------------------------------------------------------------------
        // 1. Depth simulation
        // ------------------------------------------------------------------

        public void ComputeDepths()
        {
            var entry = _fn.Blocks.FirstOrDefault(b => b.StartVa == _fn.EntryVa) ?? _fn.Blocks.FirstOrDefault();
            if (entry is null)
            {
                return;
            }

            var byVa = _fn.Blocks.ToDictionary(b => b.StartVa);
            var work = new Queue<IrBlock>();
            _entryDepth[entry] = 0;
            _entryAliases[entry] = Aliases.Empty;
            work.Enqueue(entry);

            while (work.Count > 0)
            {
                var block = work.Dequeue();
                long? depth = _entryDepth[block];
                var aliases = _entryAliases[block];
                var depths = new long?[block.Statements.Count];
                var aliasList = new Aliases[block.Statements.Count];

                for (int i = 0; i < block.Statements.Count; i++)
                {
                    depths[i] = depth;
                    aliasList[i] = aliases;
                    Step(block, i, ref depth, ref aliases);
                }

                _depthBefore[block] = depths;
                _aliasesBefore[block] = aliasList;

                foreach (ulong succVa in block.Successors)
                {
                    if (!byVa.TryGetValue(succVa, out var succ))
                    {
                        continue;
                    }

                    if (!_entryDepth.TryGetValue(succ, out var known))
                    {
                        _entryDepth[succ] = depth;
                        _entryAliases[succ] = aliases;
                        work.Enqueue(succ);
                    }
                    else if (known != depth || !_entryAliases[succ].SameAs(aliases))
                    {
                        // Conflicting states (e.g. unbalanced paths) → unknown from here on.
                        if (known is not null)
                        {
                            _entryDepth[succ] = null;
                            _entryAliases[succ] = Aliases.Empty;
                            work.Enqueue(succ);
                        }
                    }
                }
            }
        }

        private void Step(IrBlock block, int index, ref long? depth, ref Aliases aliases)
        {
            var stmt = block.Statements[index];
            switch (stmt)
            {
                case IrAssign { Dst: IrReg dst } a when dst.Name == _sp:
                    {
                        long? target = FrameOffset(a.Src, depth, aliases);
                        if (target is not null || a.Src is IrReg { } r0 && aliases.Of(r0.Name) is not null)
                        {
                            depth = target;
                            _removable.Add(stmt);
                        }
                        else
                        {
                            depth = null; // alignment (and rsp, -16), mov rsp, [x], …
                        }

                        return;
                    }

                case IrAssign { Dst: IrReg dst } a when IsGpr(dst.Name) && dst.Bits == _fn.Bitness && FrameOffset(a.Src, depth, aliases) is { } fo:
                    // mov rbp, rsp / mov r11, rsp / lea rbp, [rsp+20h] → dst is now a frame pointer alias.
                    aliases = aliases.Without(dst.Name).With(dst.Name, fo);
                    _removable.Add(stmt);
                    _aliasSetups[stmt] = dst.Name;
                    return;

                case IrAssign { Dst: IrReg dst } when RegisterAliases.Overlap(dst.Name, _sp):
                    depth = null;
                    return;

                case IrAssign { Dst: IrReg dst }:
                    aliases = aliases.Without(dst.Name);
                    return;

                case IrCallStmt call:
                    if (call.Result is IrReg res)
                    {
                        aliases = aliases.Without(res.Name);
                    }

                    // Volatile registers do not survive a call.
                    foreach (var reg in aliases.Map.Keys.ToList())
                    {
                        if (IsVolatile(reg))
                        {
                            aliases = aliases.Without(reg);
                        }
                    }

                    if (_fn.Bitness == 32 && depth is not null)
                    {
                        // stdcall callee pops its arguments; cdecl callers adjust esp themselves afterwards.
                        // Heuristic: if no "esp = esp + c" follows before the next call/return in this block,
                        // assume the callee removed the arguments pushed since the previous call.
                        depth += CalleeCleanupBytes(block, index);
                        MarkJunkPops(block, index);
                    }

                    return;
            }
        }

        private bool IsVolatile(string reg)
        {
            string c = RegisterAliases.CanonicalOf(reg);
            return _fn.Bitness == 64
                ? c is "rax" or "rcx" or "rdx" or "r8" or "r9" or "r10" or "r11"
                : c is "rax" or "rcx" or "rdx";
        }

        private static bool IsGpr(string reg)
        {
            string c = RegisterAliases.CanonicalOf(reg);
            return c is "rax" or "rbx" or "rcx" or "rdx" or "rsi" or "rdi" or "rbp" || (c.Length is 2 or 3 && c[0] == 'r' && char.IsDigit(c[1]));
        }

        /// <summary>"pop ecx" / "pop edx" right after a call only discard arguments; the loaded garbage is dropped.</summary>
        private void MarkJunkPops(IrBlock block, int callIndex)
        {
            for (int j = callIndex + 1; j + 1 < block.Statements.Count; j += 2)
            {
                if (block.Statements[j] is IrAssign { Dst: IrReg junk, Src: IrMem { Address: IrReg pr } } load && pr.Name == _sp
                    && RegisterAliases.CanonicalOf(junk.Name) is "rcx" or "rdx"
                    && block.Statements[j + 1] is IrAssign { Dst: IrReg d, Src: IrBinary { Op: IrBinaryOp.Add, Left: IrReg l, Right: IrConst c } } && d.Name == _sp && l.Name == _sp && c.Value == _ptr)
                {
                    _removable.Add(load);
                    continue;
                }

                break;
            }
        }

        private long CalleeCleanupBytes(IrBlock block, int callIndex)
        {
            // Caller cleanup (cdecl) is recognisable *immediately* after the call: "add esp, N", or the
            // "pop ecx" / "pop edx" junk-pop idiom. Anything else (a callee-saved "pop ebp", another call,
            // a return, ordinary code) means the callee removed its arguments (stdcall/thiscall/fastcall).
            int j = callIndex + 1;
            if (j < block.Statements.Count)
            {
                var next = block.Statements[j];
                if (next is IrAssign { Dst: IrReg d, Src: IrBinary { Op: IrBinaryOp.Add, Left: IrReg l, Right: IrConst } } && d.Name == _sp && l.Name == _sp)
                {
                    return 0;
                }

                if (next is IrAssign { Dst: IrReg junk, Src: IrMem { Address: IrReg pr } } && pr.Name == _sp
                    && RegisterAliases.CanonicalOf(junk.Name) is "rcx" or "rdx")
                {
                    return 0;
                }
            }

            long pushed = 0;
            for (int k = callIndex - 1; k >= 0; k--)
            {
                var s = block.Statements[k];
                if (s is IrCallStmt)
                {
                    break;
                }

                if (s is IrAssign { Dst: IrReg pd, Src: IrBinary { Op: IrBinaryOp.Sub, Left: IrReg pl, Right: IrConst c } } && pd.Name == _sp && pl.Name == _sp && c.Value == _ptr
                    && k + 1 < block.Statements.Count && block.Statements[k + 1] is IrStore { Address: IrReg sr } && sr.Name == _sp)
                {
                    pushed += _ptr;
                }
            }

            return pushed;
        }

        // ------------------------------------------------------------------
        // 2. Rewrite stack accesses into locals; drop bookkeeping
        // ------------------------------------------------------------------

        public void RewriteAndCleanup()
        {
            foreach (var block in _fn.Blocks)
            {
                if (!_depthBefore.TryGetValue(block, out var depths))
                {
                    continue; // unreachable block: leave as is
                }

                var aliasList = _aliasesBefore[block];
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    var stmt = block.Statements[i];
                    if (_removable.Contains(stmt))
                    {
                        var nop = new IrNop { Va = stmt.Va };
                        _removedOriginals[nop] = stmt;
                        block.Statements[i] = nop;
                        continue;
                    }

                    long? depth = depths[i];
                    var aliases = aliasList[i];
                    if (depth is null && aliases.Map.Count == 0)
                    {
                        continue;
                    }

                    // Stores whose address is a frame slot become assignments to the local.
                    if (stmt is IrStore st && FrameOffset(st.Address, depth, aliases) is { } fo)
                    {
                        var local = Local(fo, st.Bits);
                        block.Statements[i] = new IrAssign(local, IrRewriter.Rewrite(st.Value, e => MapExpr(e, depth, aliases))) { Va = st.Va };
                        continue;
                    }

                    // Destinations are never mapped: "pop rbp" writes the register, it does not store to the frame.
                    block.Statements[i] = IrRewriter.RewriteStmt(stmt, e => MapExpr(e, depth, aliases), includeDestinations: false);
                }
            }
        }

        /// <summary>A frame-pointer alias that is still read somewhere (used as a general register too) keeps its setup.</summary>
        public void RestoreAliasSetupsStillInUse()
        {
            var stillRead = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in _fn.AllStatements.SelectMany(IrRewriter.Reads))
            {
                if (e is IrReg r)
                {
                    stillRead.Add(RegisterAliases.CanonicalOf(r.Name));
                }
            }

            foreach (var block in _fn.Blocks)
            {
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    if (block.Statements[i] is IrNop nop && _removedOriginals.TryGetValue(nop, out var original)
                        && _aliasSetups.TryGetValue(original, out var reg) && stillRead.Contains(RegisterAliases.CanonicalOf(reg)))
                    {
                        // Re-express the setup in terms of the frame: rbp = &local_XX.
                        long? depth = _depthBefore[block][i];
                        var aliases = _aliasesBefore[block][i];
                        block.Statements[i] = IrRewriter.RewriteStmt(original, e => MapExpr(e, depth, aliases));
                    }
                }
            }
        }

        private IrExpr MapExpr(IrExpr e, long? depth, Aliases aliases)
        {
            switch (e)
            {
                // Rewriting is bottom-up, so by the time we see the IrMem its sp±k address has already
                // become &local; turn the dereference of that into the local itself.
                case IrMem { Address: IrAddressOf { Target: IrLocal ao } } m:
                    return Local(ao.FrameOffset, m.Bits);
                case IrBinary { Op: IrBinaryOp.Add, Left: IrAddressOf { Target: IrLocal ao }, Right: IrConst c }:
                    return new IrAddressOf(Local(ao.FrameOffset + c.Value, 0), _fn.Bitness);
                case IrBinary { Op: IrBinaryOp.Sub, Left: IrAddressOf { Target: IrLocal ao }, Right: IrConst c }:
                    return new IrAddressOf(Local(ao.FrameOffset - c.Value, 0), _fn.Bitness);
                case IrBinary or IrReg when FrameOffset(e, depth, aliases) is { } fo:
                    // A bare frame address (lea rax, [rsp+20h]) → &local.
                    return new IrAddressOf(Local(fo, 0), _fn.Bitness);
                default:
                    return e;
            }
        }

        /// <summary>Frame offset (relative to the entry stack pointer) for sp±k or alias±k, or null.</summary>
        private long? FrameOffset(IrExpr address, long? depth, Aliases aliases)
        {
            long? Base(IrReg r) => r.Name == _sp ? depth : aliases.Of(r.Name);

            return address switch
            {
                IrReg r => Base(r),
                IrBinary { Op: IrBinaryOp.Add, Left: IrReg r, Right: IrConst c } => Base(r) + c.Value,
                IrBinary { Op: IrBinaryOp.Sub, Left: IrReg r, Right: IrConst c } => Base(r) - c.Value,
                _ => null,
            };
        }

        private IrLocal Local(long frameOffset, int bits)
        {
            string name = frameOffset switch
            {
                0 => "return_address",
                > 0 => $"arg_{frameOffset - _ptr:X}",
                _ => $"local_{-frameOffset:X}",
            };

            if (_fn.Locals.TryGetValue(name, out var existing))
            {
                if (bits > existing.Bits)
                {
                    existing = new IrLocal(name, bits, frameOffset);
                    _fn.Locals[name] = existing;
                }

                return bits == 0 || bits == existing.Bits ? existing : new IrLocal(name, bits, frameOffset);
            }

            var local = new IrLocal(name, bits == 0 ? 8 : bits, frameOffset);
            _fn.Locals[name] = local;
            return local;
        }

        // ------------------------------------------------------------------
        // 3. Call argument recovery
        // ------------------------------------------------------------------

        public void RecoverCallArguments()
        {
            var byVa = _fn.Blocks.ToDictionary(b => b.StartVa);
            foreach (var block in _fn.Blocks)
            {
                _depthBefore.TryGetValue(block, out var depths);
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    IrCall? call = block.Statements[i] switch
                    {
                        IrCallStmt cs => cs.Call,
                        IrReturn { Value: IrCall tail } => tail,
                        _ => null,
                    };
                    if (call is null || call.Args.Count > 0 || call.Target is IrSymbol { Name: "__debugbreak" or "__halt" or "__ud2" })
                    {
                        continue;
                    }

                    var args = _fn.Bitness == 64
                        ? Win64Args(block, i, byVa)
                        : X86Args(block, i, depths is not null && i < depths.Length ? depths[i] : null);
                    if (args.Count == 0)
                    {
                        continue;
                    }

                    var newCall = call with { Args = args };
                    block.Statements[i] = block.Statements[i] switch
                    {
                        IrCallStmt cs => cs with { Call = newCall },
                        IrReturn r => r with { Value = newCall },
                        var other => other,
                    };
                }
            }
        }

        /// <summary>
        /// Contiguous prefix of rcx/rdx/r8/r9 defined since the previous call, scanning backwards through
        /// this block and then through single-predecessor blocks (a jcc between "mov rcx, x" and the call is common).
        /// </summary>
        private static List<IrExpr> Win64Args(IrBlock block, int callIndex, Dictionary<ulong, IrBlock> byVa)
        {
            var defined = new IrReg?[Win64ArgRegs.Length];
            var current = block;
            int start = callIndex - 1;
            var visited = new HashSet<IrBlock> { block };
            for (int hop = 0; hop < 4 && current is not null; hop++)
            {
                for (int j = start; j >= 0; j--)
                {
                    var s = current.Statements[j];
                    if (s is IrCallStmt)
                    {
                        return Prefix(defined);
                    }

                    if (IrRewriter.Destination(s) is IrReg r)
                    {
                        for (int k = 0; k < Win64ArgRegs.Length; k++)
                        {
                            if (defined[k] is null && RegisterAliases.Overlap(r.Name, Win64ArgRegs[k]))
                            {
                                defined[k] = r;
                            }
                        }
                    }
                }

                if (current.Predecessors.Count == 0)
                {
                    // Reached function entry: registers below the highest explicitly-set one still hold the
                    // incoming arguments, so pass them through (rcx, 1) rather than dropping the whole list.
                    int highest = Array.FindLastIndex(defined, d => d is not null);
                    for (int k = 0; k < highest; k++)
                    {
                        defined[k] ??= new IrReg(Win64ArgRegs[k], 64);
                    }

                    break;
                }

                if (current.Predecessors.Count != 1 || !byVa.TryGetValue(current.Predecessors[0], out var pred) || !visited.Add(pred))
                {
                    break;
                }

                current = pred;
                start = current.Statements.Count - 1;
            }

            return Prefix(defined);

            static List<IrExpr> Prefix(IrReg?[] defined)
            {
                var args = new List<IrExpr>();
                foreach (var reg in defined)
                {
                    if (reg is null)
                    {
                        break; // contiguous prefix only
                    }

                    args.Add(reg);
                }

                return args;
            }
        }

        /// <summary>
        /// Values pushed since the previous call become the arguments (cdecl/stdcall: last push = first argument).
        /// When the pushed expression is unchanged between push and call, the push is removed and the value
        /// is placed directly in the call; otherwise the named stack slot is passed.
        /// </summary>
        private List<IrExpr> X86Args(IrBlock block, int callIndex, long? depthAtCall)
        {
            if (depthAtCall is null)
            {
                return new List<IrExpr>();
            }

            var pushed = new SortedDictionary<long, int>();
            for (int j = callIndex - 1; j >= 0; j--)
            {
                var s = block.Statements[j];
                if (s is IrCallStmt)
                {
                    break;
                }

                if (s is IrAssign { Dst: IrLocal l } && l.FrameOffset >= depthAtCall && l.FrameOffset < 0 && !pushed.ContainsKey(l.FrameOffset))
                {
                    pushed[l.FrameOffset] = j;
                }
            }

            var args = new List<IrExpr>();
            var consumed = new List<int>();
            long expected = depthAtCall.Value;
            foreach (var (offset, index) in pushed)
            {
                if (offset != expected)
                {
                    break; // arguments must be contiguous from the call depth upwards
                }

                var push = (IrAssign)block.Statements[index];
                if (IsStableBetween(block, push.Src, index + 1, callIndex - 1))
                {
                    args.Add(push.Src);
                    consumed.Add(index);
                }
                else
                {
                    args.Add(push.Dst);
                }

                expected += _ptr;
            }

            foreach (int index in consumed)
            {
                block.Statements[index] = new IrNop { Va = block.Statements[index].Va };
            }

            return args;
        }

        /// <summary>True when nothing in [from, to] writes a variable read by <paramref name="value"/> (or memory, if it reads memory).</summary>
        private static bool IsStableBetween(IrBlock block, IrExpr value, int from, int to)
        {
            bool readsMemory = IrRewriter.ContainsMemoryRead(value);
            var vars = IrRewriter.Descendants(value).Where(e => e is IrReg or IrTemp or IrLocal).ToList();
            for (int j = from; j <= to; j++)
            {
                var s = block.Statements[j];
                if (readsMemory && (s is IrStore || s is IrCallStmt || (s is IrAssign { Dst: IrLocal })))
                {
                    return false;
                }

                if (IrRewriter.Destination(s) is { } written && vars.Any(v => RegisterAliases.MayAlias(v, written)))
                {
                    return false;
                }
            }

            return true;
        }

        // ------------------------------------------------------------------
        // 4. Saved-register spill/restore elision
        // ------------------------------------------------------------------

        public void ElideSavedRegisters()
        {
            // local_X = reg (first write) … reg = local_X (last read) with no other use of local_X → drop both.
            var writes = new Dictionary<string, List<(IrBlock Block, int Index)>>();
            var reads = new Dictionary<string, List<(IrBlock Block, int Index)>>();
            foreach (var block in _fn.Blocks)
            {
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    var s = block.Statements[i];
                    if (s is IrAssign { Dst: IrLocal wl })
                    {
                        Add(writes, wl.Name, (block, i));
                    }

                    foreach (var e in IrRewriter.Reads(s))
                    {
                        if (e is IrLocal rl)
                        {
                            Add(reads, rl.Name, (block, i));
                        }
                        else if (e is IrAddressOf { Target: IrLocal ao })
                        {
                            Add(reads, ao.Name, (block, i));
                            Add(reads, ao.Name, (block, i)); // address taken: never elide
                        }
                    }
                }
            }

            foreach (var (name, ws) in writes)
            {
                if (ws.Count != 1 || !reads.TryGetValue(name, out var rs))
                {
                    continue;
                }

                var (wb, wi) = ws[0];
                if (wb.Statements[wi] is not IrAssign { Src: IrReg savedReg })
                {
                    continue;
                }

                if (!IsCalleeSaved(savedReg.Name))
                {
                    continue;
                }

                // Every read must be a plain restore into the same register.
                bool allRestores = rs.All(r => r.Block.Statements[r.Index] is IrAssign { Dst: IrReg d, Src: IrLocal } && d.Name == savedReg.Name);
                if (!allRestores)
                {
                    continue;
                }

                wb.Statements[wi] = new IrNop { Va = wb.Statements[wi].Va };
                foreach (var (rb, ri) in rs)
                {
                    rb.Statements[ri] = new IrNop { Va = rb.Statements[ri].Va };
                }

                _fn.Locals.Remove(name);
            }
        }

        private bool IsCalleeSaved(string reg)
        {
            string c = RegisterAliases.CanonicalOf(reg);
            return _fn.Bitness == 64
                ? c is "rbx" or "rbp" or "rdi" or "rsi" or "r12" or "r13" or "r14" or "r15"
                : c is "rbx" or "rbp" or "rdi" or "rsi";
        }

        private static void Add<T>(Dictionary<string, List<T>> d, string key, T value)
        {
            if (!d.TryGetValue(key, out var list))
            {
                d[key] = list = new List<T>();
            }

            list.Add(value);
        }

        public void RemoveNops()
        {
            foreach (var block in _fn.Blocks)
            {
                block.Statements.RemoveAll(s => s is IrNop);
            }
        }
    }
}
