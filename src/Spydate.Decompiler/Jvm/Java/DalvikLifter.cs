using Spydate.Core.Dex;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// Turns a method's Dalvik code into the same IR the <see cref="JvmLifter"/> makes of JVM bytecode, so everything
/// after it — locals, structuring, the shortcuts, the class writer — reads an APK's methods unchanged.
///
/// Dalvik is register-based: every instruction reads registers and writes one, so each becomes a statement on
/// locals, and the inliner folds the single-use ones back into expressions. What Dalvik does not say is a
/// register's type: <c>const 0</c> is an int, a false or a null, <c>const/high16</c> as often a float. So types are
/// inferred first: reaching definitions over the registers (protected code feeding its handlers); a definition's
/// type from its instruction (a field's, a method's return, an array's element); an untyped constant's from what the
/// instructions it reaches expect of it; a move's from its source.
///
/// Registers map onto JVM slots — the arguments, which Dalvik keeps in the last registers, first — so the
/// <see cref="LocalNamer"/> names them from the debug table as it names a class file's locals. <c>new-instance</c>
/// and the constructor call on it are one <c>new T(…)</c>; an invoke and the <c>move-result</c> after it one
/// assignment; <c>fill-array-data</c> after <c>new-array</c> an array initialiser. Bad code never throws: an
/// undecodable instruction ends its block with a comment and a warning.
/// </summary>
internal sealed class DalvikLifter
{
    private const string Object = "Ljava/lang/Object;";

    private readonly ClassFile _class;
    private readonly JvmMethod _method;
    private readonly DexCode _code;
    private readonly DexFile? _dex;
    private readonly IReadOnlyList<DalvikInstruction> _instructions;
    private readonly Dictionary<int, int> _at = new();
    private readonly LocalNamer _locals;
    private readonly IrFunction _function;
    private readonly SortedSet<int> _leaders = new();
    private readonly Dictionary<int, IrBlock> _blocks = new();
    private readonly Dictionary<ulong, int?[]> _switchValues = new();
    private readonly int _firstParameter;
    private readonly CodeAttribute _slots;

    // Type inference: every definition (an instruction writing a register, or an argument on entry) and its type;
    // every read and the definitions that reach it.
    private readonly List<(int Instruction, int Register, bool Wide)> _defs = [];
    private readonly Dictionary<(int Instruction, int Register), int> _defAt = new();
    private string?[] _defType = [];
    private readonly Dictionary<(int Instruction, int Register), List<int>> _reaching = new();

    /// <summary>Each definition's variable: definitions a read sees together are one (union-find roots).</summary>
    private int[] _web = [];

    /// <summary>The local each untabled variable is written as, by its root.</summary>
    private readonly Dictionary<int, JLocal> _webLocal = new();

    /// <summary>The reverse: for each definition, the reads it reaches.</summary>
    private readonly Dictionary<int, List<(int Instruction, int Register)>> _reaches = new();

    private IrBlock _block = null!;
    private int _pc;

    private DalvikLifter(ClassFile file, JvmMethod method, DexCode code, IReadOnlySet<string>? reserved)
    {
        _class = file;
        _method = method;
        _code = code;
        _dex = code.File;
        _instructions = Dalvik.Decode(code.Insns.Span);
        for (int i = 0; i < _instructions.Count; i++)
        {
            _at[_instructions[i].Address] = i;
        }

        _firstParameter = code.Registers - code.Ins;

        // The debug table's locals by JVM slot, so the namer finds them where it looks for a class file's.
        var converted = method.Code ?? new CodeAttribute(0, code.Registers, ReadOnlyMemory<byte>.Empty, [], [], []);
        _slots = converted with { Locals = converted.Locals.Select(l => l with { Slot = Slot(l.Slot) }).ToList() };
        _slots = _slots with { Handlers = WidenMonitors(_slots.Handlers) };
        _locals = new LocalNamer(file, method, _slots, reserved);
        _function = new IrFunction(0, method.Name, 32);
    }

    public static LiftedMethod Lift(ClassFile file, JvmMethod method, DexCode code, IReadOnlySet<string>? reserved = null)
    {
        var lifter = new DalvikLifter(file, method, code, reserved);
        lifter.Run();
        return new LiftedMethod
        {
            Function = lifter._function,
            Handlers = lifter.SeparateSharedHandlers(),
            SwitchValues = lifter._switchValues,
            Locals = lifter._locals,
        };
    }

    /// <summary>Where the stub blocks <see cref="SeparateSharedHandlers"/> makes start: past any method's code.</summary>
    private const ulong StubBase = 0x20000;

    /// <summary>
    /// D8 protects only what can throw, so a <c>synchronized</c> block's release handler covers from the first
    /// instruction that can throw, not from the <c>monitor-enter</c>. Protecting what cannot throw changes nothing, so
    /// the range is widened back to just after the <c>monitor-enter</c> of the same lock: the shape javac writes,
    /// which reads back as <c>synchronized</c>.
    /// </summary>
    private IReadOnlyList<ExceptionHandler> WidenMonitors(IReadOnlyList<ExceptionHandler> handlers)
    {
        // D8 may also run one range over the handler itself, which guards its own release as javac's separate entry
        // does: split there, so the body's part and the handler's are the two entries javac writes.
        var widened = new List<ExceptionHandler>();
        foreach (var h in handlers)
        {
            if (h.CatchType is null && h.HandlerPc > h.StartPc && h.HandlerPc < h.EndPc)
            {
                widened.Add(h with { EndPc = h.HandlerPc });
                widened.Add(h with { StartPc = h.HandlerPc });
            }
            else
            {
                widened.Add(h);
            }
        }

        foreach (int handler in handlers.Where(h => h.CatchType is null).Select(h => h.HandlerPc).Distinct())
        {
            // move-exception vE; monitor-exit vL; throw vE
            if (!_at.TryGetValue(handler, out int k) || k + 2 >= _instructions.Count
                || _instructions[k] is not { Opcode: 0x0D } caught || _instructions[k + 1] is not { Opcode: 0x1E } release
                || _instructions[k + 2] is not { Opcode: 0x27 } rethrow || rethrow.A != caught.A)
            {
                continue;
            }

            var entries = widened.Select((h, index) => (h, index)).Where(p => p.h.HandlerPc == handler && !(handler >= p.h.StartPc && handler < p.h.EndPc)).ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            var first = entries.MinBy(p => p.h.StartPc);
            var enter = _instructions.LastOrDefault(i => i.Opcode == 0x1D && i.A == release.A && i.Address < first.h.StartPc);
            if (enter is not null)
            {
                widened[first.index] = first.h with { StartPc = enter.Address + enter.Units };
            }
        }

        return widened;
    }

    /// <summary>
    /// A handler D8 left without a <c>move-exception</c> can start at code the normal path reaches too — the
    /// latch of the loop the try is in — which no Java catch clause can be. The handler then gets a block of its
    /// own that only goes there: an empty catch, rejoining where the code does.
    /// </summary>
    private IReadOnlyList<ExceptionHandler> SeparateSharedHandlers()
    {
        var normal = _function.Blocks.SelectMany(b => b.Successors).ToHashSet();
        var stubs = new Dictionary<int, ulong>();
        foreach (int handler in _slots.Handlers.Select(h => h.HandlerPc).Distinct())
        {
            if (!normal.Contains((ulong)handler) || !_blocks.TryGetValue(handler, out var shared))
            {
                continue;
            }

            ulong va = StubBase + (ulong)stubs.Count;
            var stub = new IrBlock(va);
            stub.Statements.Add(new IrGoto((ulong)handler) { Va = va });
            stub.Successors.Add((ulong)handler);
            shared.Predecessors.Add(va);
            _function.Blocks.Add(stub);
            stubs[handler] = va;
        }

        return stubs.Count == 0
            ? _slots.Handlers
            : _slots.Handlers.Select(h => stubs.TryGetValue(h.HandlerPc, out ulong va) ? h with { HandlerPc = (int)va } : h).ToList();
    }

    /// <summary>Dalvik's arguments are its last registers; the JVM's its first slots.</summary>
    private int Slot(int register) => register >= _firstParameter ? register - _firstParameter : register + _code.Ins;

    private void Run()
    {
        FindLeaders();
        foreach (int leader in _leaders)
        {
            _blocks[leader] = new IrBlock((ulong)leader);
            _function.Blocks.Add(_blocks[leader]);
        }

        InferTypes();
        foreach (int leader in _leaders)
        {
            LiftBlock(leader);
        }

        foreach (var block in _function.Blocks)
        {
            foreach (ulong succ in block.Successors)
            {
                if (_blocks.TryGetValue((int)succ, out var target))
                {
                    target.Predecessors.Add(block.StartVa);
                }
            }
        }
    }

    // --- blocks -----------------------------------------------------------------------------------

