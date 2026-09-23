namespace Spydate.Core.Jvm;

/// <summary>
/// An annotation as the class file keeps it (JVMS §4.7.16): its type as a descriptor and its elements by name.
/// <see cref="Visible"/> is false for one the compiler kept for tools only (<c>RetentionPolicy.CLASS</c>).
/// </summary>
public sealed record JvmAnnotation(string Type, IReadOnlyList<JvmAnnotationElement> Elements, bool Visible);

/// <summary>One <c>name = value</c> of an annotation.</summary>
public sealed record JvmAnnotationElement(string Name, JvmElementValue Value);

/// <summary>An annotation element's value: a constant, an enum constant, a class, an annotation, or an array of them.</summary>
public abstract record JvmElementValue;

/// <summary>A constant; <see cref="Tag"/> is the JVM's type letter (<c>I</c>, <c>Z</c>, <c>C</c>…) or <c>s</c> for a string.</summary>
public sealed record JvmConstantElement(char Tag, object? Value) : JvmElementValue;

/// <summary><c>ElementType.METHOD</c>: the enum's descriptor and the constant's name.</summary>
public sealed record JvmEnumElement(string Type, string Constant) : JvmElementValue;

/// <summary><c>String.class</c>, as a return descriptor (<c>V</c> for <c>void.class</c>).</summary>
public sealed record JvmClassElement(string Descriptor) : JvmElementValue;

public sealed record JvmNestedAnnotation(JvmAnnotation Annotation) : JvmElementValue;

public sealed record JvmArrayElement(IReadOnlyList<JvmElementValue> Values) : JvmElementValue;

/// <summary>
/// Reads annotation attributes. Nesting is bounded, so an annotation nested inside itself a thousand times in a
/// crafted file costs a <see cref="ClassFormatException"/> for that attribute, not the process.
/// </summary>
internal static class AnnotationReader
{
    private const int MaxDepth = 32;

    public static List<JvmAnnotation> Annotations(ref ClassReader body, ConstantPool pool, bool visible)
    {
        int count = body.Count(4, "annotations");
        var annotations = new List<JvmAnnotation>(count);
        for (int i = 0; i < count; i++)
        {
            annotations.Add(Annotation(ref body, pool, visible, 0));
        }

        return annotations;
    }

    public static List<List<JvmAnnotation>> ParameterAnnotations(ref ClassReader body, ConstantPool pool, bool visible)
    {
        int parameters = body.U1();
        var all = new List<List<JvmAnnotation>>(parameters);
        for (int p = 0; p < parameters; p++)
        {
            all.Add(Annotations(ref body, pool, visible));
        }

        return all;
    }

    public static JvmElementValue Value(ref ClassReader body, ConstantPool pool) => Value(ref body, pool, 0);

    private static JvmAnnotation Annotation(ref ClassReader body, ConstantPool pool, bool visible, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ClassFormatException("annotations nest too deeply");
        }

        string type = pool.Utf8(body.U2()) ?? "Ljava/lang/Object;";
        int count = body.Count(3, "annotation elements");
        var elements = new List<JvmAnnotationElement>(count);
        for (int i = 0; i < count; i++)
        {
            string name = pool.Utf8(body.U2()) ?? "?";
            elements.Add(new JvmAnnotationElement(name, Value(ref body, pool, depth + 1)));
        }

        return new JvmAnnotation(type, elements, visible);
    }

    private static JvmElementValue Value(ref ClassReader body, ConstantPool pool, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ClassFormatException("annotation values nest too deeply");
        }

        char tag = (char)body.U1();
        switch (tag)
        {
            case 's':
                return new JvmConstantElement(tag, pool.Utf8(body.U2()));
            case 'B' or 'C' or 'I' or 'S' or 'Z':
                return new JvmConstantElement(tag, pool.Get(body.U2()) is { Tag: ConstantTag.Integer, Bits: var bits } ? (int)bits : null);
            case 'J':
                return new JvmConstantElement(tag, pool.Get(body.U2()) is { Tag: ConstantTag.Long, Bits: var l } ? l : null);
            case 'F':
                return new JvmConstantElement(tag, pool.Get(body.U2()) is { Tag: ConstantTag.Float, Bits: var f } ? BitConverter.Int32BitsToSingle((int)f) : null);
            case 'D':
                return new JvmConstantElement(tag, pool.Get(body.U2()) is { Tag: ConstantTag.Double, Bits: var d } ? BitConverter.Int64BitsToDouble(d) : null);
            case 'e':
                string enumType = pool.Utf8(body.U2()) ?? "?";
                return new JvmEnumElement(enumType, pool.Utf8(body.U2()) ?? "?");
            case 'c':
                return new JvmClassElement(pool.Utf8(body.U2()) ?? "V");
            case '@':
                return new JvmNestedAnnotation(Annotation(ref body, pool, visible: true, depth + 1));
            case '[':
                int count = body.Count(1, "array values");
                var values = new List<JvmElementValue>(count);
                for (int i = 0; i < count; i++)
                {
                    values.Add(Value(ref body, pool, depth + 1));
                }

                return new JvmArrayElement(values);
            default:
                throw new ClassFormatException($"annotation value tag '{tag}' is not one the JVM defines");
        }
    }
}
