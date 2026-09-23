using System.Globalization;
using Spydate.Core.Jvm;
using Spydate.Decompiler.Jvm;
using Spydate.Mcp.Rendering;
using Spydate.Mcp.Session;

namespace Spydate.Mcp.Tools;

/// <summary>
/// The answers xrefs, list_imports and find_strings give for a JAR, read out of its bytecode. Every row names its
/// site as <c>package.Class::member</c>, the form read_function takes, so an answer leads straight to the next
/// call; the offset is the one the bytecode listing prints.
/// </summary>
internal static class JvmAnswers
{
    /// <summary>Who refers to a class or member — this archive's, or one outside it such as <c>java.lang.Runtime::exec</c> — or what a method refers to.</summary>
    public static string? Xrefs(JvmReading reading, BytecodeTarget found, string target, string direction, int offset, int limit)
    {
        offset = Math.Max(0, offset);
        var references = reading.References;
        IReadOnlyList<JvmReference> rows;
        string what;

        if (direction == "from")
        {
            if (found.Member is not JvmMember { Method.Code: not null } method)
            {
                return found.Found
                    ? $"\"from\" reads what a method's own code refers to, and {found.Describe()} has no code"
                    : null;
            }

            rows = references.From(method);
            what = $"from {found.Describe()}";
            var fromTable = new TextTable(("at", 6), ("kind", 6), ("instruction", 16), ("refers to", 90));
            foreach (var r in rows.Skip(offset).Take(limit))
            {
                fromTable.Add(r.Offset.ToString("X4", CultureInfo.InvariantCulture), Kind(r.Kind), r.Mnemonic, Named(r));
            }

            return Page(what, fromTable, rows.Count, offset, limit, target, direction, "nothing: its code names no class, field or method");
        }

        if (found.Found && found.Type is JvmType type)
        {
            if (found.Member is JvmMember member)
            {
                rows = references.To(type.File.Name, member.Name, member.Descriptor);
            }
            else
            {
                rows = references.To(type.File.Name);
            }

            what = $"to {found.Describe()}";
        }
        else if (reading.ReferencedName(target) is { } outside)
        {
            rows = references.To(outside.Owner, string.IsNullOrEmpty(outside.Member) ? null : outside.Member);
            what = $"to {Descriptors.ClassName(outside.Owner)}{(string.IsNullOrEmpty(outside.Member) ? string.Empty : "::" + outside.Member)} (outside this archive)";
        }
        else
        {
            return null;
        }

        var table = new TextTable(("in", 70), ("at", 6), ("kind", 6), ("refers to", 60));
        foreach (var r in rows.Skip(offset).Take(limit))
        {
            table.Add(Site(r), r.Offset.ToString("X4", CultureInfo.InvariantCulture), Kind(r.Kind), Named(r));
        }

        return Page(what, table, rows.Count, offset, limit, target, direction, "nothing in this archive refers to it");
    }

    /// <summary>The classes outside the archive its code uses, most-used first: a JAR's equivalent of an import table.</summary>
    public static string Imports(JvmReading reading, string? module, string? filter, string sort, int offset, int limit)
    {
        var references = reading.References;
        var rows = references.Owners
            .Where(owner => reading.FindType(owner) is null && !owner.StartsWith('['))
            .Select(owner =>
            {
                var uses = references.To(owner);
                var members = uses.Where(u => u.Name is not null).Select(u => u.Name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                return (Class: Descriptors.ClassName(owner), Uses: uses.Count, Members: members);
            })
            .Where(r => module is null || r.Class.Contains(module, StringComparison.OrdinalIgnoreCase))
            .Where(r => filter is null || r.Members.Any(m => m.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        rows = sort == "name"
            ? rows.OrderBy(r => r.Class, StringComparer.Ordinal).ToList()
            : rows.OrderByDescending(r => r.Uses).ThenBy(r => r.Class, StringComparer.Ordinal).ToList();

        var table = new TextTable(("class", 56), ("uses", 5), ("members used", 90));
        foreach (var (name, uses, members) in rows.Skip(offset).Take(limit))
        {
            table.Add(name, uses.ToString(CultureInfo.InvariantCulture), string.Join(", ", members.Take(12)) + (members.Count > 12 ? $", +{members.Count - 12}" : string.Empty));
        }

        int shown = Math.Max(0, Math.Min(limit, rows.Count - offset));
        string? next = offset + shown < rows.Count ? $"list_imports(offset={offset + shown})" : null;
        return Budget.Clip("classes this archive uses from outside itself (the JDK, its dependencies); xrefs(target=\"java.lang.Class::member\") finds the callers\n"
                           + table.Render("its code uses nothing outside itself") + '\n'
                           + TextTable.Meta(shown, rows.Count, rows.Count, "classes", next, null));
    }

    /// <summary>String constants the code loads, each with the member that loads it.</summary>
    public static string Strings(JvmReading reading, string query, int minLength, int offset, int limit)
    {
        var all = reading.References.Strings;
        var matching = all
            .Where(s => s.Text.Length >= minLength)
            .Where(s => query.Length == 0 || s.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (query.Length == 0)
        {
            matching = matching.OrderByDescending(s => s.Text.Length).ToList();
        }

        var table = new TextTable(("string", 70), ("in", 70), ("at", 6));
        foreach (var use in matching.Skip(offset).Take(limit))
        {
            table.Add(Literal(use.Text), $"{use.Type.FullName}::{use.Member.Signature}", use.Offset < 0 ? "value" : use.Offset.ToString("X4", CultureInfo.InvariantCulture));
        }

        int shown = Math.Max(0, Math.Min(limit, matching.Count - offset));
        string? next = offset + shown < matching.Count ? $"find_strings(query=\"{query}\", offset={offset + shown})" : null;
        return Budget.Clip(table.Render(query.Length == 0 ? "no string constants" : $"no string contains \"{query}\"") + '\n'
                           + TextTable.Meta(shown, matching.Count, all.Count, "string loads", next, query.Length == 0 ? null : $"query={query}"));
    }

    private static string Page(string what, TextTable table, int total, int offset, int limit, string target, string direction, string empty)
    {
        int shown = Math.Max(0, Math.Min(limit, total - offset));
        string? next = offset + shown < total ? $"xrefs(target=\"{target}\", direction=\"{direction}\", offset={offset + shown})" : null;
        return Budget.Clip($"references {what}\n" + table.Render(empty) + '\n' + TextTable.Meta(shown, total, total, "references", next, null));
    }

    private static string Site(JvmReference r) => $"{r.FromType.FullName}::{r.FromMember.Signature}";

    private static string Named(JvmReference r)
    {
        string owner = Descriptors.ClassName(r.Owner);
        if (r.Name is null)
        {
            return owner;
        }

        if (r.Descriptor is { } descriptor && Descriptors.Method(descriptor, simple: true) is { } method)
        {
            return $"{owner}::{r.Name}({string.Join(", ", method.Parameters)}) : {method.Return}";
        }

        return r.Descriptor is { } field ? $"{owner}::{r.Name} : {Descriptors.TypeName(field, simple: true)}" : $"{owner}::{r.Name}";
    }

    private static string Kind(JvmReferenceKind kind) => kind switch
    {
        JvmReferenceKind.Call => "call",
        JvmReferenceKind.Read => "read",
        JvmReferenceKind.Write => "write",
        JvmReferenceKind.Type => "type",
        _ => "handle",
    };

    private static string Literal(string text)
    {
        string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