    private void FindLeaders()
    {
        _leaders.Add(0);
        foreach (var i in _instructions)
        {
            foreach (int target in Targets(i))
            {
                AddLeader(target);
            }

            if (EndsBlock(i) || i.Problem is not null)
            {
                AddLeader(i.Address + i.Units);
            }
        }

        foreach (var handler in _slots.Handlers)
        {
            AddLeader(handler.StartPc);
            AddLeader(handler.EndPc);
            AddLeader(handler.HandlerPc);
        }
    }

    private void AddLeader(int address)
    {
        if (_at.ContainsKey(address))
        {
            _leaders.Add(address);
        }
    }

    private static IEnumerable<int> Targets(DalvikInstruction i) => i.Opcode switch
    {
        >= 0x28 and <= 0x2A => [i.Target],
        >= 0x32 and <= 0x3D => [i.Target],
        0x2B or 0x2C => i.Cases?.Select(c => c.Target) ?? [],
        _ => [],
    };

    private static bool EndsBlock(DalvikInstruction i) => i.Opcode is (>= 0x0E and <= 0x11) or 0x27 or (>= 0x28 and <= 0x2C) or (>= 0x32 and <= 0x3D);

    private static bool Falls(DalvikInstruction i) => i.Opcode is not ((>= 0x0E and <= 0x11) or 0x27 or (>= 0x28 and <= 0x2A));

    /// <summary>The instructions of the block starting at an address, up to the next leader.</summary>
    private IEnumerable<int> BlockInstructions(int leader)
    {
        int end = _leaders.GetViewBetween(leader + 1, int.MaxValue).DefaultIfEmpty(int.MaxValue).First();
        for (int k = _at[leader]; k < _instructions.Count && _instructions[k].Address < end; k++)
        {
            yield return k;
        }
    }

    private IEnumerable<int> Successors(int leader)
    {
        int last = BlockInstructions(leader).DefaultIfEmpty(-1).Last();
        if (last < 0)
        {
            yield break;
        }

        var i = _instructions[last];
        foreach (int target in Targets(i))
        {
            yield return target;
        }

        if (Falls(i) && i.Problem is null && _at.ContainsKey(i.Address + i.Units))
        {
            yield return i.Address + i.Units;
        }
    }

    // --- type inference -------------------------------------------------------------------------------

    /// <summary>What an instruction reads: each register, whether it is a pair, and the type the instruction expects of it.</summary>
    private List<(int Register, bool Wide, string? Expect)> Uses(int k)
    {
        var i = _instructions[k];
        var uses = new List<(int, bool, string?)>();
        void Use(int register, string? expect) => uses.Add((register, expect is "J" or "D", expect));
        switch (i.Opcode)
        {
            case >= 0x01 and <= 0x03: Use(i.B, null); break;
            case >= 0x04 and <= 0x06: uses.Add((i.B, true, null)); break;
            case >= 0x07 and <= 0x09: Use(i.B, Object); break;
            case 0x0F or 0x10 or 0x11: Use(i.A, JCall.ReturnType(_method.Descriptor)); break;
            case 0x1D or 0x1E or 0x27: Use(i.A, Object); break;
            case 0x1F: Use(i.A, Object); break;
            case 0x20: Use(i.B, Object); break;
            case 0x21: Use(i.B, Object); break;
            case 0x23: Use(i.B, "I"); break;
            case 0x24 or 0x25:
            {
                string element = TypeAt(i.Index) is ['[', .. var e] ? e : "I";
                foreach (int r in i.Registers)
                {
                    Use(r, element);
                }

                break;
            }

            case 0x26: Use(i.A, Object); break;
            case 0x2B or 0x2C: Use(i.A, "I"); break;
            case 0x2D or 0x2E: Use(i.B, "F"); Use(i.C, "F"); break;
            case 0x2F or 0x30: Use(i.B, "D"); Use(i.C, "D"); break;
            case 0x31: Use(i.B, "J"); Use(i.C, "J"); break;
            case >= 0x32 and <= 0x37: Use(i.A, null); Use(i.B, null); break;
            case >= 0x38 and <= 0x3D: Use(i.A, null); break;
            case >= 0x44 and <= 0x4A: Use(i.B, Object); Use(i.C, "I"); break;
            case >= 0x4B and <= 0x51:
                Use(i.A, ArrayOpType(i.Opcode - 0x4B, null));
                Use(i.B, Object);
                Use(i.C, "I");
                break;
            case >= 0x52 and <= 0x58: Use(i.B, Object); break;
            case >= 0x59 and <= 0x5F: Use(i.A, FieldAt(i.Index)?.Type); Use(i.B, Object); break;
            case >= 0x67 and <= 0x6D: Use(i.A, FieldAt(i.Index)?.Type); break;
            case (>= 0x6E and <= 0x72) or (>= 0x74 and <= 0x78) or 0xFA or 0xFB or 0xFC or 0xFD:
                foreach (var (register, type) in Arguments(i))
                {
                    Use(register, type);
                }

                break;
            case >= 0x7B and <= 0x80:
            {
                string type = (i.Opcode - 0x7B) switch { 0 or 1 => "I", 2 or 3 => "J", 4 => "F", _ => "D" };
                Use(i.B, type);
                break;
            }

            case >= 0x81 and <= 0x8F: Use(i.B, ConversionSource(i.Opcode)); break;
            case >= 0x90 and <= 0xAF:
            {
                var (left, right, _) = BinaryTypes(i.Opcode - 0x90);
                Use(i.B, left);
                Use(i.C, right);
                break;
            }

            case >= 0xB0 and <= 0xCF:
            {
                var (left, right, _) = BinaryTypes(i.Opcode - 0xB0);
                Use(i.A, left);
                Use(i.B, right);
                break;
            }

            case >= 0xD0 and <= 0xE2: Use(i.B, "I"); break;
        }

        return uses;
    }

    /// <summary>What an instruction writes: its register, whether it is a pair, and the type, when the instruction says it.</summary>
    private (int Register, bool Wide, string? Type)? Def(int k)
    {
        var i = _instructions[k];
        return i.Opcode switch
        {
            >= 0x01 and <= 0x03 => (i.A, false, null),
            >= 0x04 and <= 0x06 => (i.A, true, null),
            >= 0x07 and <= 0x09 => (i.A, false, null),
            0x0A or 0x0B or 0x0C => (i.A, i.Opcode == 0x0B, ResultType(k)),
            0x0D => (i.A, false, CaughtType(i.Address)),
            >= 0x12 and <= 0x15 => (i.A, false, null),
            >= 0x16 and <= 0x19 => (i.A, true, null),
            0x1A or 0x1B => (i.A, false, "Ljava/lang/String;"),
            0x1C => (i.A, false, "Ljava/lang/Class;"),
            0x1F => (i.A, false, TypeAt(i.Index) ?? Object),
            0x20 => (i.A, false, "Z"),
            0x21 => (i.A, false, "I"),
            0x22 or 0x23 => (i.A, false, TypeAt(i.Index) ?? Object),
            >= 0x2D and <= 0x31 => (i.A, false, "I"),
            >= 0x44 and <= 0x4A => (i.A, i.Opcode == 0x45, null),
            >= 0x52 and <= 0x58 => (i.A, i.Opcode == 0x53, FieldAt(i.Index)?.Type),
            >= 0x60 and <= 0x66 => (i.A, i.Opcode == 0x61, FieldAt(i.Index)?.Type),
            >= 0x7B and <= 0x80 => (i.A, i.Opcode is 0x7D or 0x7E or 0x80, (i.Opcode - 0x7B) switch { 0 or 1 => "I", 2 or 3 => "J", 4 => "F", _ => "D" }),
            >= 0x81 and <= 0x8F => (i.A, ConversionTarget(i.Opcode) is "J" or "D", ConversionTarget(i.Opcode)),
            >= 0x90 and <= 0xAF => (i.A, BinaryTypes(i.Opcode - 0x90).Result is "J" or "D", BinaryTypes(i.Opcode - 0x90).Result),
            >= 0xB0 and <= 0xCF => (i.A, BinaryTypes(i.Opcode - 0xB0).Result is "J" or "D", BinaryTypes(i.Opcode - 0xB0).Result),
            >= 0xD0 and <= 0xE2 => (i.A, false, "I"),
            0xFE => (i.A, false, "Ljava/lang/invoke/MethodHandle;"),
            0xFF => (i.A, false, "Ljava/lang/invoke/MethodType;"),
            _ => null,
        };
    }

