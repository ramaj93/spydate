namespace Spydate.Core.PE;

/// <summary>WIN_CERTIFICATE.wCertificateType values.</summary>
public enum CertificateType : ushort
{
    X509 = 0x0001,
    /// <summary>PKCS#7 SignedData — what Authenticode actually uses.</summary>
    PkcsSignedData = 0x0002,
    Reserved = 0x0003,
    TerminalServerProtocolStack = 0x0004,
}

/// <summary>
/// Summary of the embedded code signature (the certificate table, which lives in the overlay
/// rather than in a section). Describes what is present; it does <b>not</b> verify that the
/// signature matches the file — that needs the full Authenticode hashing rules.
/// </summary>
public sealed record AuthenticodeSignature
{
    /// <summary>File offset of the WIN_CERTIFICATE structure.</summary>
    public required long Offset { get; init; }
    /// <summary>Length of the whole entry, including the 8-byte header.</summary>
    public required uint Length { get; init; }
    public required ushort Revision { get; init; }
    public required CertificateType Type { get; init; }

    /// <summary>Number of certificates in the PKCS#7 chain.</summary>
    public int CertificateCount { get; init; }
    public string? SignerSubject { get; init; }
    public string? SignerIssuer { get; init; }
    public string? SignerSerialNumber { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    /// <summary>Digest algorithm the signature covers the file with (SHA-256, SHA-1, …).</summary>
    public string? DigestAlgorithm { get; init; }
    /// <summary>Countersignature time, when the signature was timestamped.</summary>
    public DateTimeOffset? Timestamp { get; init; }
    /// <summary>Why the PKCS#7 blob could not be decoded, when it could not.</summary>
    public string? ParseError { get; init; }

    public bool IsPkcs7 => Type == CertificateType.PkcsSignedData;

    /// <summary>Whether the signing certificate is inside its validity window right now.</summary>
    public bool? CertificateCurrentlyValid => NotBefore is { } from && NotAfter is { } to
        ? DateTimeOffset.UtcNow >= from && DateTimeOffset.UtcNow <= to
        : null;

    public override string ToString() => SignerSubject is null
        ? $"{Type}, {Length:N0} bytes"
        : $"{SignerSubject} ({DigestAlgorithm})";
}
