using Spydate.Core.Jvm;

namespace Spydate.Decompiler.Jvm;

/// <summary>What an instruction does with what it names.</summary>
public enum JvmReferenceKind
{
    /// <summary>invokevirtual, invokestatic, invokespecial, invokeinterface.</summary>
    Call,

    /// <summary>getfield, getstatic.</summary>
    Read,

    /// <summary>putfield, putstatic.</summary>
    Write,

    /// <summary>new, checkcast, instanceof, anewarray, multianewarray, or a class literal.</summary>
    Type,

    /// <summary>
    /// A method handle: an invokedynamic's bootstrap argument, which is how a lambda's body or a method reference
    /// is reached — the only "call" to <c>lambda$main$0</c> there is.
    /// </summary>
    Handle,
}

/// <summary>One instruction in this archive that names a class, field or method, here or anywhere else.</summary>
public sealed record JvmReference(JvmType FromType, JvmMember FromMember, int Offset, JvmReferenceKind Kind, string Owner, string? Name, string? Descriptor, string Mnemonic);

/// <summary>A string constant, and the member whose code loads it (or the field it is the constant value of).</summary>
public sealed record JvmStringUse(string Text, JvmType Type, JvmMember Member, int Offset);

/// <summary>
/// Every reference and string literal in an archive's bytecode, read once and indexed by what is named. It is what
/// answers "who calls this", "what does this method touch", "who calls <c>Runtime.exec</c>" and "where is this
/// string used" — the questions a class listing alone cannot answer without reading every other class.
/// </summary>
public sealed class JvmReferences
{
    private readonly Dictionary<string, List<JvmReference>> _byOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<JvmMember, List<JvmReference>> _byMember = new();
    private readonly List<JvmStringUse> _strings = [];

    private JvmReferences()
    {
    }

    public IReadOnlyList<JvmStringUse> Strings => _strings;

    public int Count { get; private set; }

    /// <summary>Every class, inside the archive or not, that some instruction names.</summary>
    public IReadOnlyCollection<string> Owners => _byOwner.Keys;

    public static JvmReferences Build(JvmReading reading, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var references = new JvmReferences();
        foreach (var space in reading.Namespaces)
        {
            foreach (var type in space.Types.Cast<JvmType>())
            {
                references.Walk(type, cancellationToken);
            }
        }

        return references;
    }

    /// <summary>
    /// References to a class (<paramref name="name"/> null: the class itself and every member of it), or to one
    /// member by name — all its overloads unless a <paramref name="descriptor"/> picks one.
    /// </summary>
    public IReadOnlyList<JvmReference> To(string owner, string? name = null, string? descriptor = null)
    {
        if (!_byOwner.TryGetValue(owner, out var list))
        {
            return [];
        }

        return name is null
            ? list
            : list.Where(r => r.Name == name && (descriptor is null || r.Descriptor == descriptor)).ToList();
    }

    /// <summary>What one method's code names, in code order.</summary>
    public IReadOnlyList<JvmReference> From(JvmMember member) => _byMember.GetValueOrDefault(member) ?? [];

    private void Walk(JvmType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pool = type.File.Pool;
        foreach (var member in type.Members.Cast<JvmMember>())
        {
            if (member.Field is { ConstantValue: > 0 and var index } && pool.Get(index) is { Tag: ConstantTag.String, A: var text } && pool.Utf8(text) is { } value)
            {
                _strings.Add(new JvmStringUse(value, type, member, -1));
            }

            if (member.Method?.Code is not { } code)
            {
                continue;
            }

            foreach (var instruction in Bytecode.Decode(code.Code.Span))
            {
                Record(type, member, instruction, pool);
            }
        }

        foreach (var nested in type.NestedTypes.Cast<JvmType>())
        {
            Walk(nested, cancellationToken);
        }
    }

    private void Record(JvmType type, JvmMember member, JvmInstruction instruction, ConstantPool pool)
    {
        switch (instruction.Kind)
        {
            case OperandKind.Pool:
            case OperandKind.PoolByte:
            case OperandKind.Interface:
            case OperandKind.MultiArray:
                break;
            case OperandKind.Dynamic:
                if (pool.Get(instruction.Operand) is { Tag: ConstantTag.InvokeDynamic, A: var bootstrap }
                    && bootstrap < type.File.BootstrapMethods.Count)
                {
                    foreach (int argument in type.File.BootstrapMethods[bootstrap].Arguments)
                    {
                        if (pool.Get(argument) is { Tag: ConstantTag.MethodHandle, B: var target } && pool.Member(target) is { } handled)
                        {
                            Add(new JvmReference(type, member, instruction.Offset, JvmReferenceKind.Handle, handled.Owner, handled.Name, handled.Descriptor, instruction.Mnemonic));
                        }
                    }
                }

                return;
            default:
                return;
        }

        int index = instruction.Operand;
        if (pool.Member(index) is { } named)
        {
            var kind = instruction.Opcode switch
            {
                0xB2 or 0xB4 => JvmReferenceKind.Read,
                0xB3 or 0xB5 => JvmReferenceKind.Write,
                _ => JvmReferenceKind.Call,
            };
            Add(new JvmReference(type, member, instruction.Offset, kind, named.Owner, named.Name, named.Descriptor, instruction.Mnemonic));
            return;
        }

        switch (pool.Get(index))
        {
            case { Tag: ConstantTag.Class, A: var name } when pool.Utf8(name) is { } className:
                Add(new JvmReference(type, member, instruction.Offset, JvmReferenceKind.Type, ElementClass(className), null, null, instruction.Mnemonic));
                break;
            case { Tag: ConstantTag.String, A: var text } when pool.Utf8(text) is { } value:
                _strings.Add(new JvmStringUse(value, type, member, instruction.Offset));
                break;
            case { Tag: ConstantTag.MethodHandle, B: var target } when pool.Member(target) is { } handled:
                Add(new JvmReference(type, member, instruction.Offset, JvmReferenceKind.Handle, handled.Owner, handled.Name, handled.Descriptor, instruction.Mnemonic));
                break;
        }
    }

    /// <summary>An array class names its element: <c>[Ljava/lang/String;</c> is a use of <c>java/lang/String</c>.</summary>
    private static string ElementClass(string className)
    {
        string trimmed = className.TrimStart('[');
        return trimmed.Length != className.Length && trimmed.StartsWith('L') && trimmed.EndsWith(';')
            ? trimmed[1..^1]
            : className;
    }

    private void Add(JvmReference reference)
    {
        if (!_byOwner.TryGetValue(reference.Owner, out var byOwner))
        {
            _byOwner[reference.Owner] = byOwner = [];
        }

        byOwner.Add(reference);
        if (!_byMember.TryGetValue(reference.FromMember, out var byMember))
        {
            _byMember[reference.FromMember] = byMember = [];
        }

        byMember.Add(reference);
        Count++;
    }
}