    private void InferTypes()
    {
        // The arguments are defined on entry, with the types the descriptor gives them.
        var argumentDefs = new Dictionary<int, int>();
        int register = _firstParameter;
        if ((_method.Access & JvmAccess.Static) == 0)
        {
            argumentDefs[register] = AddDef(-1, register, false, $"L{_class.Name};");
            register++;
        }

        foreach (string parameter in Descriptors.ParameterDescriptors(_method.Descriptor))
        {
            bool wide = parameter is "J" or "D";
            argumentDefs[register] = AddDef(-1, register, wide, parameter);
            register += wide ? 2 : 1;
        }

        for (int k = 0; k < _instructions.Count; k++)
        {
            if (Def(k) is { } def)
            {
                AddDef(k, def.Register, def.Wide, def.Type);
            }
        }

        // Reaching definitions per block, as bitsets over the definitions: what a block generates, the definitions
        // of every register it writes (which it kills), and every definition in it (which its handlers can see).
        var leaders = _leaders.ToList();
        var index = leaders.Select((l, b) => (l, b)).ToDictionary(p => p.l, p => p.b);
        int count = _defs.Count;
        int words = (count + 63) / 64;
        var into = new ulong[leaders.Count * words];
        var gen = new ulong[leaders.Count * words];
        var kill = new ulong[leaders.Count * words];
        var inside = new ulong[leaders.Count * words];
        var byRegister = _defs.Select((d, id) => (d, id)).GroupBy(p => p.d.Register).ToDictionary(g => g.Key, g => g.Select(p => p.id).ToList());
        for (int b = 0; b < leaders.Count; b++)
        {
            var last = new Dictionary<int, int>();
            var written = new HashSet<int>();
            var block = BlockInstructions(leaders[b]).ToList();
            for (int position = 0; position < block.Count; position++)
            {
                int k = block[position];
                if (_defAt.TryGetValue((k, DefRegister(k)), out int id))
                {
                    // A handler sees a store only when something after it in the block can throw.
                    if (block.Skip(position + 1).Any(later => CanThrow(_instructions[later])))
                    {
                        inside[(b * words) + (id / 64)] |= 1UL << (id % 64);
                    }

                    last[_defs[id].Register] = id;
                    written.Add(_defs[id].Register);
                    if (_defs[id].Wide)
                    {
                        last.Remove(_defs[id].Register + 1);
                        written.Add(_defs[id].Register + 1);
                    }
                }
            }

            foreach (int id in last.Values)
            {
                gen[(b * words) + (id / 64)] |= 1UL << (id % 64);
            }

            foreach (int writtenRegister in written)
            {
                foreach (int id in byRegister.GetValueOrDefault(writtenRegister) ?? [])
                {
                    kill[(b * words) + (id / 64)] |= 1UL << (id % 64);
                }
            }
        }

        foreach (int id in argumentDefs.Values)
        {
            into[id / 64] |= 1UL << (id % 64);
        }

        var canThrow = leaders.Select(l => BlockInstructions(l).Any(k => CanThrow(_instructions[k]))).ToArray();
        var handlers = new List<int>[leaders.Count];
        for (int b = 0; b < leaders.Count; b++)
        {
            handlers[b] = _slots.Handlers.Where(h => leaders[b] >= h.StartPc && leaders[b] < h.EndPc && index.ContainsKey(h.HandlerPc)).Select(h => index[h.HandlerPc]).Distinct().ToList();
        }

        var work = new Queue<int>(Enumerable.Range(0, leaders.Count));
        var queued = Enumerable.Repeat(true, leaders.Count).ToArray();
        var outgoing = new ulong[words];
        var seen = new ulong[words];
        while (work.Count > 0)
        {
            int b = work.Dequeue();
            queued[b] = false;
            for (int w = 0; w < words; w++)
            {
                int at = (b * words) + w;
                outgoing[w] = (into[at] & ~kill[at]) | gen[at];
                seen[w] = (canThrow[b] ? into[at] : 0) | inside[at];
            }

            foreach (int next in Successors(leaders[b]).Where(index.ContainsKey).Select(t => index[t]))
            {
                if (Grow(into, next * words, outgoing, words) && !queued[next])
                {
                    work.Enqueue(next);
                    queued[next] = true;
                }
            }

            // Anything in a protected block can throw: its handlers see what came in and everything it stored.
            foreach (int handler in handlers[b])
            {
                if (Grow(into, handler * words, seen, words) && !queued[handler])
                {
                    work.Enqueue(handler);
                    queued[handler] = true;
                }
            }
        }

        // Each read, with the definitions that reach it.
        for (int b = 0; b < leaders.Count; b++)
        {
            var current = new Dictionary<int, List<int>>();
            for (int id = 0; id < count; id++)
            {
                if ((into[(b * words) + (id / 64)] & (1UL << (id % 64))) != 0)
                {
                    (current.TryGetValue(_defs[id].Register, out var list) ? list : current[_defs[id].Register] = []).Add(id);
                }
            }

            foreach (int k in BlockInstructions(leaders[b]))
            {
                foreach (var use in Uses(k))
                {
                    _reaching[(k, use.Register)] = current.TryGetValue(use.Register, out var reaching) ? [.. reaching] : [];
                }

                if (_defAt.TryGetValue((k, DefRegister(k)), out int id))
                {
                    current[_defs[id].Register] = [id];
                    if (_defs[id].Wide)
                    {
                        current.Remove(_defs[id].Register + 1);
                    }
                }
            }
        }

        SolveTypes();
        BuildWebs();
    }

    /// <summary>
    /// The variables: the definitions a read sees together are one variable, as the JVM side splits slots into
    /// webs — but here with every definition's type known, so each variable is declared as what it holds.
    /// </summary>
    private void BuildWebs()
    {
        _web = Enumerable.Range(0, _defs.Count).ToArray();
        int Find(int x)
        {
            while (_web[x] != x)
            {
                _web[x] = _web[_web[x]];
                x = _web[x];
            }

            return x;
        }

        foreach (var defs in _reaching.Values)
        {
            for (int n = 1; n < defs.Count; n++)
            {
                _web[Find(defs[n])] = Find(defs[0]);
            }
        }

        for (int d = 0; d < _defs.Count; d++)
        {
            _web[d] = Find(d);
        }
    }

    /// <summary>
    /// The local a variable is written as, when it is one of the lifter's own: not an argument (the parameter),
    /// not one the debug table names anywhere (the table's name). Null for those, which the namer resolves.
    /// </summary>
    private JLocal? WebLocal(int definition, int register)
    {
        int root = _web[definition];
        if (_webLocal.TryGetValue(root, out var known))
        {
            return known;
        }

        var members = Enumerable.Range(0, _defs.Count).Where(d => _web[d] == root).ToList();
        if (members.Any(d => _defs[d].Instruction < 0))
        {
            return null;
        }

        // Named by the table at any of its stores or reads: the table's variable.
        foreach (int d in members)
        {
            var i = _instructions[_defs[d].Instruction];
            if (Named(register, i.Address + i.Units) || NamedRead(d, register) is not null)
            {
                return null;
            }
        }

        var types = members.Select(d => _defType[d]).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var constants = members.Where(d => _instructions[_defs[d].Instruction].Opcode is >= 0x12 and <= 0x19).ToHashSet();
        var typed = members.Where(d => !constants.Contains(d)).Select(d => _defType[d]).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        string type = typed.Count == 1 ? typed[0]
            : typed.Count == 0 ? types.FirstOrDefault() ?? "I"
            : typed.All(t => t[0] is 'L' or '[') ? "Ljava/lang/Object;"
            : typed.Contains("J") ? "J" : typed.Contains("D") ? "D" : typed.Contains("F") ? "F" : "I";
        var local = _locals.Untabled($"var{Slot(register)}", type);
        _webLocal[root] = local;
        return local;
    }

    private int DefRegister(int k) => Def(k)?.Register ?? -1;

    /// <summary>Whether an instruction can throw: what a handler can be reached from.</summary>
    private static bool CanThrow(DalvikInstruction i) => i.Opcode switch
    {
        0x1B or 0x1C or 0x1D or 0x1E or 0x1F or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 => true,
        >= 0x44 and <= 0x6D => true,
        (>= 0x6E and <= 0x72) or (>= 0x74 and <= 0x78) or (>= 0xFA and <= 0xFF) => true,
        0x93 or 0x94 or 0x9E or 0x9F or 0xB3 or 0xB4 or 0xBE or 0xBF or 0xD3 or 0xD4 or 0xDB or 0xDC => true,
        _ => i.Problem is not null,
    };

    private int AddDef(int instruction, int register, bool wide, string? type)
    {
        int id = _defs.Count;
        _defs.Add((instruction, register, wide));
        Array.Resize(ref _defType, id + 1);
        _defType[id] = type;
        _defAt[(instruction, register)] = id;
        return id;
    }

    private static bool Grow(ulong[] target, int at, ulong[] source, int words)
    {
        bool grew = false;
        for (int w = 0; w < words; w++)
        {
            ulong before = target[at + w];
            target[at + w] |= source[w];
            grew |= target[at + w] != before;
        }

        return grew;
    }

