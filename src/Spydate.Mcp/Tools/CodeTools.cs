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
    [Description("Read a function as pseudo-C (default) or as a disassembly listing, with a header naming its signature, callers, callees and any strings it uses. An address inside a function resolves to the function.")]
    public string ReadFunction(
        [Description("Address, sub_XXXX, or a name.")] string target,
        [Description("\"pseudo_c\" or \"asm\". Default \"pseudo_c\", which carries far more meaning per token.")] string view = "pseudo_c",
        [Description("First line of the body to return, for continuing a long function.")] int offset = 0,
        [Description("Body lines to return, at most 1000.")] int maxLines = DefaultLines)
    {
        if (_store.Current is not { Analysis: { } analysis } session)
        {
            return SessionTools.NothingOpen;
        }

        var (resolved, function, inside) = Targets.ResolveFunction(session, target);
        if (!resolved.Found || function is null)
        {
            return resolved.Problem ?? $"no function at {target}";
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

    private static string Hex(ulong va, ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            int run = Math.Min(16, bytes.Length - offset);
            sb.Append(CultureInfo.InvariantCulture, $"0x{va + (ulong)offset:X}  ");
            for (int i = 0; i < 16; i++)
            {
                sb.Append(i < run ? bytes[offset + i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(' ');
            }

            sb.Append(' ');
            for (int i = 0; i < run; i++)
            {
                byte b = bytes[offset + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

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
