using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;
using Spydate.Core.Strings;
using Spydate.Disassembly;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>Reading code and data: the part of the loop where understanding actually happens.</summary>
[McpServerToolType]
public sealed class CodeTools
{
    private const int DefaultLines = 250;
    private const int MaxLines = 1000;
    private const int MaxInstructions = 256;
    private const int MaxBytes = 1024;

    /// <summary>Callers and callees named in the header before it stops counting them.</summary>
    private const int Listed = 6;

    private readonly SessionStore _store;

    public CodeTools(SessionStore store) => _store = store;

    [McpServerTool(Name = "read_function")]
    [Description("Read a function as pseudo-C (default) or as a disassembly listing, with a header naming its signature, callers, callees and any strings it uses. An address inside a function resolves to the function. In a .NET assembly, reads a type or member as C# or IL.")]
    public string ReadFunction(
        [Description("Address, sub_XXXX, or a name. In .NET: Namespace.Type or Namespace.Type::Member.")] string target,
        [Description("Native \"pseudo_c\" or \"asm\"; .NET \"csharp\" or \"il\". Defaults to the first of the pair.")] string view = "auto",
        [Description("First line of the body to return, for continuing a long function.")] int offset = 0,
        [Description("Body lines to return, at most 1000.")] int maxLines = DefaultLines)
    {
        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        // The managed reading is tried first, and asked for explicitly by the view. Order matters
        // for one case only, and it is the common one: in an IL-only assembly a native name lookup
        // can only fail, so letting it answer first would turn every "read this type" into "not an
        // address or a known name".
        bool wantsManaged = view is "csharp" or "il";
        string? managedProblem = null;
        if (wantsManaged || session.Managed is not null)
        {
            var found = ManagedTargets.Resolve(session, target);
            if (found.Found)
            {
                // Refused rather than quietly substituted. "asm" on a type could only mean the C#
                // instead, and an agent that believes it is reading instructions when it is reading
                // source will draw conclusions about bytes that were never there.
                return view is "pseudo_c" or "asm"
                    ? $"{found.Describe()} is managed code; \"{view}\" is for native code. "
                      + $"Use view=\"csharp\" or view=\"il\"."
                    : ReadManaged(session, found, view, offset, maxLines);
            }

            managedProblem = found.Problem;
            if (wantsManaged)
            {
                return managedProblem ?? NoManaged(session, view);
            }
        }

        if (session.Analysis is not { } analysis)
        {
            return managedProblem ?? $"there is nothing to read: {session.Image.Machine} is not a machine this disassembles";
        }

        var (resolved, function, inside) = Targets.ResolveFunction(session, target);
        if (!resolved.Found || function is null)
        {
            // The managed miss is the better answer whenever there was one to make. An agent that
            // wrote a type name wants to hear which type it meant, not that its text is not hex.
            return managedProblem ?? resolved.Problem ?? $"no function at {target}";
        }

        if (view is "auto")
        {
            view = "pseudo_c";
        }

        string body;
        try
        {
            body = view == "asm"
                ? AsmListing.ForFunction(analysis, function)
                : session.Decompiler!.Decompile(function).Text;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return $"{analysis.NameFor(function.EntryVa)} could not be read as {view}: {ex.Message}";
        }

        maxLines = Math.Clamp(maxLines, 1, MaxLines);
        string continuation = $"read_function(target=\"0x{function.EntryVa:X}\", view=\"{view}\", offset={offset + maxLines})";

        var sb = new StringBuilder();
        if (inside is { } asked)
        {
            sb.Append(CultureInfo.InvariantCulture, $"(0x{asked:X} is inside this function)\n");
        }

        sb.Append(Header(session, analysis, function));
        sb.Append(Budget.Window(body, offset, maxLines, continuation));
        return Budget.Clip(sb.ToString());
    }

