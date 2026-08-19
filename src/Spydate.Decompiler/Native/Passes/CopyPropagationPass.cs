using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// Forward substitution: when a register/temp/local is assigned an expression and later read before being
/// redefined (and none of the expression's inputs changed in between), the read is replaced by the
/// expression. Values that every predecessor agrees on reach a block from outside it (see
/// <see cref="ReachingValues"/>), so a register set in one block can be read as its value in the next. Cheap values (constants, registers, symbols) are forwarded to every reader; complex
/// expressions only when there is exactly one reader, to avoid duplicating work in the output.
/// Definitions whose readers were all replaced and that are redefined later in the block are removed;
/// a call's dead result register is dropped instead of removing the call.
/// </summary>
public sealed class CopyPropagationPass : IIrPass
{
    public string Name => "copy-propagation";

    public void Run(IrFunction function)
    {
        var incoming = ReachingValues.Compute(function);
        foreach (var block in function.Blocks)
        {
            var seeds = incoming.TryGetValue(block.StartVa, out var values) ? values : NoSeeds;

            // Dry run to learn how many readers each definition has and whether it dies inside the
            // block, then the real pass.
            var facts = Process(block, function.Bitness, dryRun: true, null, seeds);
            Process(block, function.Bitness, dryRun: false, facts, seeds);
        }
    }

    private static readonly List<(IrExpr Var, IrExpr Value)> NoSeeds = new();

    private sealed class Def
    {
        /// <summary>Index of the defining statement, or negative for a value that arrived from another block.</summary>
        public required int Index;
        public required IrExpr Var;
        public required IrExpr Value;
        public required bool ReadsMemory;
        public required bool IsCallResult;
        public int Reads;
        public int Substituted;
        public bool Valid = true;
        public bool Redefined;
    }

