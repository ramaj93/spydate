using System.ComponentModel;
using System.Globalization;
using System.Reflection.Metadata;
using ModelContextProtocol.Server;
using Spydate.Core.Symbols;
using Spydate.Decompiler.Managed;
using Spydate.Disassembly;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>
/// Finding something worth reading. This is where reverse engineering actually starts — not with a
/// function, but with a question like "who calls CreateFileW" or "what is still unnamed and used a
/// lot" — so these tools matter more to the loop than the ones that read code.
/// </summary>
[McpServerToolType]
public sealed class NavigationTools
{
    private const int DefaultLimit = 40;
    private const int MaxLimit = 200;
    private const int NameColumn = 48;

    private readonly SessionStore _store;

    public NavigationTools(SessionStore store) => _store = store;

    [McpServerTool(Name = "list_functions")]
    [Description("List discovered functions. named=\"unnamed\" with sort=\"refs\" is the worklist: what is still called sub_* , most-referenced first.")]
    public string ListFunctions(
        [Description("\"unnamed\" (still sub_*), \"named\", or \"all\". Default \"all\".")] string named = "all",
        [Description("\"refs\", \"size\" or \"address\". Default \"address\".")] string sort = "address",
        [Description("Only functions referenced at least this many times.")] int minRefs = 0,
        [Description("Only functions whose name contains this.")] string? nameContains = null,
        [Description("Continue after this address (from the previous page's next: line). Address order only.")] string? afterVa = null,
        [Description("Rows to return, at most 200.")] int limit = DefaultLimit)
    {
        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        var all = session.Functions;

        var matching = all.Where(f => Matches(f, named, minRefs, nameContains, analysis)).ToList();
        var ordered = sort switch
        {
            "refs" => matching.OrderByDescending(f => analysis.Xrefs.CountTo(f.EntryVa)).ThenBy(f => f.EntryVa).ToList(),
            "size" => matching.OrderByDescending(f => f.CodeSize).ThenBy(f => f.EntryVa).ToList(),
            _ => matching,
        };

        // A cursor, not an offset. The function set grows while a session runs — resolving an
        // address or asking for a signature can discover one — and an offset walk would silently
        // re-show and skip entries as everything below the cursor shifted.
        bool byAddress = sort is not ("refs" or "size");
        if (byAddress && afterVa is not null && Targets.Resolve(session, afterVa) is { Found: true } cursor)
        {
            ordered = ordered.Where(f => f.EntryVa > cursor.Va).ToList();
        }

        var page = ordered.Take(limit).ToList();
        var table = new TextTable(("address", 18), ("name", NameColumn), ("size", 8), ("blocks", 6), ("refs", 5));
        foreach (var function in page)
        {
            table.Add(
                $"0x{function.EntryVa:X}",
                analysis.NameFor(function.EntryVa),
                $"0x{function.CodeSize:X}",
                function.Blocks.Count.ToString(CultureInfo.InvariantCulture),
                analysis.Xrefs.CountTo(function.EntryVa).ToString(CultureInfo.InvariantCulture));
        }

        string? next = page.Count == limit && ordered.Count > limit
            ? byAddress ? $"list_functions(after_va=\"0x{page[^1].EntryVa:X}\")" : "narrow the filter; ranked lists do not page"
            : null;

        string filters = $"named={named}, sort={sort}"
                         + (minRefs > 0 ? $", min_refs={minRefs}" : string.Empty)
                         + (nameContains is not null ? $", name_contains={nameContains}" : string.Empty);

        return Budget.Clip(Stub(session)
                           + table.Render("no function matched") + '\n'
                           + TextTable.Meta(page.Count, ordered.Count, all.Count, "functions", next, filters)
                           + Partial(session));
    }