    [McpServerTool(Name = "disassemble")]
    [Description("Disassemble a run of instructions from any address, including bytes no function claims. Use read_function when the address is in one.")]
    public string Disassemble(
        [Description("Address to start at.")] string address,
        [Description("Instructions to decode, at most 256.")] int instructions = 48)
    {
        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        var resolved = Targets.Resolve(session, address);
        if (!resolved.Found)
        {
            return resolved.Problem!;
        }

        if (!analysis.Source.IsExecutable(resolved.Va))
        {
            return $"0x{resolved.Va:X} is not in executable memory; read_data reads it as data instead";
        }

        instructions = Math.Clamp(instructions, 1, MaxInstructions);
        var decoded = analysis.DisassembleRange(resolved.Va, instructions * 16, instructions);

        var sb = new StringBuilder();
        if (analysis.FunctionContaining(resolved.Va) is { } owner)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"inside {analysis.NameFor(owner.EntryVa)} at +0x{resolved.Va - owner.EntryVa:X} - read_function(target=\"0x{owner.EntryVa:X}\") reads the whole thing\n");
        }

        foreach (var instruction in decoded)
        {
            string operands = analysis.Disassembler.FormatOperands(instruction.Native);
            sb.Append(CultureInfo.InvariantCulture, $"0x{instruction.Va:X}  {instruction.Mnemonic}");
            if (operands.Length > 0)
            {
                sb.Append(' ').Append(operands);
            }

            sb.Append('\n');
        }

        return Budget.Clip(sb.ToString());
    }

    [McpServerTool(Name = "read_data")]
    [Description("Read bytes at an address as hex, text, or pointers. In \"pointers\" mode every word that lands in the image is named, which turns a vtable or an import table into a list of what it points at.")]
    public string ReadData(
        [Description("Address to read from.")] string address,
        [Description("Bytes to read, at most 1024.")] int length = 128,
        [Description("\"hex\", \"utf8\", \"utf16\" or \"pointers\". Default \"hex\".")] string @as = "hex")
    {
        if (_store.Current is not { } session)
        {
            return SessionTools.NothingOpen;
        }

        var resolved = Targets.Resolve(session, address);
        if (!resolved.Found)
        {
            return resolved.Problem!;
        }

        length = Math.Clamp(length, 1, MaxBytes);
        var bytes = session.Image.ReadAtVa(resolved.Va, length).Span;
        if (bytes.IsEmpty)
        {
            return $"0x{resolved.Va:X} is not inside any mapped section";
        }

        return Budget.Clip(@as switch
        {
            "utf8" => $"0x{resolved.Va:X}  \"{StringLiterals.Escape(Encoding.UTF8.GetString(bytes).TrimEnd('\0'))}\"",
            "utf16" => $"0x{resolved.Va:X}  L\"{StringLiterals.Escape(Encoding.Unicode.GetString(bytes).TrimEnd('\0'))}\"",
            "pointers" => Pointers(session, resolved.Va, bytes),
            _ => Hex(resolved.Va, bytes),
        });
    }

    // ------------------------------------------------------------------

    /// <summary>Why there is no managed reading of this file, for a view that asked for one.</summary>
    private static string NoManaged(BinarySession session, string view)
        => session.Image.ClrHeader is null
            ? $"{session.Image.FileName} is not a .NET assembly, so there is no \"{view}\" of it. "
              + "Use view=\"pseudo_c\" or view=\"asm\"."
            : $"{session.Image.FileName} carries a CLR header but its metadata could not be read"
              + (session.ManagedLoadError is { } why ? $" - {why}" : string.Empty);

    /// <summary>
    /// A type or member as C# or IL.
    ///
    /// The header is much shorter than the native one, and that is not an omission. Managed code
    /// carries its own facts: the signature is in the text, the declaring type is in the name, and
    /// what calls it is a question this cannot answer yet. Repeating what the body already says
    /// would be spending an agent's context to tell it what it is about to read.
    /// </summary>
    private static string ReadManaged(BinarySession session, ManagedTarget target, string view, int offset, int maxLines)
    {
        var managed = session.Managed!;
        bool il = view == "il";
        var type = target.Type!;

        string body;
        try
        {
            body = (il, target.Member) switch
            {
                (false, { } member) => managed.Decompiler.DecompileMember(member),
                (false, null) => managed.Decompiler.DecompileType(type),
                (true, { } member) => managed.Decompiler.DisassembleMember(member),
                (true, null) => managed.Decompiler.DisassembleType(type),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException
                                       or NotSupportedException or BadImageFormatException)
        {
            // ILSpy fails on individual members far more readily than the native decompiler does -
            // an unsupported construct, a reference it could not resolve - and the useful answer
            // names the other view, which very often works where this one did not.
            return $"{target.Describe()} could not be read as {(il ? "il" : "csharp")}: {ex.Message}"
                   + $"\ntry read_function(target=\"{target.Key()}\", view=\"{(il ? "csharp" : "il")}\")";
        }

        maxLines = Math.Clamp(maxLines, 1, MaxLines);
        string chosen = il ? "il" : "csharp";
        string continuation = $"read_function(target=\"{target.Key()}\", view=\"{chosen}\", offset={offset + maxLines})";

        var sb = new StringBuilder();
        if (target.Member is { } shown)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{type.FullName}::{shown.Signature}   {shown.Kind.ToString().ToLowerInvariant()}\n");
            sb.Append(CultureInfo.InvariantCulture, $"in          {type.FullName} ({type.Kind.ToString().ToLowerInvariant()})\n");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{type.FullName}   {type.Kind.ToString().ToLowerInvariant()}, {type.Members.Count} members\n");
            if (type.Members.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"members     find_symbol(query=\"{type.Name}\") names them; read one with read_function\n");
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $"assembly    {managed.FullName}\n");
        sb.Append(Budget.Window(body, offset, maxLines, continuation));
        return Budget.Clip(sb.ToString());
    }

    /// <summary>
    /// Everything about a function that is not its body. This is the densest part of the answer:
    /// without it the agent needs three more calls to learn what calls this, what it calls, and
    /// whether the decompiler trusted itself.
    /// </summary>
    private static string Header(BinarySession session, BinaryAnalysis analysis, Function function)
    {
        var sb = new StringBuilder();
        string name = analysis.NameFor(function.EntryVa);

        sb.Append(CultureInfo.InvariantCulture,
            $"{name}   0x{function.EntryVa:X}   0x{function.CodeSize:X} bytes, {function.Blocks.Count} blocks, {function.InstructionCount} instructions\n");

        var signature = analysis.SignatureFor(function.EntryVa);
        if (signature.Source != SignatureSource.None)
        {
            sb.Append(CultureInfo.InvariantCulture, $"signature   {signature}\n");
        }

        var callers = analysis.Xrefs.To(function.EntryVa);
        if (callers.Count > 0)
        {
            var owners = callers
                .Select(x => analysis.FunctionContaining(x.FromVa))
                .Where(f => f is not null)
                .Select(f => $"{analysis.NameFor(f!.EntryVa)}(0x{f.EntryVa:X})")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            sb.Append(CultureInfo.InvariantCulture,
                $"callers     {callers.Count} sites in {owners.Count} functions: {string.Join(", ", owners.Take(Listed))}{More(owners.Count)}\n");
        }

        var callees = function.CallTargets
            .Concat(function.IndirectCallSlots)
            .Distinct()
            .Select(analysis.NameFor)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (callees.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"calls       {string.Join(", ", callees.Take(Listed))}{More(callees.Count)}\n");
        }

        var strings = function.Instructions
            .SelectMany(i => analysis.Xrefs.From(i.Va))
            .Where(x => !x.IsCode)
            .Select(x => analysis.StringAt(x.ToVa))
            .Where(s => s is not null)
            .Select(s => StringLiterals.Escape(s!.Text))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (strings.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"strings     {string.Join(", ", strings.Take(Listed).Select(s => $"\"{Budget.Elide(s, 40)}\""))}{More(strings.Count)}\n");
        }

        if (analysis.CommentFor(function.EntryVa) is { } comment)
        {
            sb.Append(CultureInfo.InvariantCulture, $"comment     {comment}\n");
        }

        if (function.Notes.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"notes       {string.Join("; ", function.Notes.Take(2))}\n");
        }

        _ = session;
        return sb.ToString();
    }

    private static string More(int count) => count > Listed ? $", +{count - Listed} more" : string.Empty;

    /// <summary>Lifted into HexDump, so the debugger's memory reads render identically to these.</summary>
    private static string Hex(ulong va, ReadOnlySpan<byte> bytes) => HexDump.Render(bytes, va);

    /// <summary>
    /// Words read as pointers, each named when it lands somewhere known. A vtable becomes a list of
    /// methods and an import thunk table becomes a list of APIs, in one call instead of twenty.
    /// </summary>
    private static string Pointers(BinarySession session, ulong va, ReadOnlySpan<byte> bytes)
    {
        int width = session.Image.Is64Bit ? 8 : 4;
        var table = new TextTable(("at", 18), ("value", 18), ("points at", 60));

        for (int offset = 0; offset + width <= bytes.Length; offset += width)
        {
            ulong value = width == 8
                ? BitConverter.ToUInt64(bytes[offset..(offset + 8)])
                : BitConverter.ToUInt32(bytes[offset..(offset + 4)]);

            string points = session.Analysis is { } analysis && session.Image.SectionFromVa(value) is not null
                ? analysis.NameFor(value)
                : string.Empty;

            table.Add($"0x{va + (ulong)offset:X}", $"0x{value:X}", points);
        }

        return table.Render("nothing to read");
    }
}
