using System.Runtime.InteropServices;

namespace Spydate.Debugger;

/// <summary>
/// One thread's registers, as x64 <c>CONTEXT</c>.
///
/// The buffer is allocated aligned rather than declared as a struct. <c>CONTEXT</c> contains the XMM
/// save area and the kernel requires 16-byte alignment; a managed struct on the stack or inside an
/// object gives whatever alignment the allocator felt like, and <c>GetThreadContext</c> answers a
/// misaligned one with a flat refusal that says nothing about why.
///
/// Fields are read by offset for the same reason the debug event is: declaring all 1232 bytes to use
/// eighteen of them is more code and more places to be wrong.
/// </summary>
public sealed unsafe class ThreadContext : IDisposable
{
    private const int Size = 1232;
    private const int Alignment = 16;

    // Offsets into CONTEXT_AMD64.
    private const int OffContextFlags = 0x30;
    private const int OffEFlags = 0x44;
    private const int OffRax = 0x78;
    private const int OffRip = 0xF8;

    private byte* _buffer;

    public ThreadContext()
    {
        _buffer = (byte*)NativeMemory.AlignedAlloc(Size, Alignment);
        NativeMemory.Clear(_buffer, Size);
        *(uint*)(_buffer + OffContextFlags) = Native.CONTEXT_AMD64_FULL;
    }

    /// <summary>The general-purpose registers, in the order a person reads them.</summary>
    public static readonly IReadOnlyList<string> GeneralNames =
    [
        "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
        "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15",
    ];

    public bool Read(IntPtr thread)
    {
        *(uint*)(_buffer + OffContextFlags) = Native.CONTEXT_AMD64_FULL;
        return Native.GetThreadContext(thread, _buffer);
    }

    public bool Write(IntPtr thread)
    {
        *(uint*)(_buffer + OffContextFlags) = Native.CONTEXT_AMD64_FULL;
        return Native.SetThreadContext(thread, _buffer);
    }

    public ulong Rip
    {
        get => *(ulong*)(_buffer + OffRip);
        set => *(ulong*)(_buffer + OffRip) = value;
    }

    public uint EFlags
    {
        get => *(uint*)(_buffer + OffEFlags);
        set => *(uint*)(_buffer + OffEFlags) = value;
    }

    /// <summary>Rax is first and the rest follow in the order <see cref="GeneralNames"/> gives.</summary>
    public ulong this[int index]
    {
        get => index >= 0 && index < GeneralNames.Count
            ? *(ulong*)(_buffer + OffRax + (index * 8))
            : throw new ArgumentOutOfRangeException(nameof(index));
        set
        {
            if (index < 0 || index >= GeneralNames.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            *(ulong*)(_buffer + OffRax + (index * 8)) = value;
        }
    }

    public ulong this[string name]
    {
        get
        {
            int index = IndexOf(name);
            return index >= 0 ? this[index] : name.Equals("rip", StringComparison.OrdinalIgnoreCase) ? Rip : 0;
        }
    }

    /// <summary>Every general register with its name, for a panel to show.</summary>
    public IReadOnlyList<(string Name, ulong Value)> General()
        => GeneralNames.Select((n, i) => (n, this[i])).ToList();

    private static int IndexOf(string name)
    {
        for (int i = 0; i < GeneralNames.Count; i++)
        {
            if (GeneralNames[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Asks the processor to fault after one instruction.</summary>
    public void SetTrapFlag(bool on) => EFlags = on ? EFlags | Native.TrapFlag : EFlags & ~Native.TrapFlag;

    public void Dispose()
    {
        if (_buffer is not null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }
    }
}
