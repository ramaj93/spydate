using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Spydate.Agent.Secrets;

/// <summary>
/// Where API keys live. An interface because the real one encrypts to the Windows account, which is
/// untestable anywhere else and undesirable in a test run even here.
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret under a name, or null when there is none.</summary>
    string? Get(string name);

    /// <summary>Stores a secret, or forgets it when the value is null or empty.</summary>
    void Set(string name, string? value);

    /// <summary>Names that have a secret. Never the secrets themselves.</summary>
    IReadOnlyList<string> Names();
}

/// <summary>
/// Keys encrypted to the current Windows account, under <c>%LOCALAPPDATA%\Spydate\secrets</c>.
///
/// DPAPI rather than a passphrase: the key is already only as safe as the account, and asking for a
/// passphrase on every launch would push people towards a plain text file instead. Copying the file
/// to another machine or account yields nothing, which is the property that matters.
///
/// One secret per file, named after the provider. A single file would mean decrypting every key to
/// read one, and a corrupt one would lose all of them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>
    /// Mixed into the encryption so a blob lifted out of this folder is not accepted by anything else
    /// on the machine that happens to use DPAPI.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Spydate.Agent.ProviderKey.v1");

    private readonly string _directory;

    public DpapiSecretStore(string? directory = null)
        => _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spydate",
            "secrets");

    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Written by another account, or damaged. Either way there is no key here, which is what
            // the caller needs to know; throwing would only turn a missing key into a crash.
            return null;
        }
    }

    public void Set(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string path = PathFor(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(_directory);
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);

        // Written and moved into place, so an interrupted write cannot leave a half-file that
        // decrypts to nothing and reads as "no key configured".
        string temporary = $"{path}.{Environment.ProcessId:X}.tmp";
        File.WriteAllBytes(temporary, cipher);
        File.Move(temporary, path, overwrite: true);
    }

    public IReadOnlyList<string> Names()
        => Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "*.key").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    /// <summary>A provider name is ours, not the user's, but it still builds a path, so it is checked.</summary>
    private string PathFor(string name)
    {
        if (name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' is not usable as a file name", nameof(name));
        }

        return Path.Combine(_directory, name + ".key");
    }
}

/// <summary>Keys held only for as long as the process runs. For tests, and for nothing else.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public string? Get(string name) => _secrets.TryGetValue(name, out string? value) ? value : null;

    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _secrets.Remove(name);
        }
        else
        {
            _secrets[name] = value;
        }
    }

    public IReadOnlyList<string> Names() => _secrets.Keys.Order(StringComparer.Ordinal).ToList();
}
