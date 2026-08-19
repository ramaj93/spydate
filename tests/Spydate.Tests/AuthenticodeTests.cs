using Spydate.Core.PE;
using Xunit.Abstractions;

namespace Spydate.Tests;

/// <summary>The certificate table: present/absent, and what the embedded signature says.</summary>
public class AuthenticodeTests
{
    private readonly ITestOutputHelper _output;

    public AuthenticodeTests(ITestOutputHelper output) => _output = output;

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    [SkippableTheory]
    [InlineData("kernel32.dll")]
    [InlineData("user32.dll")]
    public void SystemBinariesAreSignedByMicrosoft(string fileName)
    {
        string path = Path.Combine(System32, fileName);
        Skip.IfNot(File.Exists(path), $"{fileName} not found");

        var pe = PeImage.Load(path);
        Skip.If(pe.Signature is null, $"{fileName} has no embedded signature (catalog-signed)");

        var signature = pe.Signature!;
        _output.WriteLine($"{fileName}: {signature.Length:N0} bytes at 0x{signature.Offset:X}, {signature.CertificateCount} certs, " +
                          $"digest {signature.DigestAlgorithm}, signer {signature.SignerSubject}, timestamp {signature.Timestamp}");

        Assert.Null(signature.ParseError);
        Assert.True(signature.IsPkcs7);
        Assert.Equal(0x0200, signature.Revision); // WIN_CERT_REVISION_2_0
        Assert.True(signature.CertificateCount > 0);
        Assert.Contains("Microsoft", signature.SignerSubject!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(signature.DigestAlgorithm);
        Assert.True(signature.NotBefore < signature.NotAfter);

        // The signature lives in the overlay, past every section.
        Assert.True(signature.Offset >= pe.Overlay.Offset, "the certificate table should be in the overlay");
        Assert.True(signature.Offset + signature.Length <= pe.Length);
    }

    [SkippableFact]
    public void TimestampPrecedesCertificateExpiry()
    {
        string path = Path.Combine(System32, "kernel32.dll");
        Skip.IfNot(File.Exists(path), "kernel32.dll not found");

        var pe = PeImage.Load(path);
        Skip.If(pe.Signature?.Timestamp is null, "signature is not timestamped");

        // Timestamping is what keeps a signature valid after the certificate expires, so the
        // countersignature time has to fall inside the certificate's window.
        var s = pe.Signature!;
        Assert.InRange(s.Timestamp!.Value, s.NotBefore!.Value, s.NotAfter!.Value);
    }

    [Fact]
    public void UnsignedImageHasNoSignature()
    {
        var pe = SyntheticPe.WithSectionData(new byte[] { 0x90 });
        Assert.Null(pe.Signature);
        Assert.Empty(pe.Warnings);
    }

    [Fact]
    public void CertificateTableOutsideTheFileIsAWarningNotACrash()
    {
        var full = File.ReadAllBytes(typeof(PeImage).Assembly.Location);
        var pe = PeImage.Parse(full);
        int dirOffset = (int)pe.NtHeadersOffset + 4 + CoffFileHeader.Size + (pe.Is64Bit ? 112 : 96) + (8 * (int)DataDirectoryIndex.Security);
        BitConverter.GetBytes(0x7FFF_0000u).CopyTo(full, dirOffset);
        BitConverter.GetBytes(0x1000u).CopyTo(full, dirOffset + 4);

        var corrupt = PeImage.Parse(full);

        Assert.Null(corrupt.Signature);
        Assert.Contains(corrupt.Warnings, w => w.Contains("Certificate table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TruncatedCertificateTableIsClamped()
    {
        var full = File.ReadAllBytes(typeof(PeImage).Assembly.Location);
        var pe = PeImage.Parse(full);
        int dirOffset = (int)pe.NtHeadersOffset + 4 + CoffFileHeader.Size + (pe.Is64Bit ? 112 : 96) + (8 * (int)DataDirectoryIndex.Security);

        // Point at the last 16 bytes of the file but claim a megabyte.
        BitConverter.GetBytes((uint)(full.Length - 16)).CopyTo(full, dirOffset);
        BitConverter.GetBytes(0x10_0000u).CopyTo(full, dirOffset + 4);
        BitConverter.GetBytes(0x10_0000u).CopyTo(full, full.Length - 16);   // length field
        BitConverter.GetBytes((ushort)0x0200).CopyTo(full, full.Length - 12); // revision
        BitConverter.GetBytes((ushort)CertificateType.PkcsSignedData).CopyTo(full, full.Length - 10);

        var corrupt = PeImage.Parse(full);

        Assert.NotNull(corrupt.Signature);
        Assert.True(corrupt.Signature!.Offset + corrupt.Signature.Length <= corrupt.Length);
        Assert.Contains(corrupt.Warnings, w => w.Contains("Certificate table", StringComparison.OrdinalIgnoreCase));
    }
}
