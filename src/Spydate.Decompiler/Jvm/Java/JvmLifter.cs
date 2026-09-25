using System.Globalization;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Jvm.Java;

/// <summary>
/// One method's code lifted into IR blocks: the operand stack simulated away into expressions, locals named, and
/// the exception table kept for the region builder. Block and statement addresses are bytecode offsets.
/// </summary>
internal sealed class LiftedMethod
{
    public required IrFunction Function { get; init; }

    public required IReadOnlyList<ExceptionHandler> Handlers { get; init; }

    /// <summary>For each switch, by the offset of its instruction: the case value of each target index, null for default.</summary>
    public required Dictionary<ulong, int?[]> SwitchValues { get; init; }

    /// <summary>
    /// The statements a handler that turns what it catches into a <c>MatchException</c> covers, by address: a record
    /// pattern's accessor calls (<see cref="JavaPatterns"/>).
    /// </summary>
    public HashSet<ulong> MatchCovered { get; } = [];

    public required LocalNamer Locals { get; init; }
}

/// <summary>
/// Turns a method's bytecode into IR blocks of Java-shaped statements.
///
/// The operand stack is simulated per block. What a block leaves on the stack for its successor is written to a
/// stack variable at the join (a conditional expression's value), except an object that is still waiting for its
/// constructor, which is carried as it is so <c>new T(cond ? a : b)</c> still folds. Evaluation order is kept by
/// spilling what is on the stack to a temporary before anything with an effect runs over it — a call, a store,
/// a write to the local it reads — and the inliner puts the temporaries back where that changes nothing. Trees
/// are kept shallow the same way, so a crafted method cannot build an expression deep enough to overflow the
/// stack of whatever walks it.
///
/// Nothing here throws on bad code: an underflowing stack yields a placeholder and a warning, and an undecodable
/// instruction ends its block.
/// </summary>
internal sealed class JvmLifter
{
    /// <summary>Past this height an expression is spilled into a temporary, which bounds every recursive walk that follows.</summary>
    private const int MaxDepth = 40;

    /// <summary>How many times a method is lifted again to carry values through joins; each pass reaches one join further.</summary>
    private const int MaxPasses = 4;

    private readonly ClassFile _class;
    private readonly JvmMethod _method;
    private readonly CodeAttribute _code;
    private readonly IReadOnlyList<JvmInstruction> _instructions;
    private readonly ConstantPool _pool;
    private readonly LocalNamer _locals;
    private readonly IrFunction _function;
    private readonly Dictionary<int, IrBlock> _blocks = new();
    private readonly Dictionary<int, List<JExpr>> _entryStacks = new();
    private readonly Dictionary<ulong, int?[]> _switchValues = new();
    private readonly SortedSet<int> _leaders = new();
    private readonly HashSet<int> _handlerStarts = new();

    /// <summary>What each path hands each stack slot of each block, null where two paths differ.</summary>
    private readonly Dictionary<(int Target, int Slot), JExpr?> _incoming = new();

    /// <summary>From the pass before: the slots every path hands the same local or constant, which need no variable.</summary>
    private readonly IReadOnlyDictionary<(int Target, int Slot), JExpr>? _carried;
    private int _temps;
    private int _stackVars;
    private int _uninitialized;
    private bool _warnedUnderflow;

    private List<JExpr> _stack = [];
    private IrBlock _block = null!;
    private int _pc;

    private JvmLifter(ClassFile file, JvmMethod method, CodeAttribute code, IReadOnlyDictionary<(int, int), JExpr>? carried, IReadOnlySet<string>? reserved)
    {
        _carried = carried;
        _class = file;
        _method = method;
        _code = code;
        _pool = file.Pool;
        _instructions = Bytecode.Decode(code.Code.Span);
        _locals = new LocalNamer(file, method, code, reserved);
        _function = new IrFunction(0, method.Name, 32);
    }

    /// <param name="reserved">Names the method's locals must not take — a lambda's, those of the method it is written in.</param>
    public static LiftedMethod Lift(ClassFile file, JvmMethod method, CodeAttribute code, IReadOnlySet<string>? reserved = null)
    {
        // A method translated from DEX has Dalvik code, which lifts into the same IR its own way.
        if (method.Dalvik is { } dalvik)
        {
            return DalvikLifter.Lift(file, method, dalvik, reserved);
        }

        // Again while a join is handed values: each pass learns which slots every path fills with the same local or
        // constant, and the next carries those as they are instead of through a variable per path — which can make
        // the paths into a later join agree too.
        var lifter = new JvmLifter(file, method, code, carried: null, reserved);
        lifter.Run();
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            var carried = lifter._incoming.Where(kv => kv.Value is { IsSimple: true } and not JUninitialized).ToDictionary(kv => kv.Key, kv => kv.Value!);
            if (carried.Count == (lifter._carried?.Count ?? 0))
            {
                break;
            }

            lifter = new JvmLifter(file, method, code, carried, reserved);
            lifter.Run();
        }