    [McpServerTool(Name = "find_symbol")]
    [Description("Search every known name - functions, imports, exports, data and anything renamed - for a substring. In a .NET assembly, searches types and members instead. Use this when you know what something is called but not where it is.")]
    public string FindSymbol(
        [Description("Substring to look for, case-insensitive. Empty lists everything of the chosen kind.")] string query = "",
        [Description("\"any\", \"function\", \"import\", \"export\" or \"data\". Default \"any\".")] string kind = "any",
        [Description("Rows to return, at most 200.")] int limit = DefaultLimit)
    {
        if (_store.Current is not { } open)
        {
            return SessionTools.NothingOpen;
        }

        // Managed first when there is a managed reading. The native symbol table of an IL-only
        // assembly holds one import and the loader stub, so answering from it would be answering a
        // question about the file that nobody asked.
        if (open.ManagedIndex is { } managed && open.Image.ClrHeader?.IsILOnly == true)
        {
            return FindManaged(managed, open.Managed!.PInvokes, query, limit);
        }

        if (open.Analysis is not { } analysis)
        {
            return $"there is no symbol table: {open.Image.Machine} is not a machine this disassembles";
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        var wanted = KindOf(kind);

        var symbols = analysis.Symbols.All.ToList();
        var matching = symbols
            .Where(s => s.Kind != SymbolKind.Section)
            .Where(s => wanted is null || s.Kind == wanted)
            .Where(s => query.Length == 0 || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name.Length)           // an exact-ish match is shorter than one that merely contains it
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var table = new TextTable(("address", 18), ("kind", 9), ("name", 64), ("refs", 5));
        foreach (var symbol in matching.Take(limit))
        {
            table.Add(
                $"0x{symbol.Va:X}",
                symbol.Kind.ToString().ToLowerInvariant(),
                symbol.Name,
                analysis.Xrefs.CountTo(symbol.Va).ToString(CultureInfo.InvariantCulture));
        }

        return Budget.Clip(table.Render($"nothing is called anything like '{query}'") + '\n'
                           + TextTable.Meta(Math.Min(limit, matching.Count), matching.Count, symbols.Count, "symbols", null, $"kind={kind}"));
    }

    [McpServerTool(Name = "list_imports")]
    [Description("What the binary calls from elsewhere: imported functions with the IAT slot address to pass to xrefs, or in .NET the members it calls in other assemblies.")]
    public string ListImports(
        [Description("Only modules whose name contains this.")] string? module = null,
        [Description("Only functions whose name contains this.")] string? filter = null,
        [Description("\"refs\" (most used first) or \"name\". Default \"refs\".")] string sort = "refs",
        [Description("Rows to skip, for paging.")] int offset = 0,
        [Description("Rows to return, at most 200.")] int limit = 60)
    {
        if (_store.Current is not { } open)
        {
            return SessionTools.NothingOpen;
        }

        // An IL-only assembly's import directory holds one entry, for the loader. Answering from it
        // would be truthfully describing the wrong table: what the program uses is in its MemberRefs.
        if (open.Managed is not null && open.Image.ClrHeader?.IsILOnly == true)
        {
            return ManagedImports(open, module, filter, sort, Math.Max(0, offset), Math.Clamp(limit, 1, MaxLimit));
        }

        if (open is not { Analysis: { } analysis } session)
        {
            return $"there is no import table to read: {open.Image.Machine} is not a machine this disassembles";
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);

        var image = session.Image;
        var rows = image.Imports.Concat(image.DelayImports)
            .Where(m => module is null || m.Name.Contains(module, StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.Functions.Select(f => (Module: m, Function: f)))
            .Where(e => filter is null || e.Function.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.Module, e.Function, Va: image.RvaToVa(e.Function.IatRva)))
            .ToList();

        int total = image.Imports.Sum(m => m.Functions.Count) + image.DelayImports.Sum(m => m.Functions.Count);
        var ordered = sort == "name"
            ? rows.OrderBy(r => r.Module.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Function.DisplayName, StringComparer.Ordinal).ToList()
            : rows.OrderByDescending(r => analysis.Xrefs.CountTo(r.Va)).ThenBy(r => r.Function.DisplayName, StringComparer.Ordinal).ToList();

        var table = new TextTable(("iat_va", 18), ("module", 34), ("function", 40), ("takes", 22), ("refs", 5));
        foreach (var row in ordered.Skip(offset).Take(limit))
        {
            table.Add(
                $"0x{row.Va:X}",
                row.Module.Name,
                row.Function.DisplayName + (row.Module.IsDelayLoad ? " (delay)" : string.Empty),
                Takes(analysis.SignatureFor(row.Va)),
                analysis.Xrefs.CountTo(row.Va).ToString(CultureInfo.InvariantCulture));
        }

        int returned = Math.Max(0, Math.Min(limit, ordered.Count - offset));
        string? next = offset + returned < ordered.Count ? $"list_imports(offset={offset + returned})" : null;

        return Budget.Clip(table.Render("no import matched") + '\n'
                           + TextTable.Meta(returned, ordered.Count, total, "imports", next, $"sort={sort}")
                           + Unresolved(session));
    }

    [McpServerTool(Name = "xrefs")]
    [Description("Every place that refers to an address, or everything one address refers to. This answers \"who calls this import\" and \"who reads this global\". In .NET: a type, a member, or one in another assembly like System.IO.File::Delete.")]
    public string Xrefs(
        [Description("Address, sub_XXXX, or a name. For an import use its IAT slot address from list_imports.")] string target,
        [Description("\"to\" (who refers to it) or \"from\" (what it refers to). Default \"to\".")] string direction = "to",
        [Description("\"code\", \"data\" or \"all\". Default \"all\".")] string kind = "all",
        [Description("Rows to skip, for paging.")] int offset = 0,
        [Description("Rows to return, at most 200.")] int limit = DefaultLimit)
    {
        if (_store.Current is not { } open)
        {
            return SessionTools.NothingOpen;
        }

        // Resolved before the scan is paid for, not after. Walking every method body is the most
        // expensive thing in this server, and a mixed-mode assembly can be asked about an address -
        // for which the answer is native and the walk would have bought nothing.
        var managed = ManagedTargets.Resolve(open, target);
        if (managed.Found)
        {
            return ManagedXrefs(open, managed, direction, kind, offset, Math.Clamp(limit, 1, MaxLimit));
        }

        if (open is not { Analysis: { } analysis } session)
        {
            // Asked before giving up, not only on the way past a native miss: an ARM64 assembly has
            // no analysis to fall through, and "who calls File.Delete" is still a fair question.
            return open.References?.Import(target) is { } only
                ? Outside(open, only, offset, Math.Clamp(limit, 1, MaxLimit))
                : managed.Problem ?? $"there is nothing to cross-reference: {open.Image.Machine} is not a machine this disassembles";
        }

        var resolved = Targets.Resolve(session, target);
        if (!resolved.Found)
        {
            // Nothing here is an address or a name in this image, so the last thing it can be is a
            // member of another assembly: "who calls File.Delete" is the managed question that the
            // native side answers with list_imports and an IAT slot.
            if (open.References?.Import(target) is { } import)
            {
                return Outside(open, import, offset, Math.Clamp(limit, 1, MaxLimit));
            }

            return managed.Problem ?? resolved.Problem!;
        }

        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(0, offset);

        bool to = direction != "from";
        var all = (to ? analysis.Xrefs.To(resolved.Va) : analysis.Xrefs.From(resolved.Va))
            .Where(x => kind switch { "code" => x.IsCode, "data" => x.IsData, _ => true })
            .OrderBy(x => to ? x.FromVa : x.ToVa)
            .ToList();

        // Paged before the owning function is resolved for any of them. FunctionContaining is a
        // linear scan over every function, so doing it per reference on a busy import would be
        // millions of comparisons for forty rows of output.
        var page = all.Skip(offset).Take(limit).ToList();

        var table = new TextTable(("site", 18), ("kind", 13), ("in", 40), ("instruction", 46));
        foreach (var xref in page)
        {
            ulong site = to ? xref.FromVa : xref.ToVa;
            var owner = analysis.FunctionContaining(site);
            table.Add(
                $"0x{site:X}",
                xref.Kind.ToString().ToLowerInvariant(),
                owner is null ? "-" : $"{analysis.NameFor(owner.EntryVa)}+0x{site - owner.EntryVa:X}",
                InstructionAt(analysis, owner, site));
        }

        string what = $"0x{resolved.Va:X}" + (analysis.NameFor(resolved.Va) is { } n && n.Length > 0 ? $" ({n})" : string.Empty);
        string? next = offset + page.Count < all.Count ? $"xrefs(target=\"{target}\", offset={offset + page.Count})" : null;

        return Budget.Clip($"references {direction} {what}\n"
                           + table.Render("nothing refers to it") + '\n'
                           + TextTable.Meta(page.Count, all.Count, all.Count, "references", next, $"kind={kind}"));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Who refers to a type or member of this assembly, or what one of its methods refers to.
    ///
    /// Every row names its site as <c>Namespace.Type::Member</c>, which is deliberately the same
    /// form <c>read_function</c> takes: the point of an answer here is the call it leads to, and a
    /// row that has to be translated before it can be used is a row that will be translated wrongly.
    /// </summary>
    private static string ManagedXrefs(BinarySession session, ManagedTarget target, string direction, string kind, int offset, int limit)
    {
        var references = session.References!;
        offset = Math.Max(0, offset);
        bool to = direction != "from";

        if (!to)
        {
            if (target.Member is not { Handle.Kind: HandleKind.MethodDefinition } method)
            {
                return $"\"from\" reads what a method's own code refers to, and {target.Describe()} is "
                       + (target.Member is null ? "a type" : "not a method") + ". Name a method.";
            }

            var edges = references.From((MethodDefinitionHandle)method.Handle)
                .Where(e => Wanted(e.Site.Kind, kind))
                .ToList();

            var outward = new TextTable(("at", 7), ("kind", 6), ("refers to", 84));
            foreach (var (entity, site) in edges.Skip(offset).Take(limit))
            {
                outward.Add($"IL_{site.Offset:X4}", site.Kind.ToString().ToLowerInvariant(), references.NameOf(entity));
            }

            return Budget.Clip($"what {target.Describe()} refers to\n"
                               + outward.Render("it refers to nothing") + '\n'
                               + Page(edges.Count, offset, limit, target.Key(), direction, kind));
        }

        var handle = target.Member?.Handle ?? target.Type!.Handle;
        var sites = references.To(handle).Where(s => Wanted(s.Kind, kind)).ToList();

        var table = new TextTable(("in", 84), ("at", 7), ("kind", 6));
        foreach (var site in sites.Skip(offset).Take(limit))
        {
            table.Add(references.NameOf(site.From), $"IL_{site.Offset:X4}", site.Kind.ToString().ToLowerInvariant());
        }

        // An empty answer about a type is worth a sentence, because it is usually not what it looks
        // like: a type is named by its members' signatures far more often than by an instruction,
        // and none of that is in the IL to be found.
        string nothing = target.Member is null
            ? "no instruction names this type. Its members are referred to individually - ask about one of those"
            : "nothing refers to it";

        return Budget.Clip($"references to {target.Describe()}\n"
                           + table.Render(nothing) + '\n'
                           + Page(sites.Count, offset, limit, target.Key(), direction, kind));
    }

    /// <summary>
    /// What the assembly calls in other assemblies, which is its real import list.
    ///
    /// Nothing in the PE import directory says any of this. A managed binary imports one function —
    /// the loader's — and everything it actually uses is a MemberRef reached only by reading the
    /// code, so this column of counts is the product of the IL walk rather than of a table lookup.
    /// </summary>
    private static string ManagedImports(BinarySession session, string? module, string? filter, string sort, int offset, int limit)
    {
        var references = session.References!;
        var matching = references.Imports
            .Where(i => module is null || i.Assembly.Contains(module, StringComparison.OrdinalIgnoreCase))
            .Where(i => filter is null || i.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sort == "name")
        {
            matching = matching
                .OrderBy(i => i.Assembly, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.FullName, StringComparer.Ordinal)
                .ToList();
        }

        var table = new TextTable(("assembly", 28), ("member", 72), ("refs", 5));
        foreach (var import in matching.Skip(offset).Take(limit))
        {
            table.Add(
                import.Assembly.Length == 0 ? "-" : import.Assembly,
                import.FullName,
                import.Count.ToString(CultureInfo.InvariantCulture));
        }

        int returned = Math.Max(0, Math.Min(limit, matching.Count - offset));
        string? next = offset + returned < matching.Count ? $"list_imports(offset={offset + returned})" : null;

        return Budget.Clip(table.Render("it calls nothing outside itself") + '\n'
                           + TextTable.Meta(returned, matching.Count, references.Imports.Count, "members", next, $"sort={sort}")
                           + "\n-- pass a member to xrefs to see every call to it --");
    }

    /// <summary>Where a member of another assembly is used. Always inward: its own code is not here.</summary>
    private static string Outside(BinarySession session, ManagedImport import, int offset, int limit)
    {
        var references = session.References!;
        offset = Math.Max(0, offset);

        var table = new TextTable(("in", 84), ("at", 7), ("kind", 6));
        foreach (var site in import.Sites.Skip(offset).Take(limit))
        {
            table.Add(references.NameOf(site.From), $"IL_{site.Offset:X4}", site.Kind.ToString().ToLowerInvariant());
        }

        string from = import.Assembly.Length > 0 ? $", from {import.Assembly}" : string.Empty;
        return Budget.Clip($"references to {import.FullName}{from} - it is not defined in this assembly\n"
                           + table.Render("nothing refers to it") + '\n'
                           + Page(import.Sites.Count, offset, limit, import.FullName, "to", "all"));
    }

    private static string Page(int total, int offset, int limit, string target, string direction, string kind)
    {
        int returned = Math.Max(0, Math.Min(limit, total - offset));
        string? next = offset + returned < total
            ? $"xrefs(target=\"{target}\", direction=\"{direction}\", offset={offset + returned})"
            : null;

        return TextTable.Meta(returned, total, total, "references", next, $"kind={kind}");
    }

    /// <summary>
    /// Whether a managed reference passes the native filter words.
    ///
    /// <c>code</c> and <c>data</c> are what every other binary is asked in, so they keep meaning
    /// something here: calling and constructing are code, and touching a field, naming a type or
    /// loading a literal are data. The managed kind names are accepted too, for an agent that has
    /// read one out of a previous answer's own column.
    /// </summary>
    private static bool Wanted(ManagedRefKind actual, string kind) => kind switch
    {
        "all" => true,
        "code" => actual is ManagedRefKind.Call or ManagedRefKind.New,
        "data" => actual is ManagedRefKind.Read or ManagedRefKind.Write or ManagedRefKind.Type or ManagedRefKind.String,
        _ => string.Equals(actual.ToString(), kind, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Type and member names matching a substring — the whole managed listing surface, since there
    /// is no separate one.
    ///
    /// There is deliberately no <c>list_types</c>. Every tool's schema is sent on every turn of
    /// every conversation, including the ones that never open a .NET file, and a tool that only
    /// listed types would be paid for by all of them to do what an empty query does here. Reading a
    /// type's members is <c>read_function</c>, which prints the type: in managed code the listing
    /// and the source are the same answer.
    ///
    /// A member is matched on its own name and on its declaring type's, so a query naming a type
    /// returns that type and everything in it. Each row carries the type it belongs to, because a
    /// bare method name cannot be handed back to any other tool.
    /// </summary>
    private static string FindManaged(ManagedIndex index, IReadOnlyList<ManagedPInvoke> pinvokes, string query, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        var hits = new List<(string Kind, string Name)>();
        foreach (var type in index.Types)
        {
            bool wholeType = query.Length == 0 || type.FullName.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (wholeType)
            {
                hits.Add((type.Kind.ToString().ToLowerInvariant(), type.FullName));
            }

            foreach (var member in type.Members)
            {
                if (!wholeType && !member.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                hits.Add((member.Kind.ToString().ToLowerInvariant(), $"{type.FullName}::{member.Signature}"));
            }
        }

        // P/Invokes match on the native side too — the library or the entry point — so an agent
        // chasing IsDebuggerPresent or ntdll finds the managed method that calls it, which is the one
        // thing the metadata knows and nothing else here surfaces. The native target is shown beside
        // the managed method, which is what a breakpoint or read_function then takes.
        foreach (var pinvoke in pinvokes)
        {
            if (query.Length == 0
                || pinvoke.Method.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pinvoke.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pinvoke.EntryPoint.Contains(query, StringComparison.OrdinalIgnoreCase)
                || pinvoke.Library.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(("pinvoke", $"{pinvoke.Managed} -> {pinvoke.Native}"));
            }
        }

        // Shortest first, as in the native search: a name that merely contains the query is longer
        // than one that nearly is the query, and that is a good enough proxy for relevance.
        var ordered = hits.OrderBy(h => h.Name.Length).ThenBy(h => h.Name, StringComparer.Ordinal).ToList();

        var table = new TextTable(("kind", 11), ("name", 84));
        foreach (var (kind, name) in ordered.Take(limit))
        {
            table.Add(kind, name);
        }

        return Budget.Clip(table.Render($"nothing is called anything like '{query}'") + '\n'
                           + TextTable.Meta(Math.Min(limit, ordered.Count), ordered.Count,
                               index.Types.Count + index.MemberCount, "names", null, "managed metadata"));
    }

    private static bool Matches(Function function, string named, int minRefs, string? nameContains, BinaryAnalysis analysis)
    {
        string name = analysis.NameFor(function.EntryVa);
        bool generated = name.StartsWith("sub_", StringComparison.Ordinal) || name.StartsWith("loc_", StringComparison.Ordinal);

        if (named == "unnamed" && !generated)
        {
            return false;
        }

        if (named == "named" && generated)
        {
            return false;
        }

        if (nameContains is not null && !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return minRefs <= 0 || analysis.Xrefs.CountTo(function.EntryVa) >= minRefs;
    }

    private static SymbolKind? KindOf(string kind) => kind switch
    {
        "function" => SymbolKind.Function,
        "import" => SymbolKind.Import,
        "export" => SymbolKind.Export,
        "data" => SymbolKind.Data,
        _ => null,
    };

    /// <summary>What the DLL on disk said the import takes, or nothing when it could not be read.</summary>
    private static string Takes(CalleeSignature signature)
    {
        if (signature.Source == SignatureSource.None)
        {
            return string.Empty;
        }

        if (!signature.HasArgumentCount)
        {
            return signature.StackCleanupBytes == 0 ? "caller-cleaned" : string.Empty;
        }

        string text = signature.ArgumentCount == 1 ? "1 arg" : $"{signature.ArgumentCount} args";
        var floats = Enumerable.Range(0, signature.ArgumentCount).Where(signature.IsFloat).ToList();
        return floats.Count == 0 ? text : $"{text} (float: {string.Join(",", floats)})";
    }

    private static string InstructionAt(BinaryAnalysis analysis, Function? owner, ulong va)
    {
        if (owner?.Instructions.FirstOrDefault(i => i.Va == va) is not { } instruction)
        {
            return "-";
        }

        string operands = analysis.Disassembler.FormatOperands(instruction.Native);
        return operands.Length == 0 ? instruction.Mnemonic : $"{instruction.Mnemonic} {operands}";
    }

    /// <summary>
    /// Says so when the functions in the list are not the program's.
    ///
    /// Discovery still runs over an IL-only assembly, and deliberately: the ILOnly flag is one bit
    /// in a header that untrusted input is free to lie about, and a binary that hides real native
    /// code behind it is exactly the binary somebody opened this to look at. So the sweep happens
    /// and the answer is labelled, rather than the sweep being skipped on the file's own say-so.
    /// What it finds in genuine IL is x86 shapes in bytes that were never instructions — and an
    /// unnamed-by-references worklist made of those would be a day's work naming nothing.
    /// </summary>
    private static string Stub(BinarySession session)
        => session.Managed is not null && session.Image.ClrHeader?.IsILOnly == true
            ? "-- IL-only .NET assembly: these are x86 shapes found in bytes that hold IL, not this "
              + "program's methods. Its own code is find_symbol() and read_function(view=\"csharp\") --\n"
            : string.Empty;

    /// <summary>Says so when the answer rests on a discovery that did not finish.</summary>
    private static string Partial(BinarySession session)
        => session.Discovery.Complete ? string.Empty : "\n-- discovery was capped, so functions may be missing from this list --";

    /// <summary>
    /// Which DLLs could not be read, when any could not. Without this an agent reads a blank "takes"
    /// as "this function takes nothing", when it means "nobody could tell".
    /// </summary>
    private static string Unresolved(BinarySession session)
    {
        var missing = session.Analysis?.Signatures?.Modules.Where(m => !m.IsUsable).Take(4).ToList();
        return missing is not { Count: > 0 }
            ? string.Empty
            : $"\n-- an empty 'takes' means unknown, not none: {string.Join("; ", missing.Select(m => m.ToString()))} --";
    }
}
