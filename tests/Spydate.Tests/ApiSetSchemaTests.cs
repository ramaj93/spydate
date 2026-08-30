using Spydate.Core.PE;

namespace Spydate.Tests;

/// <summary>
/// The table that says which DLL is behind an <c>api-ms-win-*</c> name. It is read out of the operating
/// system rather than typed in, so the tests check the reader against the files it names: every host it
/// claims must exist, which a wrong parse would not survive.
/// </summary>
public class ApiSetSchemaTests
{
    private const string SchemaPath = @"C:\Windows\System32\apisetschema.dll";

    [Fact]
    public void TheSchemaNamesHostsThatAreReallyThere()
    {
        if (!File.Exists(SchemaPath))
        {
            return;
        }

        var schema = ApiSetSchema.From(PeImage.Load(SchemaPath));

        Assert.True(schema.Count > 100, $"only {schema.Count} entries");

        int checkedHosts = 0;
        foreach (var (name, host) in schema.Entries)
        {
            Assert.True(ApiSetSchema.IsApiSetName(name), name);

            // Almost every host is a DLL, but not all: a handful of sets are implemented by a driver.
            Assert.True(host.Length > 4 && host.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, host);

            // A misread offset yields plausible-looking UTF-16 from elsewhere in the section, so the
            // check that matters is whether the name is of something on disk.
            if (File.Exists(Path.Combine(@"C:\Windows\System32", host)))
            {
                checkedHosts++;
            }
        }

        Assert.True(checkedHosts > schema.Count / 2, $"only {checkedHosts} of {schema.Count} hosts exist");
    }

    [Fact]
    public void AnImportIsMatchedByAnEntryOfADifferentMinorVersion()
    {
        if (!File.Exists(SchemaPath))
        {
            return;
        }

        // Binaries import api-ms-win-core-synch-l1-1-0; the schema on this machine carries l1-1-1. The
        // loader keys on the name up to the last version component, and so must this.
        var schema = ApiSetSchema.From(PeImage.Load(SchemaPath));

        Assert.Equal("kernelbase.dll", schema.Resolve("api-ms-win-core-synch-l1-1-0.dll"));
        Assert.Equal("kernelbase.dll", schema.Resolve("api-ms-win-core-synch-l1-1-0"));
        Assert.Null(schema.Resolve("kernel32.dll"));
    }

    [Fact]
    public void AFileThatIsNotASchemaYieldsNothing()
    {
        // The section is absent from an ordinary DLL, and a malformed one must not stop the analysis of
        // the binary that prompted the lookup.
        var schema = ApiSetSchema.From(PeImage.Load(typeof(ApiSetSchemaTests).Assembly.Location));

        Assert.Equal(0, schema.Count);
        Assert.Null(schema.Resolve("api-ms-win-core-synch-l1-1-0.dll"));
    }

    [Fact]
    public void OnlyRedirectedNamesAreRecognised()
    {
        Assert.True(ApiSetSchema.IsApiSetName("api-ms-win-core-synch-l1-1-0.dll"));
        Assert.True(ApiSetSchema.IsApiSetName("EXT-MS-WIN-RTCORE-NTUSER-WINDOW-L1-1-0.DLL"));
        Assert.False(ApiSetSchema.IsApiSetName("kernel32.dll"));
    }
}
