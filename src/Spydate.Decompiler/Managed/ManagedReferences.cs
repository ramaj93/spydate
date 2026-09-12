using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Spydate.Decompiler.Managed;

/// <summary>What one IL instruction does to the thing it names.</summary>
public enum ManagedRefKind
{
    Call,
    New,
    Read,
    Write,
    Type,
    String,
}

/// <summary>One instruction that refers to something, and where in its method it sits.</summary>
public readonly record struct ManagedSite(MethodDefinitionHandle From, int Offset, ManagedRefKind Kind);

/// <summary>A literal in the assembly, and every instruction that loads it.</summary>
public sealed record ManagedString(string Text, IReadOnlyList<ManagedSite> Sites)
{
    public int Count => Sites.Count;
}

/// <summary>
/// A member of another assembly that this one uses, with everywhere it is used.
///
/// The managed answer to an import table. A .NET assembly's real import list is not in its import
/// directory — that holds one entry, for the loader — it is in the MemberRef table, and it is only
/// discoverable by reading what the code actually calls.
/// </summary>
public sealed record ManagedImport(string Assembly, string Type, string Member, IReadOnlyList<ManagedSite> Sites)
{
    /// <summary>How this is written where it is called, and what <c>xrefs</c> takes for it.</summary>
    public string FullName => $"{Type}::{Member}";

    public int Count => Sites.Count;
}

/// <summary>
/// Who refers to what, read out of the IL.
///
/// This is the one part of managed analysis with no library behind it. ILSpy decompiles a member
/// beautifully and will not tell you who calls it: its analyzers ("used by", "instantiated by") live
/// in the ILSpy application rather than in the <c>ICSharpCode.Decompiler</c> package, so the scan is
/// ours. One pass over every method body answers all of it, because a string reference <em>is</em> a
/// reference — <c>ldstr</c> is in the same instruction stream as <c>call</c>, and walking it twice
/// to build two indexes would be walking it twice for nothing.
///
/// The walk is over untrusted bytes, and behaves like it. Every read is bounds-checked, an opcode
/// this does not know ends that method rather than the scan, and a body that cannot be reached at
/// all is skipped: a file whose method RVAs point into the middle of its own headers is a file
/// somebody opened this to look at, and it must come back with fewer answers rather than none.
/// </summary>
public sealed class ManagedReferences
{
    private readonly MetadataReader _reader;
    private readonly Dictionary<EntityHandle, List<ManagedSite>> _to = new();
    private readonly Dictionary<MethodDefinitionHandle, List<(EntityHandle Target, ManagedSite Site)>> _from = new();
    private readonly Dictionary<string, List<ManagedSite>> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManagedImportBuilder> _outside = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityHandle, string> _names = new();

    private ManagedReferences(MetadataReader reader) => _reader = reader;

    /// <summary>Instructions that referred to anything. Zero means nothing could be read.</summary>
    public int Count { get; private set; }

    /// <summary>Method bodies that could not be read at all, which is a fact about the file.</summary>
    public int Unreadable { get; private set; }

    /// <summary>Every literal, most-used first.</summary>
    public IReadOnlyList<ManagedString> Strings { get; private set; } = Array.Empty<ManagedString>();

    /// <summary>Every member of another assembly this one calls or reads, most-used first.</summary>
    public IReadOnlyList<ManagedImport> Imports { get; private set; } = Array.Empty<ManagedImport>();

    /// <summary>Walks every method body once. Costs one pass over the IL and is not re-entrant.</summary>
    public static ManagedReferences Build(ManagedAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var references = new ManagedReferences(assembly.Metadata);
        references.Scan(assembly);
        return references;
    }

    /// <summary>Everywhere that refers to something in this assembly.</summary>
    public IReadOnlyList<ManagedSite> To(EntityHandle target)
        => _to.TryGetValue(target, out var sites) ? sites : Array.Empty<ManagedSite>();

    /// <summary>Everything one method refers to, in the order the instructions appear.</summary>
    public IReadOnlyList<(EntityHandle Target, ManagedSite Site)> From(MethodDefinitionHandle method)
        => _from.TryGetValue(method, out var edges) ? edges : Array.Empty<(EntityHandle, ManagedSite)>();

    /// <summary>The outside member written that way, or null. Matches with or without a namespace.</summary>
    public ManagedImport? Import(string fullName)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        if (_outside.TryGetValue(fullName, out var exact))
        {
            return exact.Build();
        }

        // "File::Delete" for "System.IO.File::Delete": an agent writes what it read in the source,
        // and the source says whatever the using directives left it saying.
        var matches = _outside.Values
            .Where(o => o.FullName.EndsWith("." + fullName, StringComparison.Ordinal))
            .ToList();