    /// <summary>
    /// Untyped definitions — constants, moves, array reads of an unknown array — take their type from their sources
    /// and from what the reads they reach expect, until nothing changes.
    /// </summary>
    private void SolveTypes()
    {
        // Which reads each definition reaches.
        var reaches = _reaches;
        foreach (var (read, defs) in _reaching)
        {
            foreach (int d in defs)
            {
                (reaches.TryGetValue(d, out var list) ? list : reaches[d] = []).Add(read);
            }
        }

        var expectations = new Dictionary<(int, int), string?>();
        for (int k = 0; k < _instructions.Count; k++)
        {
            foreach (var use in Uses(k))
            {
                expectations[(k, use.Register)] = use.Expect;
            }
        }

        for (int round = 0; round < 8; round++)
        {
            bool changed = false;
            for (int d = 0; d < _defs.Count; d++)
            {
                if (_defType[d] is not null || _defs[d].Instruction < 0)
                {
                    continue;
                }

                int k = _defs[d].Instruction;
                var i = _instructions[k];
                string? type = null;

                // A move is its source; an array read is its array's element.
                if (i.Opcode is >= 0x01 and <= 0x09)
                {
                    type = KnownType(k, i.B);
                }
                else if (i.Opcode is >= 0x44 and <= 0x4A)
                {
                    type = KnownType(k, i.B) is ['[', .. var element] ? element : i.Opcode switch { 0x45 => null, 0x46 => Object, 0x47 => "Z", 0x48 => "B", 0x49 => "C", 0x4A => "S", _ => null };
                }

                // Otherwise, and for constants, what the reads it reaches expect of it.
                if (type is null)
                {
                    var wanted = (reaches.GetValueOrDefault(d) ?? []).Select(r => Expected(r, expectations)).OfType<string>().ToList();
                    type = Choose(wanted, _defs[d].Wide);
                }

                if (type is not null)
                {
                    _defType[d] = type;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        for (int d = 0; d < _defs.Count; d++)
        {
            _defType[d] ??= _defs[d].Wide ? "J" : "I";
        }
    }

    /// <summary>What a read expects: its instruction's expectation, or, for a move, what its own definition turned out to be.</summary>
    private string? Expected((int Instruction, int Register) read, Dictionary<(int, int), string?> expectations)
    {
        if (expectations.GetValueOrDefault(read) is { } expected)
        {
            return expected;
        }

        var i = _instructions[read.Instruction];
        if (i.Opcode is >= 0x01 and <= 0x09 && _defAt.TryGetValue((read.Instruction, i.A), out int moved))
        {
            return _defType[moved];
        }

        // Compared with another value: the other one's type.
        if (i.Opcode is >= 0x32 and <= 0x37)
        {
            int other = read.Register == i.A ? i.B : i.A;
            return KnownType(read.Instruction, other);
        }

        return null;
    }

    private static string? Choose(List<string> wanted, bool wide)
    {
        if (wanted.Count == 0)
        {
            return null;
        }

        if (wide)
        {
            return wanted.Contains("D") ? "D" : "J";
        }

        if (wanted.FirstOrDefault(w => w[0] is 'L' or '[') is { } reference)
        {
            return reference;
        }

        // A boolean only when every read wants one — an int is never a boolean in Java — while a char, byte or short
        // read as an int too widens to it.
        if (wanted.All(w => w == "Z"))
        {
            return "Z";
        }

        foreach (string t in new[] { "F", "C", "B", "S" })
        {
            if (wanted.Contains(t) && wanted.All(w => w == t || (t != "F" && w == "I")))
            {
                return t;
            }
        }

        return wanted.Contains("F") && !wanted.Contains("I") ? "F" : "I";
    }

    /// <summary>The type of what a register holds as an instruction reads it: the one type every definition reaching it agrees on, or the first known.</summary>
    private string? KnownType(int instruction, int register)
    {
        if (!_reaching.TryGetValue((instruction, register), out var defs) || defs.Count == 0)
        {
            return null;
        }

        var types = defs.Select(d => _defType[d]).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return types.Count switch
        {
            0 => null,
            1 => types[0],
            _ => types.FirstOrDefault(t => t[0] is 'L' or '[' && t != Object) ?? types[0],
        };
    }

    // --- lifting -------------------------------------------------------------------------------------

    private void LiftBlock(int leader)
    {
        _block = _blocks[leader];
        var instructions = BlockInstructions(leader).ToList();
        bool ended = false;
        foreach (int k in instructions)
        {
            var i = _instructions[k];
            _pc = i.Address;
            if (i.Problem is not null && i.Cases is null && i.ArrayData is null)
            {
                Emit(new IrComment($"Dalvik code at {i.Address:X4} could not be decoded: {i.Problem}"));
                _function.Warnings.Add($"undecodable Dalvik code at {i.Address:X4}");
                ended = true;
                break;
            }

            if (Lift(k))
            {
                ended = true;
                break;
            }
        }

        if (!ended && instructions.Count > 0)
        {
            var last = _instructions[instructions[^1]];
            int next = last.Address + last.Units;
            if (_at.ContainsKey(next))
            {
                _block.Successors.Add((ulong)next);
            }
        }
    }

    private void Emit(IrStmt statement) => _block.Statements.Add(statement with { Va = (ulong)_pc });

    /// <summary>
    /// A register as an instruction at <paramref name="k"/> reads it. A register only a constant reaches is that
    /// constant, typed for this read: D8 keeps one <c>const/4 v1, 1</c> for every 1 a method uses — an int here, a
    /// true there — which no single variable could hold. Unless the debug table names the register a variable there:
    /// then the source had one.
    /// </summary>
    private JExpr Read(int k, int register, string? expect = null)
    {
        int address = _instructions[k].Address;
        if (_reaching.TryGetValue((k, register), out var defs) && SameConstant(defs) is { } at && !Named(register, address))
        {
            var constant = _instructions[at];
            if (constant.Opcode is 0x1A or 0x1B)
            {
                return new JConst(StringAt(constant.Index), "Ljava/lang/String;");
            }

            if (constant.Opcode == 0x1C)
            {
                return new JConst(new ClassLiteral(TypeAt(constant.Index) ?? Object), "Ljava/lang/Class;");
            }

            string type = expect ?? _defType[defs[0]] ?? "I";
            if (type is ['L' or '['] || (type[0] is 'L' or '[' && constant.Literal != 0))
            {
                type = _defType[defs[0]] ?? "I";
            }

            return Literal(constant, type);
        }

        if (defs is { Count: > 0 } && WebLocal(defs[0], register) is { } variable)
        {
            return variable;
        }

        string known = KnownType(k, register) ?? expect ?? "I";
        return _locals.Load(Slot(register), Kind(known), address);
    }

    /// <summary>
    /// The constant instruction every definition reaching a read is, all with one value — through moves, since D8
    /// copies constants between registers too; null otherwise.
    /// </summary>
    private int? SameConstant(List<int> defs, int depth = 0)
    {
        if (defs.Count == 0 || depth > 8)
        {
            return null;
        }

        int? found = null;
        foreach (int d in defs)
        {
            int k = _defs[d].Instruction;
            if (k < 0)
            {
                return null;
            }

            var i = _instructions[k];
            int? constant = i.Opcode switch
            {
                >= 0x12 and <= 0x19 or 0x1A or 0x1B or 0x1C => k,
                >= 0x01 and <= 0x09 when _reaching.TryGetValue((k, i.B), out var source) => SameConstant(source, depth + 1),
                _ => null,
            };
            if (constant is not { } c)
            {
                return null;
            }

            if (found is { } f && !SameValue(_instructions[f], _instructions[c]))
            {
                return null;
            }

            found ??= c;
        }

        return found;
    }

    /// <summary>
    /// Whether a constant's (or a copy of one's) every read takes the constant itself: then nothing reads the
    /// register, and the store would only stand between other definitions and the statement they fold into. A
    /// constant the debug table names stays: the source declared it.
    /// </summary>
    private bool Folded(int k, int register)
    {
        if (!_defAt.TryGetValue((k, register), out int d))
        {
            return false;
        }

        var i = _instructions[k];
        if (Named(register, i.Address + i.Units) || NamedRead(d, register) is not null)
        {
            return false;
        }

        return (_reaches.GetValueOrDefault(d) ?? []).All(read =>
            _reaching.TryGetValue(read, out var defs) && SameConstant(defs) is not null && !Named(read.Register, _instructions[read.Instruction].Address));
    }

    /// <summary>Whether two constant instructions load the same value: the same number of the same width, the same string or class.</summary>
    private bool SameValue(DalvikInstruction a, DalvikInstruction b) => (a.Opcode, b.Opcode) switch
    {
        (0x1A or 0x1B, 0x1A or 0x1B) => StringAt(a.Index) == StringAt(b.Index),
        (0x1C, 0x1C) => a.Index == b.Index,
        (>= 0x12 and <= 0x19, >= 0x12 and <= 0x19) => a.Literal == b.Literal && (a.Opcode >= 0x16) == (b.Opcode >= 0x16),
        _ => false,
    };

    /// <summary>Whether the debug table names a register a variable at an address.</summary>
    private bool Named(int register, int address)
    {
        int slot = Slot(register);
        return _slots.Locals.Any(l => l.Slot == slot && address >= l.StartPc && address <= l.StartPc + l.Length);
    }

    /// <summary>A constant instruction's value as a constant of a type.</summary>
    private static JConst Literal(DalvikInstruction i, string type)
    {
        bool wide = i.Opcode >= 0x16;
        return type switch
        {
            "F" when !wide => new JConst(BitConverter.Int32BitsToSingle((int)i.Literal), "F"),
            "D" when wide => new JConst(BitConverter.Int64BitsToDouble(i.Literal), "D"),
            "J" => new JConst(i.Literal, "J"),
            ['L' or '[', ..] when i.Literal == 0 => new JConst(null, type),
            "Z" or "B" or "C" or "S" when !wide => new JConst((int)i.Literal, type),
            _ => wide ? new JConst(i.Literal, "J") : JConst.Int((int)i.Literal),
        };
    }

    /// <param name="definedAt">The instruction whose definition this store is, when not <paramref name="k"/>'s own: a
    /// <c>new</c> is stored where its constructor runs, but defined by its <c>new-instance</c>.</param>
    private void Write(int k, int register, JExpr value, int? definedAt = null)
    {
        var i = _instructions[k];
        bool defined = _defAt.TryGetValue((definedAt ?? k, register), out int d);
        string type = defined && _defType[d] is { } known ? known : value.Type ?? "I";
        int liveFrom = i.Address + i.Units;

        // D8's debug table can leave a store out of its variable's range — on a path that joins where the variable
        // is live again. The store is still that variable when a read it reaches is named: it takes that name.
        if (!Named(register, liveFrom) && defined && NamedRead(d, register) is { } named)
        {
            liveFrom = named;
        }

        var local = defined && WebLocal(d, register) is { } variable ? variable : _locals.Store(Slot(register), Kind(type), liveFrom, value.Type);

        // A register given a value of another type made from what it held — x = x.getValue(), x = (Integer) x — is a
        // new variable, but a store reading its own target reads as an update of one. Through a temporary it is not
        // one, and the temporary folds back once the variables are told apart.
        if (type[0] is 'L' or '[' && KnownType(k, register) is { } before && before != type
            && JavaRewrite.PostOrder(value).OfType<JLocal>().Any(l => l.Name == local.Name))
        {
            var temp = new JLocal($"t{++_temps}", value.Type, JLocalKind.Temp);
            _locals.Declare(temp);
            Emit(new IrAssign(temp, value));
            value = temp;
        }

        Emit(new IrAssign(local, Coerce(value, local.Type)));
    }

    private int _temps;

    /// <summary>Where the debug table's variable starts that names the register at a read a definition reaches, if any does.</summary>
    private int? NamedRead(int definition, int register)
    {
        int slot = Slot(register);
        foreach (var (instruction, read) in _reaches.GetValueOrDefault(definition) ?? [])
        {
            if (read != register)
            {
                continue;
            }

            int address = _instructions[instruction].Address;
            if (_slots.Locals.FirstOrDefault(l => l.Slot == slot && address >= l.StartPc && address <= l.StartPc + l.Length) is { } local)
            {
                return local.StartPc;
            }
        }

        return null;
    }

    private static string Kind(string type) => type[0] is 'L' or '[' ? type : type;

    private static JExpr Coerce(JExpr value, string? type) => value is JConst constant ? constant.As(type) : value;

    /// <summary>Lifts one instruction; true when it ends the block.</summary>
    private bool Lift(int k)
    {
        var i = _instructions[k];
        byte op = i.Opcode;
        switch (op)
        {
            case 0x00:
                return false;
            case >= 0x01 and <= 0x09:
                if (!Folded(k, i.A))
                {
                    Write(k, i.A, Read(k, i.B));
                }

                return false;
            case >= 0x0A and <= 0x0C:
                // Taken with the invoke or filled-new-array before it.
                return false;
            case 0x0D:
                // An exception nothing reads is a catch without a variable of its own, whatever local the register
                // held before: storing it there would write the catch as an assignment to that local.
                if (_defAt.TryGetValue((k, i.A), out int caught) && (_reaches.GetValueOrDefault(caught) ?? []).Count == 0)
                {
                    Emit(new JExprStmt(new JCaught(CaughtInternal(i.Address))));
                }
                else
                {
                    Write(k, i.A, new JCaught(CaughtInternal(i.Address)));
                }

                return false;
            case 0x0E:
                Emit(new IrReturn(null));
                return true;
            case >= 0x0F and <= 0x11:
                Emit(new IrReturn(Coerce(Read(k, i.A), JCall.ReturnType(_method.Descriptor))));
                return true;
            case >= 0x12 and <= 0x19:
                if (!Folded(k, i.A))
                {
                    Write(k, i.A, Constant(k, i));
                }

                return false;
            case 0x1A or 0x1B:
                if (!Folded(k, i.A))
                {
                    Write(k, i.A, new JConst(StringAt(i.Index), "Ljava/lang/String;"));
                }

                return false;
            case 0x1C:
                if (!Folded(k, i.A))
                {
                    Write(k, i.A, new JConst(new ClassLiteral(TypeAt(i.Index) ?? Object), "Ljava/lang/Class;"));
                }

                return false;
            case 0x1D or 0x1E:
                Emit(new JMonitor(op == 0x1D, Read(k, i.A, Object)));
                return false;
            case 0x1F:
                Write(k, i.A, new JCast(TypeAt(i.Index) ?? Object, Read(k, i.A, Object)));
                return false;
            case 0x20:
                Write(k, i.A, new JInstanceOf(Read(k, i.B, Object), TypeAt(i.Index) ?? Object));
                return false;
            case 0x21:
                Write(k, i.A, new JArrayLength(Read(k, i.B, Object)));
                return false;
            case 0x22:
                // The object and its constructor call are one `new`, written where the constructor runs.
                return false;
            case 0x23:
                Write(k, i.A, new JNewArray(TypeAt(i.Index) ?? "[Ljava/lang/Object;", [Read(k, i.B, "I")]));
                return false;
            case 0x24 or 0x25:
                FilledArray(k, i);
                return false;
            case 0x26:
                FillArray(k, i);
                return false;
            case 0x27:
                Emit(new JThrow(Read(k, i.A, Object)));
                return true;
            case >= 0x28 and <= 0x2A:
                Emit(new IrGoto((ulong)i.Target));
                _block.Successors.Add((ulong)i.Target);
                return true;
            case 0x2B or 0x2C:
                Switch(k, i);
                return true;
            case >= 0x2D and <= 0x31:
            {
                string type = op switch { 0x2D or 0x2E => "F", 0x2F or 0x30 => "D", _ => "J" };
                Write(k, i.A, new JCompare(Read(k, i.B, type), Read(k, i.C, type)));
                return false;
            }

            case >= 0x32 and <= 0x37:
                Branch(k, i, new IrCondition(Code(op - 0x32), Read(k, i.A), Read(k, i.B)));
                return true;
            case >= 0x38 and <= 0x3D:
                Branch(k, i, ZeroTest(k, i));
                return true;
            case >= 0x44 and <= 0x4A:
            {
                var array = Read(k, i.B, Object);
                string element = array.Type is ['[', .. var e] ? e : ArrayOpType(op - 0x44, null);
                Write(k, i.A, new JArrayElement(array, Read(k, i.C, "I"), element));
                return false;
            }

            case >= 0x4B and <= 0x51:
            {
                var array = Read(k, i.B, Object);
                string element = array.Type is ['[', .. var e] ? e : ArrayOpType(op - 0x4B, null);
                Emit(new IrAssign(new JArrayElement(array, Read(k, i.C, "I"), element), Coerce(Read(k, i.A, element), element)));
                return false;
            }

            case >= 0x52 and <= 0x58:
            {
                var field = FieldAt(i.Index);
                Write(k, i.A, new JField(Read(k, i.B, Object), Owner(field?.Owner), field?.Name ?? "?", field?.Type ?? "I"));
                return false;
            }

            case >= 0x59 and <= 0x5F:
            {
                var field = FieldAt(i.Index);
                var target = new JField(Read(k, i.B, Object), Owner(field?.Owner), field?.Name ?? "?", field?.Type ?? "I");
                Emit(new IrAssign(target, Coerce(Read(k, i.A, field?.Type), field?.Type)));
                return false;
            }

            case >= 0x60 and <= 0x66:
            {
                var field = FieldAt(i.Index);

                // A lambda that captures nothing is one instance D8 keeps in a static field.
                if (field is { Name: "INSTANCE" } && Lambda(Owner(field.Owner), []) is { } lambda)
                {
                    Write(k, i.A, lambda);
                    return false;
                }

                Write(k, i.A, new JField(null, Owner(field?.Owner), field?.Name ?? "?", field?.Type ?? "I"));
                return false;
            }

            case >= 0x67 and <= 0x6D:
            {
                var field = FieldAt(i.Index);
                Emit(new IrAssign(new JField(null, Owner(field?.Owner), field?.Name ?? "?", field?.Type ?? "I"), Coerce(Read(k, i.A, field?.Type), field?.Type)));
                return false;
            }

            case (>= 0x6E and <= 0x72) or (>= 0x74 and <= 0x78):
                Invoke(k, i);
                return false;
            case 0xFA or 0xFB:
                InvokePolymorphic(k, i);
                return false;
            case 0xFC or 0xFD:
                InvokeCustom(k, i);
                return false;
            case >= 0x7B and <= 0x80:
            {
                string type = (op - 0x7B) switch { 0 or 1 => "I", 2 or 3 => "J", 4 => "F", _ => "D" };
                var operand = Read(k, i.B, type);
                Write(k, i.A, op is 0x7C or 0x7E
                    ? new JBinary("^", operand, type == "J" ? new JConst(-1L, "J") : JConst.Int(-1), type)
                    : new JNegate(operand));
                return false;
            }

            case >= 0x81 and <= 0x8F:
                Write(k, i.A, new JCast(ConversionTarget(op), Read(k, i.B, ConversionSource(op))));
                return false;
            case >= 0x90 and <= 0xAF:
            {
                var (left, right, result) = BinaryTypes(op - 0x90);
                Write(k, i.A, Binary(op - 0x90, Read(k, i.B, left), Read(k, i.C, right), result));
                return false;
            }

            case >= 0xB0 and <= 0xCF:
            {
                var (left, right, result) = BinaryTypes(op - 0xB0);
                Write(k, i.A, Binary(op - 0xB0, Read(k, i.A, left), Read(k, i.B, right), result));
                return false;
            }

            case >= 0xD0 and <= 0xE2:
                Write(k, i.A, Literal(op, Read(k, i.B, "I"), (int)i.Literal));
                return false;
            case 0xFE or 0xFF:
                Write(k, i.A, new JUnknown($"{i.Mnemonic} {i.Index}"));
                return false;
            default:
                Emit(new IrComment($"{i.Mnemonic} is not an instruction Android runs"));
                return false;
        }
    }

    private JExpr Constant(int k, DalvikInstruction i)
    {
        string type = _defAt.TryGetValue((k, i.A), out int d) && _defType[d] is { } known ? known : i.Opcode >= 0x16 ? "J" : "I";
        return Literal(i, type);
    }

    /// <summary><c>if-eqz</c> and the rest compare with zero: a reference with null, a boolean by itself, a compare's operands with each other.</summary>
    private IrExpr ZeroTest(int k, DalvikInstruction i)
    {
        var code = Code(i.Opcode - 0x38);
        var value = Read(k, i.A);
        if (value.Type is ['L' or '[', ..])
        {
            return new IrCondition(code, value, new JConst(null, value.Type));
        }

        if (value.Type == "Z" && code is IrCondCode.Equal or IrCondCode.NotEqual)
        {
            return code == IrCondCode.NotEqual ? value : new IrUnary(IrUnaryOp.LogicalNot, value);
        }

        return new IrCondition(code, value, JConst.Int(0));
    }

    private void Branch(int k, DalvikInstruction i, IrExpr condition)
    {
        int fallthrough = i.Address + i.Units;
        Emit(new IrBranch(condition, (ulong)i.Target, (ulong)fallthrough));
        _block.Successors.Add((ulong)i.Target);
        if (fallthrough != i.Target && _at.ContainsKey(fallthrough))
        {
            _block.Successors.Add((ulong)fallthrough);
        }

        _ = k;
    }

    private void Switch(int k, DalvikInstruction i)
    {
        var cases = i.Cases ?? [];
        var targets = new List<ulong>(cases.Count + 1);
        var values = new int?[cases.Count + 1];
        for (int c = 0; c < cases.Count; c++)
        {
            targets.Add((ulong)cases[c].Target);
            values[c] = cases[c].Key;
        }

        int fallthrough = i.Address + i.Units;
        targets.Add((ulong)fallthrough);
        values[cases.Count] = null;
        foreach (ulong target in targets.Distinct())
        {
            if (_at.ContainsKey((int)target))
            {
                _block.Successors.Add(target);
            }
        }

        _switchValues[(ulong)i.Address] = values;
        Emit(new IrSwitch(Read(k, i.A, "I"), targets));
    }

    private void Invoke(int k, DalvikInstruction i)
    {
        var target = MethodAt(i.Index);
        if (target is null)
        {
            Emit(new IrComment($"{i.Mnemonic} names method {i.Index}, which the file does not have"));
            return;
        }

        var arguments = Arguments(i);
        bool isStatic = i.Opcode is 0x71 or 0x77;
        var reads = arguments.Select(a => Coerce(Read(k, a.Register, a.Type), a.Type)).ToList();
        JExpr? receiver = isStatic || reads.Count == 0 ? null : reads[0];
        var args = isStatic ? reads : reads.Skip(1).ToList();
        string owner = Owner(target.Owner);
        string descriptor = target.Proto.Descriptor;

        // A constructor on an object new-instance made: the object is now `new T(args)`, stored in its register.
        if (target.Name == "<init>" && !isStatic && arguments.Count > 0 && FreshObject(k, arguments[0].Register) is var (created, at) && created is not null)
        {
            Write(k, arguments[0].Register, (JExpr?)Lambda(created, args) ?? new JNew(created, descriptor, args), at);
            return;
        }

        var kind = i.Opcode switch
        {
            0x6E or 0x74 => JCallKind.Virtual,
            0x6F or 0x75 or 0x70 or 0x76 => JCallKind.Special,
            0x71 or 0x77 => JCallKind.Static,
            _ => JCallKind.Interface,
        };
        var call = new JCall(kind, receiver, owner, target.Name, descriptor, args);

        // A helper D8 put in its own synthetic class that makes one call with its parameters first — a workaround
        // around a platform bug, like its compareAndSet — is that call.
        if (kind == JCallKind.Static && _dex is not null && Forwarded(_dex, target) is { } forwarded)
        {
            call = forwarded.Kind == JCallKind.Static
                ? new JCall(JCallKind.Static, null, Owner(forwarded.Target.Owner), forwarded.Target.Name, forwarded.Target.Proto.Descriptor, args)
                : new JCall(forwarded.Kind, args[0], Owner(forwarded.Target.Owner), forwarded.Target.Name, forwarded.Target.Proto.Descriptor, args.Skip(1).ToList());
        }
        Result(k, call);
    }

    /// <summary>What an invoke returns: stored by the move-result after it, or, with none, the call made for its effect.</summary>
    private void Result(int k, JExpr value)
    {
        if (k + 1 < _instructions.Count && _instructions[k + 1].Opcode is >= 0x0A and <= 0x0C)
        {
            var result = _instructions[k + 1];
            _pc = result.Address;
            Write(k + 1, result.A, value);
            _pc = _instructions[k].Address;
        }
        else
        {
            Emit(new JExprStmt(value));
        }
    }

    /// <summary>
    /// <c>invoke-polymorphic</c>: a call to a signature-polymorphic method — <c>MethodHandle.invokeExact</c>,
    /// <c>VarHandle.get</c> — whose type is the call site's own, carried by the instruction as a proto, rather than the
    /// method's declared <c>(Object...)Object</c>. It is the <c>invokevirtual</c> javac writes, with that type.
    /// </summary>
    private void InvokePolymorphic(int k, DalvikInstruction i)
    {
        var arguments = Arguments(i);
        if (MethodAt(i.Index) is not { } target || ProtoAt(i.Proto) is not { } proto || arguments.Count == 0)
        {
            Emit(new IrComment($"{i.Mnemonic} names a method or type the file does not have"));
            return;
        }

        var reads = arguments.Select(a => Coerce(Read(k, a.Register, a.Type), a.Type)).ToList();
        Result(k, new JCall(JCallKind.Virtual, reads[0], Owner(target.Owner), target.Name, proto.Descriptor, reads.Skip(1).ToList()));
    }

    /// <summary>
    /// <c>invoke-custom</c>: the <c>invokedynamic</c> it was, as the JVM lifter reads one — a lambda or method reference
    /// when the bootstrap is LambdaMetafactory, a string concatenation for StringConcatFactory, otherwise a call site
    /// named by its bootstrap. D8 leaves these in place when it is told not to desugar.
    /// </summary>
    private void InvokeCustom(int k, DalvikInstruction i)
    {
        if (_dex is not { } dex || i.Index < 0 || i.Index >= dex.CallSites.Count || dex.CallSites[i.Index] is not { Type: { } type } site)
        {
            Emit(new IrComment($"{i.Mnemonic} names call site {i.Index}, which the file does not have"));
            return;
        }

        var args = Arguments(i).Select(a => Coerce(Read(k, a.Register, a.Type), a.Type)).ToList();
        var dynamic = new JDynamic(site.Name, type.Descriptor, args);
        string? bootstrap = site.Bootstrap?.Method is { } method ? $"{Owner(method.Owner)}.{method.Name}" : null;
        if (bootstrap == "java/lang/invoke/StringConcatFactory.makeConcatWithConstants" && site.Arguments is [{ Value: string recipe }, ..])
        {
            dynamic = dynamic with { Concat = ConcatParts(recipe, site.Arguments.Skip(1).ToList()) };
        }
        else if (bootstrap is "java/lang/invoke/LambdaMetafactory.metafactory" or "java/lang/invoke/LambdaMetafactory.altMetafactory"
                 && site.Arguments.Count > 1 && site.Arguments[1].Value is DexMethodHandle { Method: { } body } handle)
        {
            dynamic = dynamic with { Target = (Owner(body.Owner), body.Name, body.Proto.Descriptor), TargetKind = HandleKind(handle.Kind) };
        }
        else
        {
            dynamic = dynamic with { Bootstrap = bootstrap };
        }

        Result(k, dynamic);
    }

    /// <summary>A DEX method handle's kind as the JVM numbers it (JVMS §5.4.3.5), which is what <see cref="JDynamic.TargetKind"/> holds.</summary>
    private static int HandleKind(int dexKind) => dexKind switch
    {
        0 => 4,   // static-put
        1 => 2,   // static-get
        2 => 3,   // instance-put
        3 => 1,   // instance-get
        4 => 6,   // invoke-static
        5 => 5,   // invoke-instance
        6 => 8,   // invoke-constructor
        7 => 7,   // invoke-direct
        _ => 9,   // invoke-interface
    };

    /// <summary>A concatenation recipe: <c>\u0001</c> is the next argument, <c>\u0002</c> the next constant, the rest literal text.</summary>
    private static List<object> ConcatParts(string recipe, IReadOnlyList<DexValue> constants)
    {
        var parts = new List<object>();
        var text = new System.Text.StringBuilder();
        int argument = 0;
        int constant = 0;
        foreach (char c in recipe)
        {
            if (c is '\u0001' or '\u0002')
            {
                if (text.Length > 0)
                {
                    parts.Add(text.ToString());
                    text.Clear();
                }

                if (c == '\u0001')
                {
                    parts.Add(argument++);
                }
                else if (constant < constants.Count && constants[constant++].Value is { } literal)
                {
                    parts.Add(Convert.ToString(literal, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
            else
            {
                text.Append(c);
            }
        }

        if (text.Length > 0)
        {
            parts.Add(text.ToString());
        }

        return parts;
    }

    /// <summary>
    /// A lambda D8 made a class of, as the <c>invokedynamic</c> javac wrote: D8 turns each lambda into a synthetic
    /// class implementing the interface, whose fields hold what it captures and whose one method calls the method
    /// the lambda is — javac's <c>lambda$main$0</c>, or the method a reference names. Recognised by that shape
    /// only: a synthetic final class, one interface, one method whose code reads the captured fields in order and
    /// makes one call with them and its own parameters, then returns what the call returned.
    /// </summary>
    private JDynamic? Lambda(string internalName, IReadOnlyList<JExpr> captured)
    {
        if (_dex is null || LambdaShape(_dex, internalName) is not { } shape || shape.Captures != captured.Count)
        {
            return null;
        }

        var (sam, target, kind, iface) = shape;
        string capturedTypes = string.Concat(captured.Select(c => c.Type ?? Object));
        return new JDynamic(sam.Name, $"({capturedTypes})L{iface};", captured)
        {
            Target = (Owner(target.Owner), target.Name, target.Proto.Descriptor),
            TargetKind = kind,
        };
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DexFile, Dictionary<DexMethodRef, (JCallKind Kind, DexMethodRef Target)?>> ForwardCache = new();

    /// <summary>
    /// The call a D8 synthetic helper forwards to: a static method of a class D8 made (its source file says so) whose
    /// code starts by calling one method with exactly its own parameters, in order, and returns that method's type.
    /// </summary>
    private static (JCallKind Kind, DexMethodRef Target)? Forwarded(DexFile dex, DexMethodRef helper)
    {
        var cache = ForwardCache.GetValue(dex, _ => []);
        lock (cache)
        {
            if (cache.TryGetValue(helper, out var known))
            {
                return known;
            }
        }

        (JCallKind, DexMethodRef)? result = null;
        var definition = dex.Classes.FirstOrDefault(c => c.Descriptor == helper.Owner);
        var method = definition?.DirectMethods.FirstOrDefault(m => m.Ref == helper);
        if (definition is { SourceFile: "D8$$SyntheticClass" } && method is { Code: { } code } && (method.Access & DexAccess.Static) != 0)
        {
            var first = Dalvik.Decode(code.Insns.Span).FirstOrDefault();
            int parameters = code.Registers - code.Ins;
            if (first is { Opcode: (>= 0x6E and <= 0x72) or (>= 0x74 and <= 0x78) } && first.Index < dex.MethodRefs.Count
                && first.Registers.SequenceEqual(Enumerable.Range(parameters, code.Ins))
                && dex.MethodRefs[first.Index] is { } target && target.Proto.ReturnType == helper.Proto.ReturnType)
            {
                var kind = first.Opcode switch
                {
                    0x6E or 0x74 => JCallKind.Virtual,
                    0x71 or 0x77 => JCallKind.Static,
                    0x72 or 0x78 => JCallKind.Interface,
                    _ => JCallKind.Special,
                };
                result = kind == JCallKind.Special ? null : (kind, target);
            }
        }

        lock (cache)
        {
            cache[helper] = result;
        }

        return result;
    }

    private sealed record LambdaClass(DexMethodRef Sam, DexMethodRef Target, int Kind, string Interface)
    {
        public int Captures { get; init; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DexFile, Dictionary<string, LambdaClass?>> LambdaCache = new();

    private static LambdaClass? LambdaShape(DexFile dex, string internalName)
    {
        var cache = LambdaCache.GetValue(dex, _ => new Dictionary<string, LambdaClass?>(StringComparer.Ordinal));
        lock (cache)
        {
            if (cache.TryGetValue(internalName, out var known))
            {
                return known;
            }
        }

        var shape = ReadLambdaShape(dex, internalName);
        lock (cache)
        {
            cache[internalName] = shape;
        }

        return shape;
    }

    private static LambdaClass? ReadLambdaShape(DexFile dex, string internalName)
    {
        var definition = dex.Classes.FirstOrDefault(c => c.Name == internalName);
        if (definition is null || (definition.Access & (DexAccess.Synthetic | DexAccess.Final)) != (DexAccess.Synthetic | DexAccess.Final)
            || definition.Interfaces.Count != 1 || definition.VirtualMethods.Count(m => (m.Access & DexAccess.VolatileOrBridge) == 0) != 1)
        {
            return null;
        }

        var sam = definition.VirtualMethods.First(m => (m.Access & DexAccess.VolatileOrBridge) == 0);
        if (sam.Code is not { } code)
        {
            return null;
        }

        // iget-object/iget… vX, p0, f$N (in order); check-cast on parameters; one invoke; move-result; return.
        var instructions = Dalvik.Decode(code.Insns.Span);
        int thisRegister = code.Registers - code.Ins;
        var fields = definition.InstanceFields.Select(f => f.Ref.Name).ToList();
        int captured = 0;
        DexMethodRef? target = null;
        int kind = 0;
        foreach (var i in instructions)
        {
            switch (i.Opcode)
            {
                case >= 0x52 and <= 0x58 when target is null && i.B == thisRegister && i.Index < dex.FieldRefs.Count && dex.FieldRefs[i.Index].Name == $"f${captured}":
                    captured++;
                    break;
                case 0x1F:
                    break;
                case (>= 0x6E and <= 0x72) or (>= 0x74 and <= 0x78) when target is null && i.Index < dex.MethodRefs.Count:
                    target = dex.MethodRefs[i.Index];
                    kind = i.Opcode switch
                    {
                        0x6E or 0x74 => 5,
                        0x6F or 0x75 or 0x70 or 0x76 => target.Name == "<init>" ? 8 : 7,
                        0x71 or 0x77 => 6,
                        _ => 9,
                    };
                    break;
                case 0x22 when target is null:
                    // new T(…) for a constructor reference: the new-instance before the <init> call.
                    break;
                case >= 0x0A and <= 0x0C or >= 0x0E and <= 0x11 or 0x00:
                    break;
                default:
                    return null;
            }
        }

        if (target is null || captured != fields.Count)
        {
            return null;
        }

        return new LambdaClass(sam.Ref, target, kind, DexFile.InternalName(definition.Interfaces[0])) { Captures = captured };
    }

    /// <summary>The class a new-instance created, when the one definition of a register reaching here is that new-instance.</summary>
    private (string? Owner, int At) FreshObject(int k, int register)
    {
        if (!_reaching.TryGetValue((k, register), out var defs) || defs.Count != 1 || _defs[defs[0]].Instruction < 0)
        {
            return (null, -1);
        }

        int at = _defs[defs[0]].Instruction;
        var definition = _instructions[at];
        return definition.Opcode == 0x22 && TypeAt(definition.Index) is { } type ? (Owner(type), at) : (null, -1);
    }

    private void FilledArray(int k, DalvikInstruction i)
    {
        string type = TypeAt(i.Index) ?? "[I";
        string element = type.Length > 1 ? type[1..] : "I";
        var elements = i.Registers.Select(r => Coerce(Read(k, r, element), element)).ToList();
        var array = new JNewArray(type, [JConst.Int(elements.Count)]) { Elements = elements };
        if (k + 1 < _instructions.Count && _instructions[k + 1].Opcode == 0x0C)
        {
            var result = _instructions[k + 1];
            _pc = result.Address;
            Write(k + 1, result.A, array);
            _pc = i.Address;
        }
        else
        {
            Emit(new JExprStmt(array));
        }
    }

    /// <summary>The elements <c>fill-array-data</c> stores: into the <c>new</c> just before when there is one, else element by element.</summary>
    private void FillArray(int k, DalvikInstruction i)
    {
        if (i.ArrayData is not { } data)
        {
            Emit(new IrComment($"fill-array-data at {i.Address:X4}: {i.Problem}"));
            return;
        }

        var array = Read(k, i.A, Object);
        string element = array.Type is ['[', .. var e] ? e : data.Width switch { 1 => "B", 2 => "S", 8 => "J", _ => "I" };
        var values = data.Values.Select(v => (JExpr)(element switch
        {
            "F" => new JConst(BitConverter.Int32BitsToSingle((int)v), "F"),
            "D" => new JConst(BitConverter.Int64BitsToDouble(v), "D"),
            "J" => new JConst(v, "J"),
            _ => new JConst((int)v, element),
        })).ToList();

        var statements = _block.Statements;
        if (statements.Count > 0 && statements[^1] is IrAssign { Dst: JLocal local, Src: JNewArray { Elements: null, Dimensions: [JConst { Value: int length }] } created } assign
            && array is JLocal read && read.Name == local.Name && length == values.Count)
        {
            statements[^1] = assign with { Src = created with { Elements = values } };
            return;
        }

        for (int v = 0; v < values.Count; v++)
        {
            Emit(new IrAssign(new JArrayElement(array, JConst.Int(v), element), values[v]));
        }
    }

    private static JExpr Binary(int index, JExpr left, JExpr right, string result)
    {
        string symbol = (index % 11) switch
        {
            0 => "+", 1 => "-", 2 => "*", 3 => "/", 4 => "%", 5 => "&", 6 => "|", 7 => "^", 8 => "<<", 9 => ">>", _ => ">>>",
        };
        if (index >= 22)
        {
            symbol = ((index - 22) % 5) switch { 0 => "+", 1 => "-", 2 => "*", 3 => "/", _ => "%" };
        }

        if (symbol is "&" or "|" or "^" && left.Type == "Z" && right.Type == "Z")
        {
            result = "Z";
        }

        return new JBinary(symbol, left, right, result);
    }

    private static JExpr Literal(byte op, JExpr operand, int literal)
    {
        int index = op >= 0xD8 ? op - 0xD8 : op - 0xD0;
        string symbol = index switch { 0 => "+", 1 => "-", 2 => "*", 3 => "/", 4 => "%", 5 => "&", 6 => "|", 7 => "^", 8 => "<<", 9 => ">>", _ => ">>>" };

        // rsub-int: the literal minus the register.
        return index == 1 ? new JBinary("-", JConst.Int(literal), operand, "I") : new JBinary(symbol, operand, JConst.Int(literal), "I");
    }

    /// <summary>Left, right and result types of binary operator <paramref name="index"/> in Dalvik's order: int, long, float, double.</summary>
    private static (string Left, string Right, string Result) BinaryTypes(int index) => index switch
    {
        < 11 => ("I", "I", "I"),
        < 19 => ("J", "J", "J"),
        < 22 => ("J", "I", "J"),
        < 27 => ("F", "F", "F"),
        _ => ("D", "D", "D"),
    };

    private static string ConversionSource(byte op) => op switch
    {
        0x81 or 0x82 or 0x83 or 0x8D or 0x8E or 0x8F => "I",
        0x84 or 0x85 or 0x86 => "J",
        0x87 or 0x88 or 0x89 => "F",
        _ => "D",
    };

    private static string ConversionTarget(byte op) => op switch
    {
        0x81 or 0x88 or 0x8B => "J",
        0x82 or 0x85 or 0x8C => "F",
        0x83 or 0x86 or 0x89 => "D",
        0x84 or 0x87 or 0x8A => "I",
        0x8D => "B",
        0x8E => "C",
        _ => "S",
    };

    private static string ArrayOpType(int index, string? fallback) => index switch
    {
        0 => fallback ?? "I",
        1 => "J",
        2 => Object,
        3 => "Z",
        4 => "B",
        5 => "C",
        _ => "S",
    };

    private static IrCondCode Code(int index) => index switch
    {
        0 => IrCondCode.Equal,
        1 => IrCondCode.NotEqual,
        2 => IrCondCode.Less,
        3 => IrCondCode.GreaterOrEqual,
        4 => IrCondCode.Greater,
        _ => IrCondCode.LessOrEqual,
    };

    /// <summary>The registers an invoke passes, each with the type its parameter has: the receiver first, a pair for a long or double.</summary>
    private List<(int Register, string Type)> Arguments(DalvikInstruction i)
    {
        var list = new List<(int, string)>();
        DexProto? proto = i.Opcode switch
        {
            0xFA or 0xFB => ProtoAt(i.Proto),
            0xFC or 0xFD => _dex is { } dex && i.Index >= 0 && i.Index < dex.CallSites.Count ? dex.CallSites[i.Index].Type : null,
            _ => MethodAt(i.Index)?.Proto,
        };
        if (proto is null)
        {
            return list;
        }

        var registers = i.Registers;
        int at = 0;
        if (i.Opcode is not (0x71 or 0x77 or 0xFC or 0xFD))
        {
            // invoke-polymorphic's receiver is the handle: the owner of the method it names, MethodHandle or VarHandle.
            string receiver = MethodAt(i.Index)?.Owner ?? Object;
            if (at < registers.Count)
            {
                list.Add((registers[at++], receiver));
            }
        }

        foreach (string parameter in proto.Parameters)
        {
            if (at >= registers.Count)
            {
                break;
            }

            list.Add((registers[at], parameter));
            at += parameter is "J" or "D" ? 2 : 1;
        }

        return list;
    }

    /// <summary>The type a move-result takes: the return of the invoke before it, or the array a filled-new-array made.</summary>
    private string? ResultType(int k)
    {
        if (k == 0)
        {
            return null;
        }

        var before = _instructions[k - 1];
        return before.Opcode switch
        {
            0x24 or 0x25 => TypeAt(before.Index),
            (>= 0x6E and <= 0x72) or (>= 0x74 and <= 0x78) => MethodAt(before.Index)?.Proto.ReturnType,
            0xFA or 0xFB => ProtoAt(before.Proto)?.ReturnType,
            0xFC or 0xFD when _dex is { } dex && before.Index >= 0 && before.Index < dex.CallSites.Count => dex.CallSites[before.Index].Type?.ReturnType,
            _ => null,
        };
    }

    private string? CaughtInternal(int address)
    {
        var handler = _slots.Handlers.FirstOrDefault(h => h.HandlerPc == address);
        return handler?.CatchType;
    }

    private string CaughtType(int address) => CaughtInternal(address) is { } type ? $"L{type};" : "Ljava/lang/Throwable;";

    private static string Owner(string? descriptor) => descriptor is null ? "java/lang/Object" : DexFile.InternalName(descriptor);

    private string StringAt(int index) => _dex is { } dex && index >= 0 && index < dex.Strings.Count ? dex.Strings[index] : string.Empty;

    private string? TypeAt(int index) => _dex is { } dex && index >= 0 && index < dex.Types.Count ? dex.Types[index] : null;

    private DexFieldRef? FieldAt(int index) => _dex is { } dex && index >= 0 && index < dex.FieldRefs.Count ? dex.FieldRefs[index] : null;

    private DexMethodRef? MethodAt(int index) => _dex is { } dex && index >= 0 && index < dex.MethodRefs.Count ? dex.MethodRefs[index] : null;

    private DexProto? ProtoAt(int index) => _dex is { } dex && index >= 0 && index < dex.Protos.Count ? dex.Protos[index] : null;
}
