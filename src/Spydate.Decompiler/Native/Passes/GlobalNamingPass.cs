using System.Globalization;
using Spydate.Core.PE;
using Spydate.Core.Strings;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Native.IR;
using Spydate.Disassembly;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>
/// What the image holds at an address, for naming the constants that turn up in lifted code. Built from
/// a <see cref="BinaryAnalysis"/> so it sees the same symbols, functions and scanned strings the rest of
/// the tool shows.
/// </summary>
public sealed class GlobalNames
{
    private readonly PeImage _image;
    private readonly SymbolTable? _symbols;
    private readonly Lazy<StringIndex> _strings;
    private readonly Func<ulong, bool> _isFunction;

    /// <summary>The string index is a function so scanning is deferred until a literal is actually looked up.</summary>
    public GlobalNames(PeImage image, SymbolTable? symbols, Func<StringIndex>? strings = null, Func<ulong, bool>? isFunction = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
        _symbols = symbols;
        _strings = new Lazy<StringIndex>(strings ?? (() => StringIndex.Empty));
        _isFunction = isFunction ?? (_ => false);
    }

    /// <summary>Naming backed by a whole analysis: symbols, discovered functions and scanned strings.</summary>
    public static GlobalNames For(BinaryAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return new GlobalNames(analysis.Image, analysis.Symbols, () => analysis.Strings, va => analysis.TryGetFunction(va, out _));
    }

    /// <summary>The section holding <paramref name="va"/>, or null when the address is outside the image.</summary>
    public SectionHeader? SectionAt(ulong va) => _image.SectionFromVa(va);

    /// <summary>
    /// A string that starts exactly at <paramref name="va"/> — an interior pointer does not count, and
    /// neither does a hit inside code: x64 prologue bytes spell "@SVWH", which is not a message.
    /// </summary>
    public FoundString? StringStartingAt(ulong va)
        => SectionAt(va) is { IsExecutable: false } && _strings.Value.Find(va) is { } found && found.Va == va
            ? found
            : null;

    /// <summary>
    /// What to call the object at <paramref name="va"/>: the symbol if one is known, otherwise a name
    /// built from the address the way a disassembler labels anonymous data.
    /// </summary>
    public string NameFor(ulong va)
    {
        if (_symbols is not null && _symbols.TryGet(va, out var symbol) && symbol.Kind != SymbolKind.Section)
        {
            return symbol.Name;
        }

        string hex = va.ToString("X", CultureInfo.InvariantCulture);
        return IsFunction(va) ? $"sub_{hex}" : $"data_{hex}";
    }

    /// <summary>Whether the address is code that is entered, rather than data that is read.</summary>
    public bool IsFunction(ulong va)
        => _isFunction(va) || (_symbols is not null && _symbols.TryGet(va, out var s) && s.Kind == SymbolKind.Function);

    /// <summary>True when an immediate is worth reading as a pointer rather than a number.</summary>
    public bool IsNamedTarget(ulong va, int bits)
    {
        if (va == 0 || bits != _image.Bitness || SectionAt(va) is not { } section)
        {
            return false;
        }

        // Inside code, only an address the analysis knows is a function is worth a name: everything else
        // would be a number that happens to fall in .text.
        return !section.IsExecutable || _isFunction(va)
            || (_symbols is not null && _symbols.TryGet(va, out var s) && s.Kind != SymbolKind.Section);
    }
}

/// <summary>
/// Replaces absolute addresses with names: <c>*(uint32_t*)(0x14003A100)</c> becomes
/// <c>data_14003A100</c>, and an immediate that points at scanned text becomes the text itself. Runs
/// before copy propagation so a literal reaches the call that uses it.
/// </summary>
public sealed class GlobalNamingPass : IIrPass
{
    private readonly GlobalNames _names;

    public GlobalNamingPass(GlobalNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = names;
    }

    public string Name => "global-naming";

    public void Run(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        foreach (var block in function.Blocks)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                var statement = IrRewriter.RewriteStmt(block.Statements[i], e => Map(e, function.Bitness));
                block.Statements[i] = AsGlobalWrite(statement) ?? statement;
            }
        }
    }

    /// <summary>
    /// A store to a named address is an assignment to that name: <c>*(uint64_t*)(&amp;data_X) = v</c> is
    /// <c>data_X = v</c>. The address has already been named when this runs.
    /// </summary>
    private static IrStmt? AsGlobalWrite(IrStmt statement) => statement switch
    {
        IrStore { Address: IrAddressOf { Target: IrGlobal g } } s => new IrAssign(new IrGlobal(g.Name, g.Va, s.Bits), s.Value) { Va = s.Va },
        IrStore { Address: IrSymbol sym } s => new IrAssign(new IrGlobal(sym.Name, sym.Va, s.Bits), s.Value) { Va = s.Va },
        _ => null,
    };

    private IrExpr Map(IrExpr expr, int bitness)
    {
        switch (expr)
        {
            // The object at a fixed address: name it, and let the name stand in for the dereference.
            case IrMem { Address: IrConst c } m when _names.IsNamedTarget((ulong)c.Value, c.Bits):
                return new IrGlobal(_names.NameFor((ulong)c.Value), (ulong)c.Value, m.Bits);

            case IrMem { Address: IrSymbol s } m:
                return new IrGlobal(s.Name, s.Va, m.Bits);

            // Rewriting is bottom-up, so the address inside a dereference is already named.
            case IrMem { Address: IrAddressOf { Target: IrGlobal g } } m:
                return new IrGlobal(g.Name, g.Va, m.Bits);

            // A pointer-sized immediate that lands in the image is an address, not a number.
            case IrConst c when _names.IsNamedTarget((ulong)c.Value, c.Bits):
                return Pointer((ulong)c.Value, bitness);

            case IrSymbol s when _names.StringStartingAt(s.Va) is { } text:
                return Literal(text, bitness);

            default:
                return expr;
        }
    }

    private IrExpr Pointer(ulong va, int bitness)
    {
        if (_names.StringStartingAt(va) is { } text)
        {
            return Literal(text, bitness);
        }

        // A function's name already means its address in C; anything else needs the & to say so.
        string name = _names.NameFor(va);
        return _names.IsFunction(va)
            ? new IrSymbol(name, va, bitness)
            : new IrAddressOf(new IrGlobal(name, va, 0), bitness);
    }

    private static IrExpr Literal(FoundString text, int bitness)
        => new IrStringLiteral(text.Text, text.Va ?? 0, text.Encoding == StringEncodingKind.Utf16, bitness);
}
