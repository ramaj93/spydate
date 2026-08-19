namespace Spydate.Core.Symbols;

public enum SymbolKind
{
    Unknown,
    EntryPoint,
    Export,
    /// <summary>An IAT slot; the name is <c>module!function</c>.</summary>
    Import,
    Function,
    Label,
    Data,
    Section,
}

/// <summary>A named virtual address.</summary>
public sealed record Symbol(ulong Va, string Name, SymbolKind Kind, uint Size = 0)
{
    public override string ToString() => $"{Name} @ 0x{Va:X}";
}
