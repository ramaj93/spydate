using Spydate.Core.Symbols;
using Spydate.Core.Text;
using Spydate.Disassembly;

namespace Spydate.Mcp.Session;

/// <summary>
/// What a target resolved to, or why it did not. A failure carries near misses, because the usual
/// reason an agent gets a name wrong is a typo or half a symbol, and "not found" alone makes it
/// guess again in the dark.
/// </summary>
public readonly record struct TargetResult(ulong Va, bool Found, string? Problem)
{
    public static TargetResult Of(ulong va) => new(va, true, null);

    public static TargetResult Failed(string problem) => new(0, false, problem);
}

/// <summary>
/// Turns what an agent writes into an address. Every address-taking tool goes through this, so all
/// of them accept the same four forms and fail the same way.
/// </summary>
public static class Targets
{
    private const int Suggestions = 3;

    /// <summary>
    /// Resolves <c>0x140001000</c>, a bare hex address, a generated name like <c>sub_140001000</c>,
    /// a symbol, or a name the user or an agent gave an address.
    /// </summary>
    public static TargetResult Resolve(BinarySession session, string? target)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(target))
        {
            return TargetResult.Failed("no target given");
        }

        string text = target.Trim();

        // A generated name states its own address, so it needs no lookup: sub_140001000 is 0x140001000.
        if (AddressText.FromGeneratedName(text) is { } generated)
        {
            return TargetResult.Of(generated);
        }

        if (AddressText.ParseHex(text) is { } parsed)
        {
            return TargetResult.Of(parsed);
        }

        if (session.Analysis is { } analysis)
        {
            if (analysis.Symbols.GetByName(text) is { } symbol)
            {
                return TargetResult.Of(symbol.Va);
            }

            foreach (var (va, annotation) in analysis.Annotations.Snapshot())
            {
                if (string.Equals(annotation.Name, text, StringComparison.Ordinal))
                {
                    return TargetResult.Of(va);
                }
            }
        }

        return TargetResult.Failed($"'{text}' is not an address or a known name{Near(session, text)}");
    }

    /// <summary>
    /// Resolves a target and, when it lands inside a function rather than on its entry, moves to the
    /// entry.
    ///
    /// This is not a convenience. <c>GetOrDiscoverFunction</c> will happily discover a "function" at
    /// any address it is handed, cache it, and name it — and an agent is constantly handed addresses
    /// that are not entries: call sites from a reference list, hits from a search, an instruction it
    /// just read. Passing those straight through would mint overlapping phantom functions and junk
    /// symbols into the analysis. The window never does this because it only ever opens functions it
    /// already found; this is the guard that replaces that.
    /// </summary>
    public static (TargetResult Target, Function? Function, ulong? Inside) ResolveFunction(BinarySession session, string? target)
    {
        var result = Resolve(session, target);
        if (!result.Found || session.Analysis is not { } analysis)
        {
            return (result, null, null);
        }

        if (analysis.TryGetFunction(result.Va, out var exact))
        {
            return (result, exact, null);
        }

        if (analysis.FunctionContaining(result.Va) is { } containing)
        {
            // The address asked about is carried back, not discarded: saying "0x140001008 is inside
            // this function" about 0x140001008 tells the agent nothing it did not just say.
            return (TargetResult.Of(containing.EntryVa), containing, result.Va);
        }

        return (TargetResult.Failed($"0x{result.Va:X} is not in any discovered function"), null, null);
    }

    /// <summary>Names close enough to the one that missed to be worth offering.</summary>
    private static string Near(BinarySession session, string text)
    {
        if (session.Analysis is not { } analysis || text.Length < 3)
        {
            return string.Empty;
        }

        var close = analysis.Symbols.All
            .Where(s => s.Kind != SymbolKind.Section && s.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(Suggestions)
            .ToList();

        return close.Count == 0 ? string.Empty : $". Did you mean: {string.Join(", ", close)}?";
    }
}
