using Iced.Intel;

namespace Spydate.Disassembly;

/// <summary>How one address refers to another.</summary>
public enum XrefKind
{
    /// <summary><c>call sub_401000</c>.</summary>
    Call,
    /// <summary><c>call [iat_slot]</c> or <c>call rax</c> with a known slot.</summary>
    IndirectCall,
    /// <summary><c>jmp</c> / <c>jne</c> to a code address.</summary>
    Jump,
    /// <summary><c>jmp [iat_slot]</c> — usually a tail-call thunk.</summary>
    IndirectJump,
    /// <summary>The address is read as data.</summary>
    Read,
    /// <summary>The address is written as data.</summary>
    Write,
    /// <summary>The address is only taken, not dereferenced: <c>lea</c> or an immediate that lands in the image.</summary>
    Offset,
}

/// <summary>A single cross-reference: <see cref="FromVa"/> refers to <see cref="ToVa"/>.</summary>
public readonly record struct Xref(ulong FromVa, ulong ToVa, XrefKind Kind)
{
    public bool IsCode => Kind is XrefKind.Call or XrefKind.IndirectCall or XrefKind.Jump or XrefKind.IndirectJump;

    public bool IsData => !IsCode;

    public override string ToString() => $"0x{FromVa:X} -> 0x{ToVa:X} ({Kind})";
}

/// <summary>
/// Bidirectional cross-reference index. Discovery adds to it from a background thread while the UI
/// reads it, so every operation is guarded; readers get snapshots, never live lists.
/// </summary>
public sealed class XrefTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, List<Xref>> _to = new();
    private readonly Dictionary<ulong, List<Xref>> _from = new();
    private readonly HashSet<Xref> _seen = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>Records a reference. Returns false when the exact reference was already known.</summary>
    public bool Add(Xref xref)
    {
        lock (_gate)
        {
            if (!_seen.Add(xref))
            {
                return false;
            }

            Append(_to, xref.ToVa, xref);
            Append(_from, xref.FromVa, xref);
            return true;
        }
    }

    /// <summary>Everything that refers to <paramref name="va"/> — "who calls this?".</summary>
    public IReadOnlyList<Xref> To(ulong va) => Snapshot(_to, va);

    /// <summary>Everything <paramref name="va"/> refers to.</summary>
    public IReadOnlyList<Xref> From(ulong va) => Snapshot(_from, va);

    /// <summary>Number of references to <paramref name="va"/> without materialising them.</summary>
    public int CountTo(ulong va)
    {
        lock (_gate)
        {
            return _to.TryGetValue(va, out var list) ? list.Count : 0;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _to.Clear();
            _from.Clear();
            _seen.Clear();
        }
    }

    private static void Append(Dictionary<ulong, List<Xref>> index, ulong key, Xref xref)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = new List<Xref>(2);
        }

        list.Add(xref);
    }

    private IReadOnlyList<Xref> Snapshot(Dictionary<ulong, List<Xref>> index, ulong va)
    {
        lock (_gate)
        {
            return index.TryGetValue(va, out var list) ? list.ToArray() : Array.Empty<Xref>();
        }
    }
}

/// <summary>
/// Pulls cross-references out of decoded instructions: direct and indirect control flow, memory
/// operands with a statically known address, and immediates that land inside the image (how x86
/// code usually names a string). Not thread-safe; use one instance per thread.
/// </summary>
public sealed class XrefExtractor
{
    private readonly ICodeSource _source;

    public XrefExtractor(ICodeSource source) => _source = source;

    /// <summary>Adds every reference made by <paramref name="function"/> to <paramref name="table"/>.</summary>
    public int Extract(Function function, XrefTable table)
    {
        int added = 0;
        foreach (var ins in function.Instructions)
        {
            foreach (var xref in Extract(ins))
            {
                if (table.Add(xref))
                {
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>References made by a single instruction.</summary>
    public IEnumerable<Xref> Extract(DecodedInstruction ins)
    {
        switch (ins.Flow)
        {
            case InstructionFlow.Call when ins.BranchTargetVa is { } call:
                yield return new Xref(ins.Va, call, XrefKind.Call);
                break;

            case InstructionFlow.IndirectCall when ins.IndirectSlotVa is { } callSlot:
                yield return new Xref(ins.Va, callSlot, XrefKind.IndirectCall);
                break;

            case InstructionFlow.UnconditionalBranch or InstructionFlow.ConditionalBranch when ins.BranchTargetVa is { } jump:
                yield return new Xref(ins.Va, jump, XrefKind.Jump);
                break;

            case InstructionFlow.IndirectBranch when ins.IndirectSlotVa is { } jumpSlot:
                yield return new Xref(ins.Va, jumpSlot, XrefKind.IndirectJump);
                break;
        }

        // Data references. call/jmp [mem] already produced a code reference above, so skip those.
        if (!ins.IsCall && !ins.IsBranch && MemoryTarget(ins.Native) is { } data && _source.IsMapped(data))
        {
            yield return new Xref(ins.Va, data, MemoryAccessKind(ins));
        }

        foreach (ulong immediate in Immediates(ins.Native))
        {
            // Only immediates that resolve inside the image are addresses rather than plain numbers.
            if (immediate != 0 && _source.IsMapped(immediate))
            {
                yield return new Xref(ins.Va, immediate, XrefKind.Offset);
            }
        }
    }

    /// <summary>Absolute address of a memory operand when it is statically known (RIP-relative or no base/index).</summary>
    internal static ulong? MemoryTarget(in Instruction instr)
    {
        for (int op = 0; op < instr.OpCount; op++)
        {
            if (instr.GetOpKind(op) != OpKind.Memory)
            {
                continue;
            }

            if (instr.IsIPRelativeMemoryOperand)
            {
                return instr.IPRelativeMemoryAddress;
            }

            if (instr.MemoryBase == Register.None && instr.MemoryIndex == Register.None)
            {
                return instr.MemoryDisplacement64;
            }
        }

        return null;
    }

    /// <summary>
    /// Read, write or address-taken. <c>lea</c> never dereferences; otherwise a memory operand in
    /// position 0 of an instruction with more than one operand is the destination.
    /// </summary>
    private static XrefKind MemoryAccessKind(DecodedInstruction ins)
    {
        if (ins.Mnemonic == "lea")
        {
            return XrefKind.Offset;
        }

        return ins.Native.OpCount > 1 && ins.Native.Op0Kind == OpKind.Memory ? XrefKind.Write : XrefKind.Read;
    }

    private static IEnumerable<ulong> Immediates(Instruction instr)
    {
        for (int op = 0; op < instr.OpCount; op++)
        {
            switch (instr.GetOpKind(op))
            {
                case OpKind.Immediate32:
                case OpKind.Immediate32to64:
                case OpKind.Immediate64:
                    yield return instr.GetImmediate(op);
                    break;
            }
        }
    }
}
