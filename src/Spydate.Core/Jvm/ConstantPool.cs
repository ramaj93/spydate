using System.Text;

namespace Spydate.Core.Jvm;

/// <summary>A constant pool entry's kind, by the tag byte the class file gives it.</summary>
public enum ConstantTag : byte
{
    /// <summary>Slot 0, and the second slot a long or double takes.</summary>
    None = 0,
    Utf8 = 1,
    Integer = 3,
    Float = 4,
    Long = 5,
    Double = 6,
    Class = 7,
    String = 8,
    Fieldref = 9,
    Methodref = 10,
    InterfaceMethodref = 11,
    NameAndType = 12,
    MethodHandle = 15,
    MethodType = 16,
    Dynamic = 17,
    InvokeDynamic = 18,
    Module = 19,
    Package = 20,
}

/// <summary>
/// One constant pool entry. The meaning of <see cref="A"/> and <see cref="B"/> follows the tag: a Class names its
/// Utf8 in A; a Methodref has its class in A and its NameAndType in B; a MethodHandle its kind in A and its
/// reference in B; an InvokeDynamic its bootstrap method in A and its NameAndType in B. Numbers are in
/// <see cref="Bits"/>, text in <see cref="Text"/>.
/// </summary>
public readonly record struct Constant(ConstantTag Tag, ushort A, ushort B, long Bits, string? Text);

/// <summary>A reference to a field or method: the class that holds it, its name, and its descriptor.</summary>
public sealed record MemberReference(string Owner, string Name, string Descriptor, bool IsInterface);

/// <summary>
/// A class file's constant pool. Reading it is strict — a malformed pool ends the parse — but looking things up in
/// it is forgiving: a listing asks about indices a hostile file made up, and deserves a null back, not an exception.
/// </summary>
public sealed class ConstantPool
{
    private readonly Constant[] _entries;

    private ConstantPool(Constant[] entries) => _entries = entries;

    /// <summary>A pool built rather than read — for a class translated from DEX — its entries from index 1 on.</summary>
    internal static ConstantPool Of(IEnumerable<Constant> entries) => new([default, .. entries]);

    /// <summary>The declared count, one more than the highest index, as the class file states it.</summary>
    public int Count => _entries.Length;

    public static ConstantPool Read(ref ClassReader reader)
    {
        int count = reader.U2();
        // Every entry takes at least three bytes, so a count the rest of the file cannot hold is refused before
        // it sizes anything.
        if ((long)(count - 1) * 3 > reader.Remaining)
        {
            throw new ClassFormatException($"A constant pool of {count} entries cannot fit in the {reader.Remaining} bytes left.");
        }

        var entries = new Constant[Math.Max(count, 1)];
        for (int i = 1; i < count; i++)
        {
            int at = reader.Position;
            var tag = (ConstantTag)reader.U1();
            switch (tag)
            {
                case ConstantTag.Utf8:
                    int length = reader.U2();
                    entries[i] = new Constant(tag, 0, 0, 0, ModifiedUtf8(reader.Bytes(length)));
                    break;
                case ConstantTag.Integer:
                case ConstantTag.Float:
                    entries[i] = new Constant(tag, 0, 0, (int)reader.U4(), null);
                    break;
                case ConstantTag.Long:
                case ConstantTag.Double:
                    entries[i] = new Constant(tag, 0, 0, (long)reader.U8(), null);
                    i++;   // takes two slots; the second is unusable, as the specification regrets
                    break;
                case ConstantTag.Class:
                case ConstantTag.String:
                case ConstantTag.MethodType:
                case ConstantTag.Module:
                case ConstantTag.Package:
                    entries[i] = new Constant(tag, reader.U2(), 0, 0, null);
                    break;
                case ConstantTag.Fieldref:
                case ConstantTag.Methodref:
                case ConstantTag.InterfaceMethodref:
                case ConstantTag.NameAndType:
                case ConstantTag.Dynamic:
                case ConstantTag.InvokeDynamic:
                    entries[i] = new Constant(tag, reader.U2(), reader.U2(), 0, null);
                    break;
                case ConstantTag.MethodHandle:
                    entries[i] = new Constant(tag, reader.U1(), reader.U2(), 0, null);
                    break;
                default:
                    throw new ClassFormatException($"Constant pool entry {i} at offset 0x{at:X} has tag {(byte)tag}, which no class file version defines.");
            }
        }

        return new ConstantPool(entries);
    }

    public Constant? Get(int index) => index > 0 && index < _entries.Length && _entries[index].Tag != ConstantTag.None
        ? _entries[index]
        : null;

    public string? Utf8(int index) => Get(index) is { Tag: ConstantTag.Utf8, Text: var text } ? text : null;

    /// <summary>A Class entry's internal name: <c>java/lang/String</c>, or an array descriptor such as <c>[I</c>.</summary>
    public string? ClassName(int index) => Get(index) is { Tag: ConstantTag.Class, A: var name } ? Utf8(name) : null;

    public (string Name, string Descriptor)? NameAndType(int index)
        => Get(index) is { Tag: ConstantTag.NameAndType, A: var name, B: var descriptor }
           && Utf8(name) is { } n && Utf8(descriptor) is { } d
            ? (n, d)
            : null;

    /// <summary>A Fieldref, Methodref or InterfaceMethodref, resolved to names.</summary>
    public MemberReference? Member(int index)
        => Get(index) is { Tag: ConstantTag.Fieldref or ConstantTag.Methodref or ConstantTag.InterfaceMethodref } c
           && ClassName(c.A) is { } owner && NameAndType(c.B) is { } nat
            ? new MemberReference(owner, nat.Name, nat.Descriptor, c.Tag == ConstantTag.InterfaceMethodref)
            : null;

    /// <summary>The name a structural index must resolve to, or a <see cref="ClassFormatException"/> saying which.</summary>
    internal string RequireUtf8(int index, string what)
        => Utf8(index) ?? throw new ClassFormatException($"The {what} names constant pool entry {index}, which is not text.");

    internal string RequireClass(int index, string what)
        => ClassName(index) ?? throw new ClassFormatException($"The {what} names constant pool entry {index}, which is not a class.");

    /// <summary>
    /// Decodes the JVM's "modified UTF-8": NUL as two bytes, and characters beyond the BMP as two three-byte
    /// surrogates. A malformed sequence becomes U+FFFD rather than an error — a name in a hostile file is still
    /// worth showing.
    /// </summary>
    public static string ModifiedUtf8(ReadOnlySpan<byte> bytes)
    {
        bool ascii = true;
        foreach (byte b in bytes)
        {
            if (b is 0 or >= 0x80)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            return Encoding.ASCII.GetString(bytes);
        }

        var sb = new StringBuilder(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            int b = bytes[i];
            if (b is > 0 and < 0x80)
            {
                sb.Append((char)b);
            }
            else if ((b & 0xE0) == 0xC0 && i + 1 < bytes.Length && (bytes[i + 1] & 0xC0) == 0x80)
            {
                sb.Append((char)(((b & 0x1F) << 6) | (bytes[i + 1] & 0x3F)));
                i++;
            }
            else if ((b & 0xF0) == 0xE0 && i + 2 < bytes.Length && (bytes[i + 1] & 0xC0) == 0x80 && (bytes[i + 2] & 0xC0) == 0x80)
            {
                sb.Append((char)(((b & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F)));
                i += 2;
            }
            else
            {
                sb.Append('�');
            }
        }

        return sb.ToString();
    }
}
