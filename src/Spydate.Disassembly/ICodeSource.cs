using Spydate.Core.Binary;

namespace Spydate.Disassembly;

/// <summary>Abstracts "give me bytes at this virtual address" so discovery does not depend on a container format directly.</summary>
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

/// <summary><see cref="ICodeSource"/> over any <see cref="IBinaryImage"/> — a PE today, an ELF next.</summary>
public sealed class ImageCodeSource : ICodeSource
{
    private readonly IBinaryImage _image;

    public ImageCodeSource(IBinaryImage image) => _image = image;

    public ulong ImageBase => _image.ImageBase;

    public int Bitness => _image.Bitness;

    public ReadOnlyMemory<byte> Read(ulong va, int length) => _image.ReadAtVa(va, length);

    public bool IsExecutable(ulong va) => _image.SectionFromVa(va)?.IsExecutable ?? false;

    public bool IsMapped(ulong va) => _image.VaToRva(va) is { } rva && rva < _image.ImageSize;
}