        return new LiftedMethod
        {
            Function = lifter._function,
            Handlers = code.Handlers,
            SwitchValues = lifter._switchValues,
            Locals = lifter._locals,
        };
    }

    private void Run()
    {
        FindLeaders();
        foreach (int leader in _leaders)
        {
            _blocks[leader] = new IrBlock((ulong)leader);
            _function.Blocks.Add(_blocks[leader]);
        }

        foreach (var handler in _code.Handlers)
        {
            if (_blocks.ContainsKey(handler.HandlerPc))
            {
                _entryStacks.TryAdd(handler.HandlerPc, [new JCaught(handler.CatchType)]);
            }
        }

        // Blocks in order of discovery from the entry, then from each handler, so a block's entry stack is known
        // before it is lifted wherever the code says what it is; anything unreached is lifted with an empty stack.
        _entryStacks.TryAdd(0, []);
        var order = new List<int>();
        var seen = new HashSet<int>();
        var work = new Stack<int>();
        foreach (int root in new[] { 0 }.Concat(_code.Handlers.Select(h => h.HandlerPc)).Reverse())
        {
            work.Push(root);
        }

        while (work.Count > 0)
        {
            int at = work.Pop();
            if (!_blocks.ContainsKey(at) || !seen.Add(at))
            {
                continue;
            }

            order.Add(at);
            foreach (ulong succ in Enumerable.Reverse(Successors(at)))
            {
                work.Push((int)succ);
            }
        }

        order.AddRange(_leaders.Where(l => !seen.Contains(l)));
        foreach (int leader in order)
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

    // --- blocks ------------------------------------------------------------------

    private void FindLeaders()
    {
        _leaders.Add(0);
        foreach (var instruction in _instructions)
        {
            if (instruction.Problem is not null)
            {
                continue;
            }

            int next = instruction.Offset + instruction.Length;
            foreach (int target in Targets(instruction))
            {
                AddLeader(target);
            }

            if (EndsBlock(instruction))
            {
                AddLeader(next);
            }
        }

        foreach (var handler in _code.Handlers)
        {
            AddLeader(handler.StartPc);
            AddLeader(handler.EndPc);
            AddLeader(handler.HandlerPc);
            _handlerStarts.Add(handler.HandlerPc);
        }

        // Only offsets where an instruction starts can lead a block; anything else a crafted table names is dropped.
        var starts = _instructions.Select(i => i.Offset).ToHashSet();
        _leaders.RemoveWhere(l => !starts.Contains(l));
    }

    private void AddLeader(int offset)
    {
        if (offset >= 0 && offset < _code.Code.Length)
        {
            _leaders.Add(offset);
        }
    }

    private static IEnumerable<int> Targets(JvmInstruction instruction) => instruction.Kind switch
    {
        OperandKind.Branch or OperandKind.WideBranch => [instruction.Operand],
        OperandKind.TableSwitch or OperandKind.LookupSwitch => (instruction.Cases ?? []).Select(c => c.Target).Append(instruction.Default),
        _ => [],
    };

    private static bool EndsBlock(JvmInstruction instruction)
        => instruction.Kind is OperandKind.Branch or OperandKind.WideBranch or OperandKind.TableSwitch or OperandKind.LookupSwitch
           || instruction.Opcode is >= 0xAC and <= 0xB1 or 0xBF or 0xA9;

    private IEnumerable<ulong> Successors(int leader) => _blocks[leader].Successors.Count > 0 ? _blocks[leader].Successors : PlannedSuccessors(leader);

    /// <summary>A block's successors from its last instruction, before it is lifted — for the discovery order.</summary>
    private IEnumerable<ulong> PlannedSuccessors(int leader)
    {
        var last = LastInstruction(leader);
        if (last is null || last.Problem is not null)
        {
            return [];
        }

        if (last.Opcode is >= 0xAC and <= 0xB1 or 0xBF or 0xA9)
        {
            return [];
        }

        var targets = Targets(last).Select(t => (ulong)t).ToList();
        if (last.Opcode is 0xA7 or 0xC8)
        {
            return targets;
        }

        if (last.Kind is OperandKind.TableSwitch or OperandKind.LookupSwitch)
        {
            return targets.Distinct();
        }

        int next = last.Offset + last.Length;
        return _leaders.Contains(next) ? targets.Append((ulong)next) : targets;
    }

    private JvmInstruction? LastInstruction(int leader)
    {
        int end = _leaders.GetViewBetween(leader + 1, int.MaxValue).DefaultIfEmpty(int.MaxValue).First();
        JvmInstruction? last = null;
        foreach (var instruction in InstructionsFrom(leader))
        {
            if (instruction.Offset >= end)
            {
                break;
            }

            last = instruction;
        }

        return last;
    }

    private IEnumerable<JvmInstruction> InstructionsFrom(int offset)
    {
        int index = BinarySearch(offset);
        for (int i = index; i >= 0 && i < _instructions.Count; i++)
        {
            yield return _instructions[i];
        }
    }

    private int BinarySearch(int offset)
    {
        int lo = 0;
        int hi = _instructions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int at = _instructions[mid].Offset;
            if (at == offset)
            {
                return mid;
            }

            if (at < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private void LiftBlock(int leader)
    {
        _block = _blocks[leader];
        _stack = _entryStacks.TryGetValue(leader, out var entry) ? [.. entry] : [];
        int end = _leaders.GetViewBetween(leader + 1, int.MaxValue).DefaultIfEmpty(int.MaxValue).First();

        JvmInstruction? last = null;
        bool ended = false;
        foreach (var instruction in InstructionsFrom(leader))
        {
            if (instruction.Offset >= end)
            {
                break;
            }

            last = instruction;
            _pc = instruction.Offset;
            if (instruction.Problem is not null)
            {
                Emit(new IrComment($"bytecode at {instruction.Offset:X4} could not be decoded: {instruction.Problem}"));
                _function.Warnings.Add($"undecodable bytecode at {instruction.Offset:X4}");
                ended = true;
                break;
            }

            if (Lift(instruction))
            {
                ended = true;
                break;
            }
        }

        if (!ended && last is not null && end != int.MaxValue)
        {
            // Falls into the next block.
            Flow(end);
            _block.Successors.Add((ulong)end);
        }
    }

    // --- the stack ------------------------------------------------------------------

    private void Push(JExpr value)
    {
        if (value.Depth > MaxDepth)
        {
            value = SpillOne(value);
        }

        _stack.Add(value);
    }

    private JExpr Pop()
    {
        if (_stack.Count == 0)
        {
            if (!_warnedUnderflow)
            {
                _function.Warnings.Add($"the operand stack underflows at {_pc:X4}");
                _warnedUnderflow = true;
            }

            return new JUnknown("stack underflow");
        }

        var value = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return value;
    }

    private JExpr[] PopMany(int count)
    {
        var values = new JExpr[count];
        for (int i = count - 1; i >= 0; i--)
        {
            values[i] = Pop();
        }

        return values;
    }

    private static bool IsWide(JExpr value) => value.Type is "J" or "D";

    private JLocal NewTemp(string? type, bool inlinable = true)
    {
        var temp = new JLocal($"t{++_temps}", type, JLocalKind.Temp) { Inlinable = inlinable };
        _locals.Declare(temp);
        return temp;
    }

    private JLocal SpillOne(JExpr value, bool inlinable = true)
    {
        var temp = NewTemp(value.Type, inlinable);
        Emit(new IrAssign(temp, value));
        return temp;
    }

    /// <summary>
    /// Evaluates now whatever on the stack the coming effect could change the value of, so it is not read later
    /// than the bytecode read it. <paramref name="local"/> set: only what reads that local; otherwise anything not
    /// already a local or a constant.
    /// </summary>
    private void Spill(string? local = null)
    {
        for (int i = 0; i < _stack.Count; i++)
        {
            var value = _stack[i];
            if (value.IsSimple)
            {
                continue;
            }

            if (local is null || Reads(value, local))
            {
                _stack[i] = SpillOne(value);
            }
        }
    }

    private static bool Reads(IrExpr expression, string local)
    {
        var pending = new Stack<IrExpr>();
        pending.Push(expression);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case JLocal l when l.Name == local:
                    return true;
                case JExpr j:
                    foreach (var child in j.Children)
                    {
                        pending.Push(child);
                    }

                    break;
                case IrCondition c:
                    pending.Push(c.Left);
                    pending.Push(c.Right);
                    break;
                case IrUnary u:
                    pending.Push(u.Operand);
                    break;
            }
        }

        return false;
    }

    private void Emit(IrStmt statement) => _block.Statements.Add(statement with { Va = (ulong)_pc });

    /// <summary>
    /// Hands what is on the stack to a successor. The first path to reach a block fixes its entry stack — stack
    /// variables for values, the very same object for one awaiting its constructor — and every path assigns its
    /// values to those variables before it leaves.
    /// </summary>
    private void Flow(int target)
    {
        for (int i = 0; i < _stack.Count; i++)
        {
            _incoming[(target, i)] = !_incoming.TryGetValue((target, i), out var seen) || Equals(seen, _stack[i]) ? _stack[i] : null;
        }

        if (_stack.Count == 0 && !_entryStacks.ContainsKey(target))
        {
            _entryStacks[target] = [];
            return;
        }

        if (!_entryStacks.TryGetValue(target, out var entry))
        {
            entry = new List<JExpr>(_stack.Count);
            for (int i = 0; i < _stack.Count; i++)
            {
                var value = _stack[i];
                if (value is JUninitialized)
                {
                    entry.Add(value);
                    continue;
                }

                if (_carried is not null && _carried.TryGetValue((target, i), out var same) && Equals(same, value))
                {
                    entry.Add(same);
                    continue;
                }

                var variable = new JLocal($"s{++_stackVars}", value.Type, JLocalKind.Stack);
                _locals.Declare(variable);
                entry.Add(variable);
            }

            _entryStacks[target] = entry;
        }

        if (entry.Count != _stack.Count)
        {
            _function.Warnings.Add($"the operand stack is {_stack.Count} deep at {_pc:X4} but {entry.Count} where it goes, {target:X4}");
        }

        for (int i = 0; i < Math.Min(entry.Count, _stack.Count); i++)
        {
            if (entry[i] is JLocal variable && !Equals(_stack[i], variable) && _stack[i] is not JUninitialized)
            {
                Emit(new IrAssign(variable, _stack[i]));
            }
        }
    }

    // --- instructions -------------------------------------------------------------------

    /// <summary>Lifts one instruction. Returns true when it ends the block.</summary>
    private bool Lift(JvmInstruction ins)
    {
        byte op = ins.Opcode;
        switch (op)
        {
            case 0x00:
                return false;
            case 0x01:
                Push(JConst.Null);
                return false;
            case >= 0x02 and <= 0x08:
                Push(JConst.Int(op - 0x03));
                return false;
            case 0x09 or 0x0A:
                Push(new JConst((long)(op - 0x09), "J"));
                return false;
            case >= 0x0B and <= 0x0D:
                Push(new JConst((float)(op - 0x0B), "F"));
                return false;
            case 0x0E or 0x0F:
                Push(new JConst((double)(op - 0x0E), "D"));
                return false;
            case 0x10 or 0x11:
                Push(JConst.Int(ins.Operand));
                return false;
            case 0x12 or 0x13 or 0x14:
                Push(Constant(ins.Operand));
                return false;
            case >= 0x15 and <= 0x19:
                Push(_locals.Load(ins.Operand, TypeOfLoad(op - 0x15), _pc));
                return false;
            case >= 0x1A and <= 0x2D:
                Push(_locals.Load((op - 0x1A) % 4, TypeOfLoad((op - 0x1A) / 4), _pc));
                return false;
            case >= 0x2E and <= 0x35:
            {
                var index = Pop();
                var array = Pop();
                Push(new JArrayElement(array, index, ElementType(array, op - 0x2E)));
                return false;
            }

            case >= 0x36 and <= 0x3A:
                Store(ins.Operand, TypeOfLoad(op - 0x36), ins.Offset + ins.Length);
                return false;
            case >= 0x3B and <= 0x4E:
                Store((op - 0x3B) % 4, TypeOfLoad((op - 0x3B) / 4), ins.Offset + ins.Length);
                return false;
            case >= 0x4F and <= 0x56:
            {
                var value = Pop();
                var index = Pop();
                var array = Pop();
                Spill();
                var element = new JArrayElement(array, index, ElementType(array, op - 0x4F));
                Emit(new IrAssign(element, Coerce(value, element.Type)));
                return false;
            }

            case 0x57:
                Discard(Pop());
                return false;
            case 0x58:
            {
                var value = Pop();
                Discard(value);
                if (!IsWide(value))
                {
                    Discard(Pop());
                }

                return false;
            }

            case 0x59:
                Dup(1, 0);
                return false;
            case 0x5A:
                Dup(1, 1);
                return false;
            case 0x5B:
                Dup(1, 2);
                return false;
            case 0x5C:
                Dup(2, 0);
                return false;
            case 0x5D:
                Dup(2, 1);
                return false;
            case 0x5E:
                Dup(2, 2);
                return false;
            case 0x5F:
            {
                var a = Pop();
                var b = Pop();
                // Swapping reverses evaluation order: both are evaluated now, and neither is inlined back.
                Push(Fix(a));
                Push(Fix(b));
                return false;
            }

            case >= 0x60 and <= 0x83:
                Arithmetic(op);
                return false;
            case 0x84:
            {
                var local = _locals.Load(ins.Operand, "I", _pc);
                Spill(local.Name);
                Emit(new IrAssign(local, new JBinary("+", local, JConst.Int(ins.Operand2), local.Type)));
                return false;
            }

            case >= 0x85 and <= 0x93:
                Push(new JCast(ConversionTarget(op), Pop()));
                return false;
            case 0x94:
            case 0x95:
            case 0x96:
            case 0x97:
            case 0x98:
            {
                var right = Pop();
                var left = Pop();
                Push(new JCompare(left, right));
                return false;
            }

            case >= 0x99 and <= 0x9E:
                Branch(ins, UnaryCondition(op, Pop()));
                return true;
            case >= 0x9F and <= 0xA6:
            {
                var right = Pop();
                var left = Pop();
                Branch(ins, new IrCondition(CompareCode(op - 0x9F), left, right));
                return true;
            }

            case 0xA7 or 0xC8:
                Flow(ins.Operand);
                Emit(new IrGoto((ulong)ins.Operand));
                _block.Successors.Add((ulong)ins.Operand);
                return true;
            case 0xA8 or 0xC9:
                // jsr: a subroutine, from before Java 6. Shown as the jump it is.
                _function.Warnings.Add($"jsr at {_pc:X4}: a pre-Java-6 subroutine, shown as a jump");
                Push(new JUnknown("return address"));
                Flow(ins.Operand);
                Emit(new IrGoto((ulong)ins.Operand));
                _block.Successors.Add((ulong)ins.Operand);
                return true;
            case 0xA9:
                Emit(new IrComment("ret: back from a pre-Java-6 subroutine"));
                return true;
            case 0xAA or 0xAB:
                Switch(ins);
                return true;
            case >= 0xAC and <= 0xB0:
                Emit(new IrReturn(Coerce(Pop(), ReturnType())));
                return true;
            case 0xB1:
                Emit(new IrReturn(null));
                return true;
            case 0xB2:
                if (_pool.Member(ins.Operand) is { } getStatic)
                {
                    Push(new JField(null, getStatic.Owner, getStatic.Name, getStatic.Descriptor));
                }
                else
                {
                    Push(new JUnknown($"field #{ins.Operand}"));
                }

                return false;
            case 0xB3:
            {
                var value = Pop();
                Spill();
                if (_pool.Member(ins.Operand) is { } putStatic)
                {
                    Emit(new IrAssign(new JField(null, putStatic.Owner, putStatic.Name, putStatic.Descriptor), Coerce(value, putStatic.Descriptor)));
                }

                return false;
            }

            case 0xB4:
            {
                var instance = Pop();
                Push(_pool.Member(ins.Operand) is { } getField
                    ? new JField(instance, getField.Owner, getField.Name, getField.Descriptor)
                    : new JUnknown($"field #{ins.Operand}"));
                return false;
            }

            case 0xB5:
            {
                var value = Pop();
                var instance = Pop();
                Spill();
                if (_pool.Member(ins.Operand) is { } putField)
                {
                    Emit(new IrAssign(new JField(instance, putField.Owner, putField.Name, putField.Descriptor), Coerce(value, putField.Descriptor)));
                }

                return false;
            }

            case >= 0xB6 and <= 0xB9:
                Invoke(ins, op);
                return false;
            case 0xBA:
                InvokeDynamic(ins);
                return false;
            case 0xBB:
                Push(new JUninitialized(_pool.ClassName(ins.Operand) ?? "?", ++_uninitialized));
                return false;
            case 0xBC:
                Push(new JNewArray("[" + PrimitiveArrayElement(ins.Operand), [Pop()]));
                return false;
            case 0xBD:
                Push(new JNewArray("[" + ClassDescriptor(_pool.ClassName(ins.Operand) ?? "java/lang/Object"), [Pop()]));
                return false;
            case 0xBE:
                Push(new JArrayLength(Pop()));
                return false;
            case 0xBF:
            {
                var exception = Pop();
                Spill();
                Emit(new JThrow(exception));
                return true;
            }

            case 0xC0:
                Push(new JCast(ClassDescriptor(_pool.ClassName(ins.Operand) ?? "java/lang/Object"), Pop()));
                return false;
            case 0xC1:
                Push(new JInstanceOf(Pop(), ClassDescriptor(_pool.ClassName(ins.Operand) ?? "java/lang/Object")));
                return false;
            case 0xC2 or 0xC3:
            {
                var lockObject = Pop();
                Spill();
                Emit(new JMonitor(op == 0xC2, lockObject));
                return false;
            }

            case 0xC5:
            {
                var dimensions = PopMany(ins.Operand2);
                Push(new JNewArray(ClassDescriptor(_pool.ClassName(ins.Operand) ?? "[Ljava/lang/Object;"), dimensions));
                return false;
            }

            case 0xC6 or 0xC7:
                Branch(ins, new IrCondition(op == 0xC6 ? IrCondCode.Equal : IrCondCode.NotEqual, Pop(), JConst.Null));
                return true;
            default:
                Emit(new IrComment($"{ins.Mnemonic} at {ins.Offset:X4} is not lifted"));
                return false;
        }
    }

    /// <summary>The type a load or store of this family moves: int, long, float, double, reference.</summary>
    private static string TypeOfLoad(int family) => family switch
    {
        0 => "I",
        1 => "J",
        2 => "F",
        3 => "D",
        _ => "Ljava/lang/Object;",
    };

    private void Store(int slot, string kind, int liveFrom)
    {
        var value = Pop();
        var local = _locals.Store(slot, kind, liveFrom, value.Type);
        Spill(local.Name);
        Emit(new IrAssign(local, Coerce(value, local.Type)));
    }

    /// <summary>A popped value's effect still happens: a call dropped on the floor is a call statement.</summary>
    private void Discard(JExpr value)
    {
        if (HasEffect(value))
        {
            Spill();
            Emit(new JExprStmt(value));
        }
    }

    private static bool HasEffect(JExpr value) => value switch
    {
        JCall or JNew or JDynamic => true,
        JLocal or JConst or JUninitialized or JUnknown or JCaught => false,
        _ => value.Children.OfType<JExpr>().Any(HasEffect),
    };

    /// <summary>Evaluates a value now unless reading it twice costs nothing, and marks it not to be moved back.</summary>
    private JExpr Fix(JExpr value) => value.IsSimple || value is JUninitialized ? value : SpillOne(value, inlinable: false);

    /// <summary>
    /// The dup family: <paramref name="words"/> is how many stack words are copied (1 or 2), <paramref name="down"/>
    /// how many words below them the copy goes. A long or double is two words.
    /// </summary>
    private void Dup(int words, int down)
    {
        var top = TakeWords(words);
        var below = TakeWords(down);
        for (int i = 0; i < top.Count; i++)
        {
            top[i] = down == 0 ? (top[i].IsSimple || top[i] is JUninitialized ? top[i] : SpillOne(top[i])) : Fix(top[i]);
        }

        for (int i = 0; i < below.Count; i++)
        {
            below[i] = down == 0 ? below[i] : Fix(below[i]);
        }

        _stack.AddRange(top);
        _stack.AddRange(below);
        _stack.AddRange(top);
    }

    /// <summary>Pops values covering <paramref name="words"/> stack words, returned bottom-first.</summary>
    private List<JExpr> TakeWords(int words)
    {
        var taken = new List<JExpr>();
        while (words > 0)
        {
            var value = Pop();
            taken.Insert(0, value);
            words -= IsWide(value) ? 2 : 1;
        }

        return taken;
    }

    private void Arithmetic(byte op)
    {
        if (op is >= 0x74 and <= 0x77)
        {
            Push(new JNegate(Pop()));
            return;
        }

        string[] symbols = ["+", "-", "*", "/", "%"];
        string result;
        string symbol;
        if (op <= 0x73)
        {
            int group = (op - 0x60) / 4;
            symbol = symbols[group];
            result = TypeOfLoad((op - 0x60) % 4);
        }
        else
        {
            (symbol, result) = op switch
            {
                0x78 => ("<<", "I"),
                0x79 => ("<<", "J"),
                0x7A => (">>", "I"),
                0x7B => (">>", "J"),
                0x7C => (">>>", "I"),
                0x7D => (">>>", "J"),
                0x7E => ("&", "I"),
                0x7F => ("&", "J"),
                0x80 => ("|", "I"),
                0x81 => ("|", "J"),
                0x82 => ("^", "I"),
                _ => ("^", "J"),
            };
        }

        var right = Pop();
        var left = Pop();

        // A bitwise operator on two booleans is a boolean one.
        if (symbol is "&" or "|" or "^" && left.Type == "Z" && right.Type == "Z")
        {
            result = "Z";
        }

        Push(new JBinary(symbol, left, right, result));
    }

    private static string ConversionTarget(byte op) => op switch
    {
        0x85 or 0x8C or 0x8F => "J",
        0x86 or 0x89 or 0x90 => "F",
        0x87 or 0x8A or 0x8D => "D",
        0x88 or 0x8B or 0x8E => "I",
        0x91 => "B",
        0x92 => "C",
        _ => "S",
    };

    private static IrCondCode CompareCode(int index) => index switch
    {
        0 => IrCondCode.Equal,
        1 => IrCondCode.NotEqual,
        2 => IrCondCode.Less,
        3 => IrCondCode.GreaterOrEqual,
        4 => IrCondCode.Greater,
        5 => IrCondCode.LessOrEqual,
        6 => IrCondCode.Equal,
        _ => IrCondCode.NotEqual,
    };

    /// <summary>
    /// <c>ifeq</c> and the rest compare with zero: against a <c>lcmp</c> they compare its two operands; on a
    /// boolean they test it; otherwise they are a comparison with 0.
    /// </summary>
    private static IrExpr UnaryCondition(byte op, JExpr value)
    {
        var code = CompareCode(op - 0x99);
        if (value is JCompare compare)
        {
            return new IrCondition(code, compare.Left, compare.Right);
        }

        if (value.Type == "Z" && code is IrCondCode.Equal or IrCondCode.NotEqual)
        {
            return code == IrCondCode.NotEqual ? value : new IrUnary(IrUnaryOp.LogicalNot, value);
        }

        return new IrCondition(code, value, JConst.Int(0));
    }

    private void Branch(JvmInstruction ins, IrExpr condition)
    {
        int target = ins.Operand;
        int fallthrough = ins.Offset + ins.Length;

        // What stays on the stack is handed to both successors: evaluated once, here, not once per path.
        Spill();
        Flow(target);
        Flow(fallthrough);
        Emit(new IrBranch(condition, (ulong)target, (ulong)fallthrough));
        _block.Successors.Add((ulong)target);
        if (fallthrough != target)
        {
            _block.Successors.Add((ulong)fallthrough);
        }
    }

    private void Switch(JvmInstruction ins)
    {
        var value = Pop();
        var cases = ins.Cases ?? [];
        var targets = new List<ulong>(cases.Count + 1);
        var values = new int?[cases.Count + 1];
        for (int i = 0; i < cases.Count; i++)
        {
            targets.Add((ulong)cases[i].Target);
            values[i] = cases[i].Match;
        }

        targets.Add((ulong)ins.Default);
        values[cases.Count] = null;
        Spill();
        foreach (ulong target in targets.Distinct())
        {
            Flow((int)target);
            _block.Successors.Add(target);
        }

        _switchValues[(ulong)ins.Offset] = values;
        Emit(new IrSwitch(value, targets));
    }

    private void Invoke(JvmInstruction ins, byte op)
    {
        if (_pool.Member(ins.Operand) is not { } target)
        {
            Emit(new IrComment($"{ins.Mnemonic} names constant #{ins.Operand}, which is not a method"));
            return;
        }

        var parameters = Descriptors.ParameterDescriptors(target.Descriptor);
        var args = PopMany(parameters.Count);
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = Coerce(args[i], parameters[i]);
        }

        var kind = op switch
        {
            0xB6 => JCallKind.Virtual,
            0xB7 => JCallKind.Special,
            0xB8 => JCallKind.Static,
            _ => JCallKind.Interface,
        };
        var receiver = kind == JCallKind.Static ? null : Pop();

        // A constructor on an object new has just made: the object, wherever it is on the stack, is now `new T(args)`.
        if (target.Name == "<init>" && receiver is JUninitialized fresh)
        {
            var constructed = new JNew(fresh.Owner, target.Descriptor, args);
            bool used = false;
            for (int i = 0; i < _stack.Count; i++)
            {
                if (Equals(_stack[i], fresh))
                {
                    _stack[i] = constructed;
                    used = true;
                }
            }

            Spill();
            if (!used)
            {
                Emit(new JExprStmt(constructed));
            }
            else if (constructed.Depth > MaxDepth)
            {
                Spill();
            }

            return;
        }

        var call = new JCall(kind, receiver, target.Owner, target.Name, target.Descriptor, args);
        Spill();
        if (call.Type is "V" or null)
        {
            Emit(new JExprStmt(call));
        }
        else
        {
            Push(call);
        }
    }

    private void InvokeDynamic(JvmInstruction ins)
    {
        if (_pool.Get(ins.Operand) is not { Tag: ConstantTag.InvokeDynamic } site || _pool.NameAndType(site.B) is not { } nat)
        {
            Push(new JUnknown($"invokedynamic #{ins.Operand}"));
            return;
        }

        var parameters = Descriptors.ParameterDescriptors(nat.Descriptor);
        var args = PopMany(parameters.Count);
        var dynamic = new JDynamic(nat.Name, nat.Descriptor, args);

        if (site.A < _class.BootstrapMethods.Count)
        {
            var bootstrap = _class.BootstrapMethods[site.A];
            string? bootstrapOwner = _pool.Get(bootstrap.MethodHandle) is { Tag: ConstantTag.MethodHandle } handle && _pool.Member(handle.B) is { } m
                ? $"{m.Owner}.{m.Name}"
                : null;

            if (bootstrapOwner == "java/lang/invoke/StringConcatFactory.makeConcatWithConstants"
                && bootstrap.Arguments.Count > 0 && _pool.Get(bootstrap.Arguments[0]) is { Tag: ConstantTag.String, A: var recipeIndex }
                && _pool.Utf8(recipeIndex) is { } recipe)
            {
                dynamic = dynamic with { Concat = ConcatParts(recipe, bootstrap.Arguments.Skip(1).ToList()) };
            }
            else if (bootstrapOwner is "java/lang/invoke/LambdaMetafactory.metafactory" or "java/lang/invoke/LambdaMetafactory.altMetafactory"
                     && bootstrap.Arguments.Count > 1
                     && _pool.Get(bootstrap.Arguments[1]) is { Tag: ConstantTag.MethodHandle, A: var kind, B: var body } && _pool.Member(body) is { } implementation)
            {
                dynamic = dynamic with { Target = (implementation.Owner, implementation.Name, implementation.Descriptor), TargetKind = kind };
            }
            else
            {
                // Its static arguments too: for a pattern switch's typeSwitch they are the case labels.
                dynamic = dynamic with { Bootstrap = bootstrapOwner, BootstrapArguments = bootstrap.Arguments.Select(Constant).ToList() };
            }
        }

        Spill();
        if (dynamic.Type is "V" or null)
        {
            Emit(new JExprStmt(dynamic));
        }
        else
        {
            Push(dynamic);
        }
    }

    /// <summary>
    /// A concatenation recipe: <c>\u0001</c> marks the next argument, <c>\u0002</c> the next constant, and the rest
    /// is literal text. Returned as literal strings and argument indices in order.
    /// </summary>
    private List<object> ConcatParts(string recipe, IReadOnlyList<int> constants)
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
                else if (constant < constants.Count && _pool.Get(constants[constant++]) is { Tag: ConstantTag.String, A: var s } && _pool.Utf8(s) is { } literal)
                {
                    parts.Add(literal);
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

    private JExpr Constant(int index)
    {
        switch (_pool.Get(index))
        {
            case { Tag: ConstantTag.Integer, Bits: var bits }:
                return JConst.Int((int)bits);
            case { Tag: ConstantTag.Float, Bits: var bits }:
                return new JConst(BitConverter.Int32BitsToSingle((int)bits), "F");
            case { Tag: ConstantTag.Long, Bits: var bits }:
                return new JConst(bits, "J");
            case { Tag: ConstantTag.Double, Bits: var bits }:
                return new JConst(BitConverter.Int64BitsToDouble(bits), "D");
            case { Tag: ConstantTag.String, A: var text }:
                return new JConst(_pool.Utf8(text) ?? string.Empty, "Ljava/lang/String;");
            case { Tag: ConstantTag.Class, A: var name }:
                return new JConst(new ClassLiteral(ClassDescriptor(_pool.Utf8(name) ?? "java/lang/Object")), "Ljava/lang/Class;");
            case { Tag: var tag }:
                return new JUnknown($"{tag.ToString().ToLowerInvariant()} constant #{index.ToString(CultureInfo.InvariantCulture)}");
            default:
                return new JUnknown($"constant #{index.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>An internal class name as a descriptor: <c>java/lang/String</c> is <c>Ljava/lang/String;</c>; an array name already is one.</summary>
    private static string ClassDescriptor(string internalName) => internalName.StartsWith('[') ? internalName : $"L{internalName};";

    private static string PrimitiveArrayElement(int code) => code switch
    {
        4 => "Z",
        5 => "C",
        6 => "F",
        7 => "D",
        8 => "B",
        9 => "S",
        10 => "I",
        _ => "J",
    };

    /// <summary>The element type an array load or store moves: from the array's own type when it is known.</summary>
    private static string? ElementType(JExpr array, int family)
    {
        if (array.Type is { Length: > 1 } arrayType && arrayType[0] == '[')
        {
            return arrayType[1..];
        }

        return family switch
        {
            0 => "I",
            1 => "J",
            2 => "F",
            3 => "D",
            4 => "Ljava/lang/Object;",
            5 => "B",
            6 => "C",
            _ => "S",
        };
    }

    private string? ReturnType() => JCall.ReturnType(_method.Descriptor);

    /// <summary>An int constant headed for a boolean or char says so, so it prints as <c>true</c> or <c>'a'</c>.</summary>
    private static JExpr Coerce(JExpr value, string? type) => value is JConst constant ? constant.As(type) : value;
}