    private static Dictionary<int, Def> Process(IrBlock block, int bitness, bool dryRun, Dictionary<int, Def>? facts, List<(IrExpr Var, IrExpr Value)> seeds)
    {
        var stmts = block.Statements;
        var live = new List<Def>();
        var all = new List<Def>();

        // Values the block inherits. They have no defining statement here, so they are never removed.
        int seedIndex = 0;
        foreach (var (variable, value) in seeds)
        {
            var seed = new Def
            {
                Index = --seedIndex, Var = variable, Value = value, ReadsMemory = false, IsCallResult = false,
            };
            live.Add(seed);
            all.Add(seed);
        }

        for (int i = 0; i < stmts.Count; i++)
        {
            var stmt = stmts[i];

            // 1. Reads / substitution.
            var substituted = IrRewriter.RewriteStmt(stmt, e =>
            {
                if (e is not (IrReg or IrTemp or IrLocal))
                {
                    return e;
                }

                // Partial/aliased reads (al after a def of eax) count as readers but are never substituted.
                Def? def = null;
                foreach (var d in live)
                {
                    if (d.Valid && RegisterAliases.MayAlias(d.Var, e))
                    {
                        d.Reads++;
                        if (Same(d.Var, e))
                        {
                            def = d;
                        }
                    }
                }

                if (def is null)
                {
                    return e;
                }

                if (dryRun)
                {
                    return e;
                }

                var known = facts is not null && facts.TryGetValue(def.Index, out var f) ? f : null;
                int expected = known?.Reads ?? int.MaxValue;
                if (def.IsCallResult)
                {
                    // A call may only move into its single reader when that reader is the very next statement,
                    // so no side effect is reordered - and only when the result register is dead afterwards,
                    // or the call would be left behind as well as moved, and appear to happen twice.
                    bool diesInBlock = known is not null && (known.Redefined || def.Var is IrTemp);
                    if (stmts[def.Index] is IrCallStmt { Result: not null } cs && expected == 1 && i == def.Index + 1 && diesInBlock)
                    {
                        def.Substituted++;
                        return cs.Call;
                    }

                    return e;
                }

                bool cheap = def.Value is IrConst or IrReg or IrTemp or IrLocal or IrSymbol or IrStringLiteral or IrAddressOf;
                if (!cheap && expected != 1)
                {
                    return e;
                }

                def.Substituted++;
                return def.Value;
            }, includeDestinations: false);

            if (!dryRun && !ReferenceEquals(substituted, stmt))
            {
                stmts[i] = substituted;
                stmt = substituted;
            }

            // 2. Kills.
            var written = IrRewriter.Destination(stmt);
            // A write through a named global is still a memory write, so it must invalidate the same
            // definitions an IrStore would.
            bool storesMemory = stmt is IrStore or IrAssign { Dst: IrMem or IrGlobal };
            bool hasCall = stmt is IrCallStmt;
            foreach (var def in live)
            {
                if (!def.Valid)
                {
                    continue;
                }

                if (written is not null && RegisterAliases.Kills(written, def.Var, bitness))
                {
                    def.Valid = false;
                    def.Redefined = true;
                    continue;
                }

                if (hasCall && def.Var is IrReg r && IsCallerSaved(r.Name, bitness))
                {
                    // The call clobbers volatile registers: the definition is dead afterwards.
                    def.Valid = false;
                    def.Redefined = true;
                    continue;
                }

                if (written is not null && (RegisterAliases.MayAlias(written, def.Var) || ValueReads(def.Value, written)))
                {
                    def.Valid = false;
                    continue;
                }

                if ((storesMemory || hasCall) && def.ReadsMemory)
                {
                    def.Valid = false;
                    continue;
                }

                // A callee may read or write the caller's stack slots (pointer args, spilled args), so a local's
                // value can neither be forwarded past a call nor treated as dead because of it.
                if (hasCall && def.Var is IrLocal)
                {
                    def.Valid = false;
                }
            }

            // Nothing in a register lives past a return. Stack slots are left alone: they may have been
            // observed by callees, and removing them would hide argument setup. On x86, ecx/edx may have
            // carried fastcall/thiscall arguments to an earlier call, so they stay visible too.
            if (stmt is IrReturn)
            {
                foreach (var def in live)
                {
                    def.Valid = false;
                    bool possibleX86RegArg = bitness == 32 && def.Var is IrReg r32 && RegisterAliases.CanonicalOf(r32.Name) is "rcx" or "rdx";
                    if (def.Var is IrTemp || (def.Var is IrReg && !possibleX86RegArg))
                    {
                        def.Redefined = true;
                    }
                }
            }

            live.RemoveAll(d => !d.Valid);

            // 3. New definition.
            if (written is not null)
            {
                Def? def = stmt switch
                {
                    IrAssign a when IsPropagatable(a.Src) => new Def
                    {
                        Index = i, Var = written, Value = a.Src, ReadsMemory = IrRewriter.ContainsMemoryRead(a.Src), IsCallResult = false,
                    },
                    IrCallStmt => new Def
                    {
                        Index = i, Var = written, Value = new IrUnknown("call", written.Bits), ReadsMemory = false, IsCallResult = true,
                    },
                    IrAssign a => new Def
                    {
                        Index = i, Var = written, Value = a.Src, ReadsMemory = true, IsCallResult = true, // opaque: only kill-tracked
                    },
                    _ => null,
                };

                if (def is not null)
                {
                    live.Add(def);
                    all.Add(def);
                }
            }
        }

        if (dryRun)
        {
            return all.ToDictionary(d => d.Index);
        }

        // 4. Remove dead definitions.
        var toRemove = new HashSet<int>();
        var dropResult = new HashSet<int>();
        foreach (var def in all)
        {
            if (def.Index < 0)
            {
                continue; // defined in another block
            }

            bool allReadersReplaced = def.Reads == def.Substituted;
            bool deadAtEnd = def.Redefined || def.Var is IrTemp;
            if (!allReadersReplaced || !deadAtEnd)
            {
                continue;
            }

            if (stmts[def.Index] is IrCallStmt)
            {
                if (def.Reads == 0)
                {
                    dropResult.Add(def.Index);
                }
                else if (def.Substituted == def.Reads)
                {
                    toRemove.Add(def.Index); // the call moved into its reader
                }
            }
            else if (stmts[def.Index] is IrAssign { Src: not IrUnknown })
            {
                toRemove.Add(def.Index);
            }
        }

        for (int i = stmts.Count - 1; i >= 0; i--)
        {
            if (toRemove.Contains(i))
            {
                stmts.RemoveAt(i);
            }
            else if (dropResult.Contains(i) && stmts[i] is IrCallStmt c)
            {
                stmts[i] = c with { Result = null };
            }
        }

        return new Dictionary<int, Def>();
    }

    private static bool Same(IrExpr a, IrExpr b) => (a, b) switch
    {
        (IrReg x, IrReg y) => x.Name == y.Name && x.Bits == y.Bits,
        (IrTemp x, IrTemp y) => x.Id == y.Id,
        (IrLocal x, IrLocal y) => x.Name == y.Name && x.Bits == y.Bits,
        _ => false,
    };

    private static bool ValueReads(IrExpr value, IrExpr var)
        => IrRewriter.Descendants(value).Any(e => e is IrReg or IrTemp or IrLocal && RegisterAliases.MayAlias(e, var));

    private static bool IsPropagatable(IrExpr src) => !IrRewriter.ContainsCallOrUnknown(src);

    /// <summary>
    /// Registers a call may clobber. On x86 only eax is treated as clobbered: ecx/edx may carry
    /// fastcall/thiscall arguments that argument recovery does not model yet, so their values are kept visible.
    /// </summary>
    private static bool IsCallerSaved(string reg, int bitness)
    {
        string c = RegisterAliases.CanonicalOf(reg);
        return bitness == 64
            ? c is "rax" or "rcx" or "rdx" or "r8" or "r9" or "r10" or "r11" || c.StartsWith("zmm", StringComparison.Ordinal)
            : c is "rax";
    }
}
