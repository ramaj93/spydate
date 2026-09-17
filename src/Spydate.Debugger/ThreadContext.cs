using System.Runtime.InteropServices;

namespace Spydate.Debugger;

/// <summary>
/// One thread's registers.
///
/// There are two of these because there are two kinds of thread. A 64-bit thread answers
/// <c>GetThreadContext</c> with <c>CONTEXT_AMD64</c>; a 32-bit thread on 64-bit Windows runs under
/// WOW64 and answers the same call with the wow64 layer's own 64-bit state — successfully. Asking
/// the wrong one is not refused, it is quietly believed, and wrong register values in a debugger are
/// worse than none because they are acted on.
///
/// The buffer is allocated aligned rather than declared as a struct. <c>CONTEXT</c> contains the XMM
/// save area and the kernel requires 16-byte alignment; a managed struct on the stack or inside an
/// object gives whatever alignment the allocator felt like, and <c>GetThreadContext</c> answers a
/// misaligned one with a flat refusal that says nothing about why.
///
/// Fields are read by offset for the same reason the debug event is: declaring all 1232 bytes to use
/// eighteen of them is more code and more places to be wrong.
/// </summary>
public abstract unsafe class ThreadContext : IDisposable
{
    private const int Alignment = 16;

    private byte* _buffer;

    protected ThreadContext(int size)
    {
        _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)size, Alignment);
        NativeMemory.Clear(_buffer, (nuint)size);
    }

    /// <summary>The right one for the thread being read. See the class summary for why it matters.</summary>
    public static ThreadContext For(bool wow64) => wow64 ? new Wow64ThreadContext() : new X64ThreadContext();

    protected byte* Buffer => _buffer;

    /// <summary>The whole CONTEXT, for an API that takes the structure rather than one field — a
    /// native stack walk hands this to StackWalk64, which reads and updates it as it unwinds.</summary>
    internal byte* Raw => _buffer;

    public abstract bool Read(IntPtr thread);

    public abstract bool Write(IntPtr thread);

    /// <summary>RIP, or EIP on a 32-bit thread.</summary>
    public abstract ulong InstructionPointer { get; set; }

    /// <summary>RSP, or ESP on a 32-bit thread.</summary>
    public abstract ulong StackPointer { get; }

    /// <summary>RBP, or EBP on a 32-bit thread — the frame pointer, to seed a stack walk.</summary>
    public abstract ulong FramePointer { get; }

    public abstract uint EFlags { get; set; }

    /// <summary>
    /// Everything worth showing, in the order a person reads it: the general registers, then the
    /// instruction pointer, then the flags. Named as the architecture names them, so what a panel
    /// shows and what a listing says are the same words.
    /// </summary>
    public abstract IReadOnlyList<(string Name, ulong Value)> General();

    /// <summary>Asks the processor to fault after one instruction.</summary>
    public void SetTrapFlag(bool on) => EFlags = on ? EFlags | Native.TrapFlag : EFlags & ~Native.TrapFlag;

    public void Dispose()
    {
        if (_buffer is not null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>A 64-bit thread, as <c>CONTEXT_AMD64</c>.</summary>
public sealed unsafe class X64ThreadContext : ThreadContext
{
    private const int Size = 1232;

    // Offsets into CONTEXT_AMD64. The general registers run from Rax at a uniform eight-byte stride.
    private const int OffContextFlags = 0x30;
    private const int OffEFlags = 0x44;
    private const int OffRax = 0x78;
    private const int OffRip = 0xF8;

    private static readonly IReadOnlyList<string> Names =
    [
        "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
        "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
    ];

    public X64ThreadContext()
        : base(Size)
        => *(uint*)(Buffer + OffContextFlags) = Native.CONTEXT_AMD64_FULL;

    public override bool Read(IntPtr thread)
    {
        *(uint*)(Buffer + OffContextFlags) = Native.CONTEXT_AMD64_FULL;
        return Native.GetThreadContext(thread, Buffer);
    }

    public override bool Write(IntPtr thread)
    {
        *(uint*)(Buffer + OffContextFlags) = Native.CONTEXT_AMD64_FULL;
        return Native.SetThreadContext(thread, Buffer);
    }

    public override ulong InstructionPointer
    {
        get => *(ulong*)(Buffer + OffRip);
        set => *(ulong*)(Buffer + OffRip) = value;
    }

    public override ulong StackPointer => this[4];

    public override ulong FramePointer => this[5];

    public override uint EFlags
    {
        get => *(uint*)(Buffer + OffEFlags);
        set => *(uint*)(Buffer + OffEFlags) = value;
    }

    public override IReadOnlyList<(string Name, ulong Value)> General()
    {
        var all = Names.Select((n, i) => (n, this[i])).ToList();
        all.Add(("rip", InstructionPointer));
        all.Add(("rflags", (ulong)EFlags));
        return all;
    }

    private ulong this[int index] => *(ulong*)(Buffer + OffRax + (index * 8));
}

/// <summary>
/// A 32-bit thread on 64-bit Windows, as <c>WOW64_CONTEXT</c>.
///
/// Its general registers are not at a stride and not in a helpful order — the structure lists them
/// Edi, Esi, Ebx, Edx, Ecx, Eax, Ebp, then Eip, and Esp after the flags — so each is named with its
/// own offset rather than indexed.
/// </summary>
public sealed unsafe class Wow64ThreadContext : ThreadContext
{
    private const int Size = 716;

    // Offsets into WOW64_CONTEXT: ContextFlags, six debug registers, a 112-byte float save area,
    // four segment selectors, then the registers below.
    private const int OffContextFlags = 0x00;
    private const int OffEdi = 0x9C;
    private const int OffEsi = 0xA0;
    private const int OffEbx = 0xA4;
    private const int OffEdx = 0xA8;
    private const int OffEcx = 0xAC;
    private const int OffEax = 0xB0;
    private const int OffEbp = 0xB4;
    private const int OffEip = 0xB8;
    private const int OffEFlags = 0xC0;
    private const int OffEsp = 0xC4;

    public Wow64ThreadContext()
        : base(Size)
        => *(uint*)(Buffer + OffContextFlags) = Native.CONTEXT_WOW64_FULL;

    public override bool Read(IntPtr thread)
    {
        *(uint*)(Buffer + OffContextFlags) = Native.CONTEXT_WOW64_FULL;
        return Native.Wow64GetThreadContext(thread, Buffer);
    }

    public override bool Write(IntPtr thread)
    {
        *(uint*)(Buffer + OffContextFlags) = Native.CONTEXT_WOW64_FULL;
        return Native.Wow64SetThreadContext(thread, Buffer);
    }

    public override ulong InstructionPointer
    {
        get => *(uint*)(Buffer + OffEip);
        set => *(uint*)(Buffer + OffEip) = (uint)value;
    }

    public override ulong StackPointer => *(uint*)(Buffer + OffEsp);

    public override ulong FramePointer => *(uint*)(Buffer + OffEbp);

    public override uint EFlags
    {
        get => *(uint*)(Buffer + OffEFlags);
        set => *(uint*)(Buffer + OffEFlags) = value;
    }

    public override IReadOnlyList<(string Name, ulong Value)> General()
    =>
    [
        ("eax", At(OffEax)), ("ecx", At(OffEcx)), ("edx", At(OffEdx)), ("ebx", At(OffEbx)),
        ("esp", At(OffEsp)), ("ebp", At(OffEbp)), ("esi", At(OffEsi)), ("edi", At(OffEdi)),
        ("eip", InstructionPointer), ("eflags", (ulong)EFlags),
    ];

    private ulong At(int offset) => *(uint*)(Buffer + offset);
}
