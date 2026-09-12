using System.Diagnostics.CodeAnalysis;
using Spydate.Decompiler.Managed;

namespace Spydate.Mcp.Session;

/// <summary>
/// Every type in an assembly, flattened out of the namespace tree and indexed by the names an agent
/// is likely to write.
///
/// Three indexes rather than one, because there are three names for the same type and an agent has
/// seen whichever the last answer printed. <c>FullName</c> is what C# shows
/// (<c>Spydate.Core.PE.PeImage</c>); <c>ReflectionName</c> is what IL and metadata show, with
/// <c>+</c> for nesting and a backtick for arity; and the bare <c>Name</c> is what the agent will
/// type when the namespace is obvious from context. Ambiguity is only possible on the last of them,
/// and is reported rather than guessed at.
/// </summary>
public sealed class ManagedIndex
{
    private readonly Dictionary<string, ManagedType> _byFullName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ManagedType>> _bySimpleName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManagedType> _all = new();

    public ManagedIndex(ManagedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var space in assembly.Namespaces)
        {
            foreach (var type in space.Types)
            {
                Add(type);
            }
        }

        MemberCount = _all.Sum(t => t.Members.Count);
    }

    /// <summary>Every type, nested ones included, in the order the namespaces list them.</summary>
    public IReadOnlyList<ManagedType> Types => _all;

    /// <summary>Members across every type. Reported in the overview so "how big is this" has an answer.</summary>
    public int MemberCount { get; }

    /// <summary>
    /// The type under any of its three names. Case-insensitive on purpose: C# is not, but an agent
    /// reading a name back out of prose gets the case wrong far more often than two types in one
    /// assembly differ only by it.
    /// </summary>
    public bool TryExact(string name, [NotNullWhen(true)] out ManagedType? type)
        => _byFullName.TryGetValue(name, out type);

    /// <summary>
    /// Types whose bare name is this. More than one is normal — two assemblies' worth of
    /// <c>Options</c> — and is the caller's problem to report, not this one's to resolve.
    /// </summary>
    public IReadOnlyList<ManagedType> BySimpleName(string name)
        => _bySimpleName.TryGetValue(name, out var list) ? list : Array.Empty<ManagedType>();

    /// <summary>Full names containing the text, for a failure that wants to be helpful.</summary>
    public IEnumerable<string> Containing(string text)
        => _all.Where(t => t.FullName.Contains(text, StringComparison.OrdinalIgnoreCase))
               .Select(t => t.FullName)
               .Distinct(StringComparer.Ordinal)
               .Order(StringComparer.Ordinal);

    private void Add(ManagedType type)
    {
        _all.Add(type);
        _byFullName.TryAdd(type.FullName, type);
        _byFullName.TryAdd(type.Definition.ReflectionName, type);

        if (!_bySimpleName.TryGetValue(type.Name, out var list))
        {
            _bySimpleName[type.Name] = list = new List<ManagedType>();
        }

        list.Add(type);

        foreach (var nested in type.NestedTypes)
        {
            Add(nested);
        }
    }
}

/// <summary>What a managed target resolved to: a type, a member of one, or neither.</summary>
public readonly record struct ManagedTarget(ManagedType? Type, ManagedMember? Member, string? Problem)
{
    /// <summary>True when this names something. A member always carries its declaring type as well.</summary>
    [MemberNotNullWhen(true, nameof(Type))]
    public bool Found => Type is not null;

    public static ManagedTarget OfType(ManagedType type) => new(type, null, null);

    public static ManagedTarget OfMember(ManagedType type, ManagedMember member) => new(type, member, null);

    public static ManagedTarget Failed(string problem) => new(null, null, problem);

    /// <summary>Nothing matched and nothing is wrong — the text was never a managed name.</summary>
    public static ManagedTarget None => default;

    /// <summary>What to print for it: the member's signature under its type, or the type's full name.</summary>
    public string Describe() => Member is { } member
        ? $"{Type!.FullName}::{member.Signature}"
        : Type?.FullName ?? "-";

    /// <summary>
    /// The same thing written so that <see cref="ManagedTargets.Resolve"/> gives it back. Every
    /// continuation this server prints is built from this rather than from what the caller typed,
    /// so a paged read resumes on the overload it started on.
    /// </summary>
    public string Key() => Member is { } member
        ? $"{Type!.FullName}::{Trim(member.Signature)}"
        : Type?.FullName ?? "-";

    private static string Trim(string signature)
    {
        int colon = signature.LastIndexOf(" : ", StringComparison.Ordinal);
        return colon < 0 ? signature : signature[..colon];
    }
}

