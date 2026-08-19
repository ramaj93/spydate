using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>
/// Test helper: a flat executable region at a fixed base address. Pass <paramref name="imageBase"/>
/// and <paramref name="imageSize"/> to model an image that is larger than the code itself, so data
/// references can land inside the image without being executable.
/// </summary>
internal sealed class MemoryCodeSource : ICodeSource
{
    private readonly byte[] _code;
    private readonly ulong _base;
    private readonly ulong _imageSize;

    public MemoryCodeSource(byte[] code, ulong baseVa, int bitness, ulong? imageBase = null, ulong imageSize = 0)
    {
        _code = code;
        _base = baseVa;
        Bitness = bitness;
        ImageBase = imageBase ?? baseVa;
        _imageSize = imageSize;
    }

    public ulong ImageBase { get; }

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

    public bool IsExecutable(ulong va) => va >= _base && va < _base + (ulong)_code.Length;

    public bool IsMapped(ulong va) => _imageSize == 0
        ? IsExecutable(va)
        : va >= ImageBase && va < ImageBase + _imageSize;
}
