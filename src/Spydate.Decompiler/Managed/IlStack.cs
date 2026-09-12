using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Spydate.Decompiler.Managed;

/// <summary>
/// What a run of IL leaves on the evaluation stack.
///
/// This has no equivalent on the native side, and it is the difference between an IL patcher and a
/// way of producing files that will not run. x86 is a machine with registers: NOPping a call leaves
/// a wrong answer in a register and the program carries on being wrong, which is usually the whole
/// point of the patch. IL is a stack machine that the runtime verifies before it will execute
/// anything, so NOPping a call leaves that call's arguments pushed and its result missing, the
/// stack depth no longer agrees at the next join, and the method is rejected outright with an
/// <c>InvalidProgramException</c> — at the moment it is first called, which may be nowhere near the
/// patch and long after the analyst has stopped looking.
///
/// So the delta is computed for what is being removed and for what is going in its place, and a
/// mismatch is reported as the number of values it is out by. "This call leaves one value on the
/// stack and your replacement leaves none" is an answer somebody can act on; a file that crashes on
/// load is not.
/// </summary>
public sealed class IlStack
{
    private readonly MetadataReader _reader;

    public IlStack(MetadataReader reader) => _reader = reader;

    /// <summary>
    /// The net change over a run of instructions, or null when a signature could not be read.
    ///
    /// Null is not zero and must not be treated as it: it means "nobody can say", and the caller's
    /// job is to pass that word on rather than to assume the convenient half of it.
    /// </summary>
    public int? Delta(ImmutableArray<byte> il, int start, int length, MethodDefinitionHandle owner = default)
    {
        int total = 0;
        int at = start;
        while (at < start + length)
        {
            if (!Il.TryDecode(il, at, out var instruction) || instruction.Offset + instruction.Length > start + length)
            {
                return null;
            }

            if (Delta(instruction, il, owner) is not { } delta)
            {
                return null;
            }

            total += delta;
            at += instruction.Length;
        }

        return total;
    }

    /// <summary>One instruction's net effect, or null when it depends on something unreadable.</summary>
    public int? Delta(IlInstruction instruction, ImmutableArray<byte> il, MethodDefinitionHandle owner = default)
    {
        var op = instruction.Op;

        int? pops = op.StackBehaviourPop == StackBehaviour.Varpop
            ? VariablePops(op, il, instruction.OperandAt, owner)
            : Fixed(op.StackBehaviourPop);

        int? pushes = op.StackBehaviourPush == StackBehaviour.Varpush
            ? VariablePushes(op, il, instruction.OperandAt)
            : Fixed(op.StackBehaviourPush);

        return pops is { } out_ && pushes is { } in_ ? in_ - out_ : null;
    }

    /// <summary>
    /// How many values a fixed behaviour moves.
    ///
    /// Written out rather than counted from the enum's name, which does encode it: <c>Popref_popi</c>
    /// really is two. Deriving it would be shorter and would be a rule nobody stated, wrong the
    /// first time the framework adds a member that does not follow it — and wrong silently, since
    /// the result is a number that looks as plausible as any other.
    /// </summary>
    private static int? Fixed(StackBehaviour behaviour) => behaviour switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 => 0,

        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref
            or StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8
            or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,

        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
            or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
            or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi
            or StackBehaviour.Push1_push1 => 2,

        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi
            or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
            or StackBehaviour.Popref_popi_pop1 => 3,

        _ => null,
    };

    /// <summary>
    /// What a call-shaped instruction takes: its arguments, plus the receiver when it has one.
    ///
    /// <c>newobj</c> is the exception worth naming — its signature is a constructor's, so it says
    /// <c>HasThis</c>, but the object does not exist yet and is not on the stack. Counting the
    /// receiver there would report every construction as one value deeper than it is.
    /// </summary>
    private int? VariablePops(OpCode op, ImmutableArray<byte> il, int operandAt, MethodDefinitionHandle owner)
    {
        if (op.Value == OpCodes.Ret.Value)
        {
            // What ret takes is decided by the method it is in, not by anything at the ret itself.
            return owner.IsNil ? null : Signature(owner) is { ReturnsVoid: false } ? 1 : 0;
        }

        if (Target(il, operandAt) is not { } target || Signature(target) is not { } signature)
        {
            return null;
        }

        bool newobj = op.Value == OpCodes.Newobj.Value;
        int receiver = signature.HasThis && !newobj ? 1 : 0;
        int pointer = op.Value == OpCodes.Calli.Value ? 1 : 0;
        return signature.Arguments + receiver + pointer;
    }

    private int? VariablePushes(OpCode op, ImmutableArray<byte> il, int operandAt)
    {
        if (op.Value == OpCodes.Newobj.Value)
        {
            return 1;   // the constructed object, whatever the constructor's own signature says
        }

        if (Target(il, operandAt) is not { } target || Signature(target) is not { } signature)
        {
            return null;
        }

        return signature.ReturnsVoid ? 0 : 1;
    }

    private static EntityHandle? Target(ImmutableArray<byte> il, int at)
    {
        if (at + 4 > il.Length)
        {
            return null;
        }

        int token = il[at] | (il[at + 1] << 8) | (il[at + 2] << 16) | (il[at + 3] << 24);
        try
        {
            return MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>How many arguments something takes and whether it gives anything back.</summary>
    private (int Arguments, bool HasThis, bool ReturnsVoid)? Signature(EntityHandle handle)
    {
        try
        {
            var blob = handle.Kind switch
            {
                HandleKind.MethodDefinition => _reader.GetMethodDefinition((MethodDefinitionHandle)handle).Signature,
                HandleKind.MemberReference => _reader.GetMemberReference((MemberReferenceHandle)handle).Signature,
                HandleKind.StandaloneSignature => _reader.GetStandaloneSignature((StandaloneSignatureHandle)handle).Signature,
                HandleKind.MethodSpecification =>
                    Specification((MethodSpecificationHandle)handle) is { } inner ? Blob(inner) : default,
                _ => default,
            };

            if (blob.IsNil)
            {
                return null;
            }

            var reader = _reader.GetBlobReader(blob);
            var header = reader.ReadSignatureHeader();
            if (header.Kind is not (SignatureKind.Method or SignatureKind.Property))
            {
                return null;
            }

            if (header.IsGeneric)
            {
                reader.ReadCompressedInteger();   // the count of type arguments, which are not values
            }

            int arguments = reader.ReadCompressedInteger();
            return (arguments, header.IsInstance, ReturnsVoid(ref reader));
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    private EntityHandle? Specification(MethodSpecificationHandle handle)
    {
        var method = _reader.GetMethodSpecification(handle).Method;
        return method.IsNil ? null : method;
    }

    private BlobHandle Blob(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MethodDefinition => _reader.GetMethodDefinition((MethodDefinitionHandle)handle).Signature,
        HandleKind.MemberReference => _reader.GetMemberReference((MemberReferenceHandle)handle).Signature,
        _ => default,
    };

    /// <summary>
    /// Whether the return type is <c>void</c>, which decides whether a call leaves anything behind.
    ///
    /// Custom modifiers come first and are skipped: a return type of <c>modopt(IsConst) void</c> is
    /// still void, and stopping at the modifier would read it as a value.
    /// </summary>
    private static bool ReturnsVoid(ref BlobReader reader)
    {
        for (int depth = 0; depth < 8; depth++)
        {
            var code = reader.ReadSignatureTypeCode();
            if (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
            {
                reader.ReadTypeHandle();
                continue;
            }

            return code == SignatureTypeCode.Void;
        }

        return false;
    }
}
