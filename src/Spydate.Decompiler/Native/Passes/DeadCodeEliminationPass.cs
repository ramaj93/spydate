using Spydate.Decompiler.Native.IR;
using Spydate.Decompiler.Native.Structuring;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// Removes assignments to registers nothing reads again, using liveness over the whole function rather
/// than one block. Compilers leave a lot of these behind — a value spilled into a register the next block
/// overwrites, a call result nobody wants — and after propagation they are pure noise.
///
/// What is *not* removed matters more than what is. A call keeps its arguments alive even when the IR does
/// not name them, because argument recovery does not model every convention; anything with a side effect
/// (stores, calls, <c>__asm</c>) stays; a partial write (<c>al</c>) never kills the register it sits in;
/// and a block whose successors are unknown — an unresolved indirect jump — is treated as though every
/// register were live afterwards.
/// </summary>
public sealed class DeadCodeEliminationPass : IIrPass
{
    /// <summary>Marker for "everything is live", used where the CFG runs out of knowledge.</summary>
    private const string Everything = "*";

    public string Name => "dead-code";

    public void Run(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (function.Blocks.Count == 0)
        {
            return;
        }

        var cfg = Cfg.Build(function);
        var order = cfg.ReversePostOrder.Reverse().ToArray();

        // Removing one assignment can make the one that fed it dead in turn, so liveness is recomputed
        // until a round finds nothing.
        for (int sweep = 0; sweep < 3; sweep++)
        {
            var liveIn = new HashSet<string>[cfg.Count];
            for (int i = 0; i < cfg.Count; i++)
            {
                liveIn[i] = new HashSet<string>(StringComparer.Ordinal);
            }

            // Backward fixpoint: a block's live-in depends on its successors, so iterate until it settles.
            for (int round = 0; round < cfg.Count + 2; round++)
            {
                bool changed = false;
                foreach (int node in order)
                {
                    var live = LiveOut(cfg, node, liveIn);
                    Scan(cfg.Blocks[node], function.Bitness, live, remove: false);
                    if (!live.SetEquals(liveIn[node]))
                    {
                        liveIn[node] = live;
                        changed = true;
                    }
                }

                if (!changed)
                {
                    break;
                }
            }

            bool removed = false;
            foreach (int node in order)
            {
                removed |= Scan(cfg.Blocks[node], function.Bitness, LiveOut(cfg, node, liveIn), remove: true);
            }

            if (!removed)
            {
                break;
            }
        }
    }

    private static HashSet<string> LiveOut(Cfg cfg, int node, HashSet<string>[] liveIn)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        var block = cfg.Blocks[node];
        bool ends = block.Statements.Count > 0 && block.Statements[^1] is IrReturn;

        if (cfg.Successors[node].Length == 0 && !ends)
        {
            // A tail jump, an unresolved indirect jump, a call that does not return: whatever runs next
            // may read anything.
            live.Add(Everything);
            return live;
        }

        foreach (int successor in cfg.Successors[node])
        {
            live.UnionWith(liveIn[successor]);
        }

        return live;
    }

    /// <summary>
    /// Walks a block backwards, turning live-out into live-in. Without <paramref name="remove"/> this is
    /// plain liveness — dead statements still count as readers, which is what keeps the fixpoint sound.
    /// With it, the statements found dead are deleted and their reads no longer count.
    /// </summary>
    private static bool Scan(IrBlock block, int bitness, HashSet<string> live, bool remove)
    {
        bool removed = false;
        var statements = block.Statements;
        for (int i = statements.Count - 1; i >= 0; i--)
        {
            var statement = statements[i];

            if (remove
                && statement is IrAssign { Dst: IrReg or IrTemp } assign
                && !IrRewriter.ContainsCallOrUnknown(assign.Src)
                && FullyKills(assign.Dst, bitness)
                && !IsLive(live, assign.Dst))
            {
                statements.RemoveAt(i);
                removed = true;
                continue; // its inputs are not read after all
            }

            // A call whose result nobody wants is still a call.
            if (statement is IrCallStmt { Result: IrReg } call && FullyKills(call.Result, bitness) && !IsLive(live, call.Result))
            {
                if (remove)
                {
                    statements[i] = call with { Result = null };
                    removed = true;
                }

                statement = call with { Result = null };
            }

            if (IrRewriter.Destination(statement) is { } written)
            {
                if (FullyKills(written, bitness))
                {
                    live.Remove(Key(written));
                }
                else
                {
                    live.Add(Key(written)); // partial write: the rest of the register survives
                }
            }

            foreach (var read in IrRewriter.Reads(statement))
            {
                if (read is IrReg or IrTemp)
                {
                    live.Add(Key(read));
                }
            }

            // `ret` names the accumulator, but a float result goes back in xmm0 and nothing in the IR
            // says so.
            if (statement is IrReturn)
            {
                live.Add("zmm0");
            }

            // A call may be reading arguments out of registers the recovery never named, so those stay
            // live. Naming one is what settles it: an argument register the call already passes is live
            // anyway, and the rest are only safe to drop where the whole list is known - which on x86
            // means the callee was read and said so, and on x64 means nothing at all. The x64 count is a
            // lower bound, and a float slot the call site never wrote is exactly the case that needs it.
            if (statement is IrCallStmt call2 && !(bitness == 32 && call2.Call.ConventionKnown))
            {
                var named = call2.Call.Args.Select(Key).ToHashSet(StringComparer.Ordinal);
                foreach (string register in ArgumentRegisters(bitness))
                {
                    if (!named.Contains(register))
                    {
                        live.Add(register);
                    }
                }
            }
        }

        return removed;
    }

    /// <summary>Registers a call may take an argument in, whether or not the IR named them.</summary>
    private static IEnumerable<string> ArgumentRegisters(int bitness) => bitness == 64
        ? new[] { "rcx", "rdx", "r8", "r9", "zmm0", "zmm1", "zmm2", "zmm3" }
        : new[] { "rcx", "rdx" };

    /// <summary>True when writing this replaces the whole register, so earlier values of it are dead.</summary>
    private static bool FullyKills(IrExpr written, int bitness) => written switch
    {
        IrTemp => true,
        // On x64 a 32-bit write zero-extends, which kills the 64-bit register too.
        IrReg r => r.Bits >= bitness || (bitness == 64 && r.Bits == 32),
        _ => false,
    };

    private static bool IsLive(HashSet<string> live, IrExpr variable)
        => live.Contains(Everything) || live.Contains(Key(variable));

    private static string Key(IrExpr variable) => variable switch
    {
        IrTemp t => $"t{t.Id}",
        IrReg r => RegisterAliases.CanonicalOf(r.Name),
        _ => "?",
    };
}