        return matches.Count == 1 ? matches[0].Build() : null;
    }

    /// <summary>How an entity is written where it is referred to: <c>Namespace.Type::Member</c>.</summary>
    public string NameOf(EntityHandle handle)
    {
        if (handle.IsNil)
        {
            return "-";
        }

        if (_names.TryGetValue(handle, out string? cached))
        {
            return cached;
        }

        string name = Resolve(handle);
        _names[handle] = name;
        return name;
    }

    // ------------------------------------------------------------------
    // The scan
    // ------------------------------------------------------------------

    private void Scan(ManagedAssembly assembly)
    {
        var peReader = assembly.Module.Reader;

        foreach (var handle in _reader.MethodDefinitions)
        {
            int rva;
            try
            {
                rva = _reader.GetMethodDefinition(handle).RelativeVirtualAddress;
            }
            catch (BadImageFormatException)
            {
                Unreadable++;
                continue;
            }

            if (rva == 0)
            {
                continue;   // abstract, extern or a P/Invoke: no body to read, and that is not a fault
            }

            ImmutableArray<byte> il;
            try
            {
                il = peReader.GetMethodBody(rva).GetILContent();
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
            {
                Unreadable++;
                continue;
            }

            Walk(handle, il);
        }

        Strings = _strings
            .Select(pair => new ManagedString(pair.Key, pair.Value))
            .OrderByDescending(s => s.Count)
            .ThenByDescending(s => s.Text.Length)
            .ToList();

        Imports = _outside.Values
            .Select(o => o.Build())
            .OrderByDescending(i => i.Count)
            .ThenBy(i => i.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private void Walk(MethodDefinitionHandle method, ImmutableArray<byte> il)
    {
        foreach (var instruction in Il.Walk(il))
        {
            if (Interesting(instruction.Op, il, instruction.OperandAt) is { } found)
            {
                Record(method, instruction.Offset, found.Kind, found.Target, found.Text);
            }
        }
    }

    /// <summary>
    /// What an instruction refers to, if anything.
    ///
    /// Decided by the operand's <em>type</em> rather than by a list of opcodes. The list would need
    /// to name about thirty instructions and would be wrong the first time one was forgotten; the
    /// operand type is the same fact stated once, and it is what the format itself is built on.
    /// </summary>
    private (ManagedRefKind Kind, EntityHandle Target, string? Text)? Interesting(OpCode op, ImmutableArray<byte> il, int at)
    {
        switch (op.OperandType)
        {
            case OperandType.InlineString:
            {
                var handle = MetadataTokens.UserStringHandle(Token(il, at));
                try
                {
                    return (ManagedRefKind.String, default, _reader.GetUserString(handle));
                }
                catch (BadImageFormatException)
                {
                    return null;
                }
            }

            case OperandType.InlineMethod:
            {
                var target = Handle(il, at);
                return target.IsNil
                    ? null
                    : (op.Value == OpCodes.Newobj.Value ? ManagedRefKind.New : ManagedRefKind.Call, target, null);
            }

            case OperandType.InlineField:
            {
                var target = Handle(il, at);
                bool stores = op.Value == OpCodes.Stfld.Value || op.Value == OpCodes.Stsfld.Value;
                return target.IsNil ? null : (stores ? ManagedRefKind.Write : ManagedRefKind.Read, target, null);
            }

            case OperandType.InlineType:
            {
                var target = Handle(il, at);
                return target.IsNil ? null : (ManagedRefKind.Type, target, null);
            }

            case OperandType.InlineTok:
            {
                // ldtoken, which can name any of the three. The kind follows what it actually named.
                var target = Handle(il, at);
                return target.Kind switch
                {
                    HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification
                        => (ManagedRefKind.Call, target, null),
                    HandleKind.FieldDefinition => (ManagedRefKind.Read, target, null),
                    HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification
                        => (ManagedRefKind.Type, target, null),
                    _ => null,
                };
            }

            default:
                // InlineSig is calli, whose operand is a standalone signature and names no target.
                return null;
        }
    }

    private void Record(MethodDefinitionHandle from, int offset, ManagedRefKind kind, EntityHandle target, string? text)
    {
        var site = new ManagedSite(from, offset, kind);
        Count++;

        if (text is not null)
        {
            Add(_strings, text, site);
            return;
        }

        // A generic call names a MethodSpec, which is an instantiation of the method rather than the
        // method. Left alone, every call to List<T>.Add at a different T would be a different target
        // and none of them would be the one an agent asked about.
        var resolved = Unwrap(target);

        if (!_to.TryGetValue(resolved, out var sites))
        {
            _to[resolved] = sites = new List<ManagedSite>();
        }

        sites.Add(site);

        if (!_from.TryGetValue(from, out var edges))
        {
            _from[from] = edges = new List<(EntityHandle, ManagedSite)>();
        }

        edges.Add((resolved, site));

        if (resolved.Kind == HandleKind.MemberReference)
        {
            Outside((MemberReferenceHandle)resolved, site);
        }
    }

    /// <summary>A generic instantiation reduced to the thing it instantiates.</summary>
    private EntityHandle Unwrap(EntityHandle handle)
    {
        if (handle.Kind != HandleKind.MethodSpecification)
        {
            return handle;
        }

        try
        {
            return _reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }
        catch (BadImageFormatException)
        {
            return handle;
        }
    }

    private void Outside(MemberReferenceHandle handle, ManagedSite site)
    {
        string full = NameOf(handle);
        if (!_outside.TryGetValue(full, out var entry))
        {
            int mark = full.IndexOf("::", StringComparison.Ordinal);
            _outside[full] = entry = new ManagedImportBuilder(
                AssemblyOf(handle),
                mark < 0 ? full : full[..mark],
                mark < 0 ? string.Empty : full[(mark + 2)..]);
        }

        entry.Sites.Add(site);
    }

    private static void Add(Dictionary<string, List<ManagedSite>> map, string key, ManagedSite site)
    {
        if (!map.TryGetValue(key, out var sites))
        {
            map[key] = sites = new List<ManagedSite>();
        }

        sites.Add(site);
    }

    // ------------------------------------------------------------------
    // Naming
    // ------------------------------------------------------------------

    private string Resolve(EntityHandle handle)
    {
        try
        {
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => MethodDefName((MethodDefinitionHandle)handle),
                HandleKind.FieldDefinition => FieldDefName((FieldDefinitionHandle)handle),
                HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)handle),
                HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)handle),
                HandleKind.MemberReference => MemberRefName((MemberReferenceHandle)handle),
                HandleKind.MethodSpecification => NameOf(Unwrap(handle)),
                HandleKind.TypeSpecification => TypeSpecName((TypeSpecificationHandle)handle),
                _ => handle.Kind.ToString(),
            };
        }
        catch (BadImageFormatException)
        {
            return "(unreadable)";
        }
    }

    private string MethodDefName(MethodDefinitionHandle handle)
    {
        var method = _reader.GetMethodDefinition(handle);
        return $"{TypeDefName(method.GetDeclaringType())}::{_reader.GetString(method.Name)}";
    }

    private string FieldDefName(FieldDefinitionHandle handle)
    {
        var field = _reader.GetFieldDefinition(handle);
        return $"{TypeDefName(field.GetDeclaringType())}::{_reader.GetString(field.Name)}";
    }

    private string TypeDefName(TypeDefinitionHandle handle)
    {
        if (handle.IsNil)
        {
            return "(none)";
        }

        var type = _reader.GetTypeDefinition(handle);
        string name = Unqualified(_reader.GetString(type.Name));

        // Nested types carry their own name only, so the outer ones are walked back for it. Guarded
        // by a depth cap because a file is free to declare a type nested inside itself.
        var declaring = type.GetDeclaringType();
        for (int depth = 0; !declaring.IsNil && depth < 16; depth++)
        {
            var outer = _reader.GetTypeDefinition(declaring);
            name = $"{_reader.GetString(outer.Name)}.{name}";
            declaring = outer.GetDeclaringType();
        }

        string space = _reader.GetString(type.Namespace);
        return space.Length == 0 ? name : $"{space}.{name}";
    }

    private string TypeRefName(TypeReferenceHandle handle)
    {
        var type = _reader.GetTypeReference(handle);
        string name = Unqualified(_reader.GetString(type.Name));
        string space = _reader.GetString(type.Namespace);

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{TypeRefName((TypeReferenceHandle)type.ResolutionScope)}.{name}";
        }

        return space.Length == 0 ? name : $"{space}.{name}";
    }

    private string MemberRefName(MemberReferenceHandle handle)
    {
        var member = _reader.GetMemberReference(handle);
        string parent = member.Parent.Kind switch
        {
            HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)member.Parent),
            HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)member.Parent),
            HandleKind.TypeSpecification => TypeSpecName((TypeSpecificationHandle)member.Parent),
            _ => "(module)",
        };

        return $"{parent}::{_reader.GetString(member.Name)}";
    }

    /// <summary>
    /// The generic type behind an instantiation: <c>List&lt;int&gt;</c> named as <c>List</c>.
    ///
    /// This has to be decoded rather than skipped. A call to a member of a generic type names a
    /// TypeSpec, so with the signature unread every such call collapses into one bucket — and not
    /// an empty one: <c>List.Add</c> and <c>HashSet.Add</c> both became "(generic type)::Add", so a
    /// hundred calls were reported against a member that does not exist. Only the head of the
    /// signature is read, because the head is the name; the type arguments after it are what makes
    /// two instantiations different and what makes them both worth counting together.
    /// </summary>
    private string TypeSpecName(TypeSpecificationHandle handle)
        => SpecHead(handle) is { IsNil: false } head ? NameOf(head) : "(generic type)";

    /// <summary>
    /// The type a specification is built on, as a handle: the <c>List</c> behind <c>List&lt;int&gt;</c>,
    /// the element type behind an array of it. Nil when the signature bottoms out in something with
    /// no name of its own, such as a type parameter.
    /// </summary>
    private EntityHandle SpecHead(TypeSpecificationHandle handle)
    {
        try
        {
            var blob = _reader.GetBlobReader(_reader.GetTypeSpecification(handle).Signature);
            return Head(ref blob, 0);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException)
        {
            return default;
        }
    }

    private static EntityHandle Head(ref BlobReader blob, int depth)
    {
        if (depth > 8 || blob.RemainingBytes <= 0)
        {
            return default;
        }

        switch (blob.ReadSignatureTypeCode())
        {
            // Class and ValueType both arrive as TypeHandle, and both are followed by the token.
            case SignatureTypeCode.TypeHandle:
                return blob.ReadTypeHandle();

            // A wrapper around the type that actually carries the name.
            case SignatureTypeCode.GenericTypeInstance:
            case SignatureTypeCode.SZArray:
            case SignatureTypeCode.Array:
            case SignatureTypeCode.Pointer:
            case SignatureTypeCode.ByReference:
            case SignatureTypeCode.Pinned:
            case SignatureTypeCode.RequiredModifier:
            case SignatureTypeCode.OptionalModifier:
                return Head(ref blob, depth + 1);

            default:
                return default;
        }
    }

    /// <summary>
    /// A metadata name with its arity suffix removed: <c>List`1</c> becomes <c>List</c>.
    ///
    /// ILSpy's own FullName — which every internal type here is displayed under — has no backtick,
    /// and a listing that writes an assembly's own types one way and the ones it calls another is an
    /// inconsistency invented on the spot. The cost is that <c>Func`2</c> and <c>Func`3</c> become
    /// one row, which for "what does this assembly use" is the more useful grouping anyway.
    /// </summary>
    private static string Unqualified(string name)
    {
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>The assembly a reference resolves to, for grouping. Empty when it is not another one.</summary>
    private string AssemblyOf(MemberReferenceHandle handle)
    {
        try
        {
            var parent = _reader.GetMemberReference(handle).Parent;
            for (int depth = 0; depth < 16; depth++)
            {
                // A member of a generic type hangs off a TypeSpec, not a TypeRef. Stopping here was
                // why every List and Span call came back with no assembly against it.
                if (parent.Kind == HandleKind.TypeSpecification)
                {
                    parent = SpecHead((TypeSpecificationHandle)parent);
                    continue;
                }

                if (parent.Kind != HandleKind.TypeReference)
                {
                    return string.Empty;
                }

                var scope = _reader.GetTypeReference((TypeReferenceHandle)parent).ResolutionScope;
                if (scope.Kind == HandleKind.AssemblyReference)
                {
                    return _reader.GetString(_reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                }

                if (scope.Kind != HandleKind.TypeReference)
                {
                    return string.Empty;
                }

                parent = scope;
            }
        }
        catch (BadImageFormatException)
        {
            // falls through to the empty answer, which reads as "could not tell"
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------

    private static int Token(ImmutableArray<byte> il, int at)
        => il[at] | (il[at + 1] << 8) | (il[at + 2] << 16) | (il[at + 3] << 24);

    private static EntityHandle Handle(ImmutableArray<byte> il, int at)
    {
        try
        {
            return MetadataTokens.EntityHandle(Token(il, at));
        }
        catch (ArgumentException)
        {
            return default;   // a token naming a table that cannot hold one
        }
    }

    private sealed class ManagedImportBuilder(string assembly, string type, string member)
    {
        public List<ManagedSite> Sites { get; } = new();

        public string FullName { get; } = $"{type}::{member}";

        public ManagedImport Build() => new(assembly, type, member, Sites);
    }
}

