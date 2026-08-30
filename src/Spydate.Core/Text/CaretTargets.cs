namespace Spydate.Core.Text;

public enum CaretTargetKind
{
    /// <summary>Nothing here can be named.</summary>
    None,
    /// <summary>A stack slot of the function being read.</summary>
    StackSlot,
    /// <summary>An address: a function, a global, a label, or the line itself.</summary>
    Address,
}

/// <summary>What a naming command should act on.</summary>
public readonly record struct CaretTarget(CaretTargetKind Kind, string? Slot, ulong Address)
{
    public static CaretTarget None { get; } = new(CaretTargetKind.None, null, 0);

    public static CaretTarget ForSlot(string slot) => new(CaretTargetKind.StackSlot, slot, 0);

    public static CaretTarget At(ulong address) => new(CaretTargetKind.Address, null, address);

    public override string ToString() => Kind switch
    {
        CaretTargetKind.StackSlot => Slot ?? "slot",
        CaretTargetKind.Address => $"0x{Address:X}",
        _ => "nothing",
    };
}

/// <summary>
/// Deciding what the caret is pointing at, which is the whole of "rename this". Kept here, away from the
/// window it is driven from, because the ordering is the part that has to be right and the part worth
/// testing: a stack slot before a symbol, a symbol before the line it sits on, the line before the
/// document as a whole.
/// </summary>
public static class CaretTargets
{
    /// <summary>Prefixes the decompiler's frame analysis gives to stack slots.</summary>
    private static readonly string[] SlotPrefixes = { "local_", "arg_" };

    public static bool IsGeneratedSlotName(string? word)
        => word is not null && SlotPrefixes.Any(p => word.StartsWith(p, StringComparison.Ordinal) && word.Length > p.Length);

    /// <param name="caretWord">Identifier under the caret, if any.</param>
    /// <param name="caretLineAddress">Address the caret's line is about, if it states one.</param>
    /// <param name="documentAddress">What the document as a whole is about — usually a function's entry.</param>
    /// <param name="slotForName">Maps a name the user chose back to the slot it renamed, if it is one.</param>
    /// <param name="addressForSymbol">Maps a name to the address it belongs to, if the symbol table knows it.</param>
    public static CaretTarget Resolve(
        string? caretWord,
        ulong? caretLineAddress,
        ulong? documentAddress,
        Func<string, string?>? slotForName = null,
        Func<string, ulong?>? addressForSymbol = null)
    {
        if (!string.IsNullOrEmpty(caretWord))
        {
            // A slot first: `arg_0` is not an address, and a name given to one is not a symbol.
            if (IsGeneratedSlotName(caretWord))
            {
                return CaretTarget.ForSlot(caretWord);
            }

            if (slotForName?.Invoke(caretWord) is { } named)
            {
                return CaretTarget.ForSlot(named);
            }

            if (AddressText.FromGeneratedName(caretWord) is { } generated)
            {
                return CaretTarget.At(generated);
            }

            if (addressForSymbol?.Invoke(caretWord) is { } symbol)
            {
                return CaretTarget.At(symbol);
            }
        }

        if (caretLineAddress is { } line)
        {
            return CaretTarget.At(line);
        }

        return documentAddress is { } document ? CaretTarget.At(document) : CaretTarget.None;
    }
}
