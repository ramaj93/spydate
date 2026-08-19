using Spydate.Core.PE;

namespace Spydate.Disassembly;

/// <summary>Abstracts "give me bytes at this virtual address" so discovery does not depend on <see cref="PeImage"/> directly.</summary>
public interface ICodeSource
{
    ulong ImageBase { get; }

    int Bitness { get; }

    /// <summary>Reads up to <paramref name="length"/> bytes at <paramref name="va"/>; returns an empty memory when unmapped.</summary>
    ReadOnlyMemory<byte> Read(ulong va, int length);

    /// <summary>True when the address lies in an executable region.</summary>
    bool IsExecutable(ulong va);

    /// <summary>True when the address is inside the image's virtual range.</summary>
    bool IsMapped(ulong va);
}

/// <summary><see cref="ICodeSource"/> over a <see cref="PeImage"/>.</summary>
public sealed class PeCodeSource : ICodeSource
{
    private readonly PeImage _pe;

    public PeCodeSource(PeImage pe) => _pe = pe;

    public ulong ImageBase => _pe.ImageBase;

    public int Bitness => _pe.Bitness;

    public ReadOnlyMemory<byte> Read(ulong va, int length) => _pe.ReadAtVa(va, length);

    public bool IsExecutable(ulong va) => _pe.SectionFromVa(va)?.IsExecutable ?? false;

    public bool IsMapped(ulong va) => _pe.VaToRva(va) is { } rva && rva < _pe.OptionalHeader.SizeOfImage;
}