/// <summary>
/// Turns what an agent writes into a type or a member of one, the way <see cref="Targets"/> turns it
/// into an address.
///
/// The forms accepted are the ones that appear in this server's own output, because that is where an
/// agent gets them: <c>Namespace.Type</c> from a type listing, <c>Namespace.Type::Member</c> from a
/// member row, <c>Namespace.Type.Member</c> because C# writes it that way, and a bare <c>Type</c>
/// when it is unambiguous. An overloaded member name resolves only once a signature is given, and
/// says so with the overloads listed rather than picking one — reading the wrong overload is a
/// mistake nothing downstream can detect.
/// </summary>
public static class ManagedTargets
{
    private const int Suggestions = 4;

    /// <summary>Resolves against the open assembly, or <see cref="ManagedTarget.None"/> if there is none.</summary>
    public static ManagedTarget Resolve(BinarySession session, string? target)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ManagedIndex is not { } index || string.IsNullOrWhiteSpace(target))
        {
            return ManagedTarget.None;
        }

        string text = target.Trim();

        if (index.TryExact(text, out var whole))
        {
            return ManagedTarget.OfType(whole);
        }

        // "::" is unambiguous, so it is tried before anything is guessed about dots.
        int mark = text.IndexOf("::", StringComparison.Ordinal);
        if (mark > 0)
        {
            return InType(index, text[..mark], text[(mark + 2)..].Trim(), text);
        }

        // A dot could be the last namespace separator or the one before a member name. The search
        // stops at any '(' so that an argument list full of dotted type names is not searched.
        int bound = text.IndexOf('(', StringComparison.Ordinal);
        int dot = text.LastIndexOf('.', (bound < 0 ? text.Length : bound) - 1);
        if (dot > 0)
        {
            var split = InType(index, text[..dot], text[(dot + 1)..].Trim(), text);
            if (split.Found)
            {
                return split;
            }
        }

        var simple = index.BySimpleName(text);
        return simple.Count switch
        {
            1 => ManagedTarget.OfType(simple[0]),
            > 1 => ManagedTarget.Failed(
                $"'{text}' names {simple.Count} types. Give the full one: {string.Join(", ", simple.Take(Suggestions).Select(t => t.FullName))}"),
            _ => ManagedTarget.Failed($"'{text}' is not a type or member in this assembly{Near(index, text)}"),
        };
    }

    /// <summary>The member of a named type, or why it is not one.</summary>
    private static ManagedTarget InType(ManagedIndex index, string typeName, string memberName, string whole)
    {
        if (!index.TryExact(typeName, out var type))
        {
            var candidates = index.BySimpleName(typeName);
            if (candidates.Count != 1)
            {
                return ManagedTarget.None;   // not a type, so the caller can try the text another way
            }

            type = candidates[0];
        }

        if (memberName.Length == 0)
        {
            return ManagedTarget.OfType(type);
        }

        string wantedName = Bare(memberName);
        var byName = type.Members.Where(m => Named(m, wantedName, StringComparison.Ordinal)).ToList();
        if (byName.Count == 0)
        {
            byName = type.Members.Where(m => Named(m, wantedName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (byName.Count == 0)
        {
            return ManagedTarget.Failed(
                $"{type.FullName} has no member '{Bare(memberName)}'{NearMember(type, Bare(memberName))}");
        }

        if (memberName.Contains('(', StringComparison.Ordinal))
        {
            // The return type is dropped from what was asked for as well as from what is compared
            // against, so a signature copied straight out of a listing - which prints " : Ret" -
            // resolves rather than missing every overload.
            string wanted = Normalise(Unreturned(memberName));
            var exact = byName.FirstOrDefault(m => Normalise(Unreturned(m.Signature)) == wanted);
            return exact is null
                ? ManagedTarget.Failed(
                    $"{type.FullName}::{Bare(memberName)} has no overload taking that. It has: "
                    + string.Join(", ", byName.Select(m => Unreturned(m.Signature))))
                : ManagedTarget.OfMember(type, exact);
        }

        if (byName.Count > 1)
        {
            return ManagedTarget.Failed(
                $"{type.FullName}::{Bare(memberName)} is overloaded {byName.Count} ways — name one: "
                + string.Join(", ", byName.Take(Suggestions).Select(m => $"\"{type.FullName}::{Unreturned(m.Signature)}\"")));
        }

        _ = whole;
        return ManagedTarget.OfMember(type, byName[0]);
    }

    /// <summary>
    /// Whether a member answers to this name — by its metadata name, or by the name the listings
    /// print for it.
    ///
    /// The two differ for exactly one kind of member and it is a common one. A constructor is
    /// <c>.ctor</c> in metadata, but every row this server prints shows it as
    /// <c>SpanReader(ReadOnlySpan)</c>, because that is how it is written and called. Matching only
    /// the metadata name meant the one form an agent could have copied from an answer was the one
    /// form that would not resolve.
    /// </summary>
    private static bool Named(ManagedMember member, string wanted, StringComparison how)
        => string.Equals(member.Name, wanted, how) || string.Equals(Bare(member.Signature), wanted, how);

    /// <summary>A member name with any argument list taken off.</summary>
    private static string Bare(string member)
    {
        int open = member.IndexOf('(', StringComparison.Ordinal);
        return open < 0 ? member : member[..open].TrimEnd();
    }

    /// <summary>A signature without its <c> : ReturnType</c> tail.</summary>
    private static string Unreturned(string signature)
    {
        int colon = signature.LastIndexOf(" : ", StringComparison.Ordinal);
        return colon < 0 ? signature : signature[..colon];
    }

    /// <summary>Every space dropped, so <c>Load(String, Int32)</c> and <c>Load(String,Int32)</c> match.</summary>
    private static string Normalise(string signature)
        => string.Concat(signature.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Names close enough to the one that missed to be worth offering.
    ///
    /// Substring alone is not enough here, and the reason is what the misses actually look like. A
    /// containing search finds a name the agent cut short; it finds nothing at all for a name the
    /// agent spelled wrong, which is the other half of the cases and the half where it has no way
    /// to recover on its own. So the last dotted segment is also matched by prefix, shortened until
    /// something answers: <c>PeImagg</c> misses on every whole-string test and matches
    /// <c>PeImage</c> at six characters. Three is the floor, below which every type in a namespace
    /// would qualify and the suggestion would say nothing.
    /// </summary>
    private static string Near(ManagedIndex index, string text)
    {
        if (text.Length < 3)
        {
            return string.Empty;
        }

        var close = index.Containing(text).Take(Suggestions).ToList();

        if (close.Count == 0)
        {
            int dot = text.LastIndexOf('.');
            string tail = dot >= 0 ? text[(dot + 1)..] : text;
            for (int length = tail.Length; length >= 3 && close.Count == 0; length--)
            {
                string prefix = tail[..length];
                close = index.Types
                    .Where(t => t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.FullName)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(Suggestions)
                    .ToList();
            }
        }

        return close.Count == 0 ? string.Empty : $". Did you mean: {string.Join(", ", close)}?";
    }

    private static string NearMember(ManagedType type, string name)
    {
        if (name.Length < 3)
        {
            return string.Empty;
        }

        var close = type.Members
            .Where(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                        || Bare(m.Signature).Contains(name, StringComparison.OrdinalIgnoreCase))
            .Select(m => Bare(m.Signature))
            .Distinct(StringComparer.Ordinal)
            .Take(Suggestions)
            .ToList();

        return close.Count == 0 ? string.Empty : $". Did you mean: {string.Join(", ", close)}?";
    }
}
