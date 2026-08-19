using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>Test helper: a flat executable region at a fixed base address.</summary>
internal sealed class MemoryCodeSource : ICodeSource
{
    private readonly byte[] _code;
    private readonly ulong _base;

    public MemoryCodeSource(byte[] code, ulong baseVa, int bitness)
    {
        _code = code;
        _base = baseVa;
        Bitness = bitness;
    }

    public ulong ImageBase => _base;

    public int Bitness { get; }

    public ReadOnlyMemory<byte> Read(ulong va, int length)
    {
        if (va < _base || va >= _base + (ulong)_code.Length)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        int offset = (int)(va - _base);
        return _code.AsMemory(offset, Math.Min(length, _code.Length - offset));
    }

    public bool IsExecutable(ulong va) => IsMapped(va);

    public bool IsMapped(ulong va) => va >= _base && va < _base + (ulong)_code.Length;
}
