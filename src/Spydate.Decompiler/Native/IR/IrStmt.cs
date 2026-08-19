namespace Spydate.Decompiler.Native.IR;

/// <summary>Base of all IR statements. Each statement remembers the VA of the instruction it came from.</summary>
public abstract record IrStmt
{
    public ulong Va { get; init; }
}

public sealed record IrAssign(IrExpr Dst, IrExpr Src) : IrStmt
{
    public override string ToString() => $"{Dst} = {Src};";
}

public sealed record IrStore(IrExpr Address, IrExpr Value, int Bits) : IrStmt
{
    public override string ToString() => $"*({IrTypes.NameFor(Bits)}*)({Address}) = {Value};";
}

public sealed record IrCallStmt(IrCall Call, IrExpr? Result) : IrStmt
{
    public override string ToString() => Result is null ? $"{Call};" : $"{Result} = {Call};";
}

public sealed record IrReturn(IrExpr? Value) : IrStmt
{
    public override string ToString() => Value is null ? "return;" : $"return {Value};";
}

public sealed record IrGoto(ulong TargetVa) : IrStmt
{
    public override string ToString() => $"goto loc_{TargetVa:X};";
}

public sealed record IrBranch(IrExpr Condition, ulong TargetVa, ulong FallthroughVa) : IrStmt
{
    public override string ToString() => $"if ({Condition}) goto loc_{TargetVa:X};";
}

/// <summary>
/// Dispatch through a recovered switch table: <paramref name="Targets"/> is indexed by
/// <paramref name="Value"/>, so entry <c>i</c> is the body of <c>case i</c>.
/// </summary>
public sealed record IrSwitch(IrExpr Value, IReadOnlyList<ulong> Targets) : IrStmt
{
    public override string ToString() => $"switch ({Value}) // {Targets.Count} case(s)";
}

public sealed record IrLabel(ulong LabelVa) : IrStmt
{
    public override string ToString() => $"loc_{LabelVa:X}:";
}

/// <summary>Unsupported instruction kept verbatim.</summary>
public sealed record IrAsm(string Text) : IrStmt
{
    public override string ToString() => $"__asm {{ {Text} }}";
}

public sealed record IrComment(string Text) : IrStmt
{
    public override string ToString() => $"// {Text}";
}

public sealed record IrNop : IrStmt
{
    public override string ToString() => string.Empty;
}

/// <summary>A basic block of IR statements.</summary>
public sealed class IrBlock
{
    public IrBlock(ulong startVa)
    {
        StartVa = startVa;
    }

    public ulong StartVa { get; }

    public List<IrStmt> Statements { get; } = new();

    public List<ulong> Successors { get; } = new();

    public List<ulong> Predecessors { get; } = new();

    public override string ToString() => $"irblock 0x{StartVa:X} ({Statements.Count} stmts)";
}

/// <summary>Lifted function.</summary>
public sealed class IrFunction
{
    public IrFunction(ulong entryVa, string name, int bitness)
    {
        EntryVa = entryVa;
        Name = name;
        Bitness = bitness;
    }

    public ulong EntryVa { get; }

    public string Name { get; }

    public int Bitness { get; }

    public List<IrBlock> Blocks { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Whether the function produces a result. False when nothing ever writes the accumulator, which
    /// means <c>ret</c> was returning whatever the caller happened to leave there (see ReturnValuePass).
    /// </summary>
    public bool ReturnsValue { get; set; } = true;

    /// <summary>
    /// Leading arguments this function is handed in registers: on x86, <c>ecx</c> for <c>__thiscall</c>
    /// and <c>ecx</c>, <c>edx</c> for <c>__fastcall</c>. They are declared under the register's own name,
    /// which is what the body already calls them.
    /// </summary>
    public int RegisterParameters { get; set; }

    /// <summary>Set of VAs that are targets of a goto/branch (need labels).</summary>
    public HashSet<ulong> LabelTargets { get; } = new();

    /// <summary>Locals discovered by passes, keyed by name.</summary>
    public SortedDictionary<string, IrLocal> Locals { get; } = new(StringComparer.Ordinal);

    public IEnumerable<IrStmt> AllStatements => Blocks.SelectMany(b => b.Statements);
}
