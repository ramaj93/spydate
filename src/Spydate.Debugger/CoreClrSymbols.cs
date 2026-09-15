using System.Collections.Concurrent;
using System.Net.Http;
using Spydate.Core.PE;
using Spydate.Core.Pdb;

namespace Spydate.Debugger;

/// <summary>
/// Finds a symbol in the loaded runtime (coreclr.dll on .NET, clr.dll on Framework) by its address in
/// the image, reading the runtime's own PDB. The PDB is not shipped, so it is fetched once from the
/// Microsoft symbol server — by the exact build hash in the image's debug directory, which sends the
/// hash and nothing else — and cached on disk by build, so later runs need no network.
///
/// This exists for one thing: locating <c>PreStubWorker</c>, the JIT's shared entry, so the native loop
/// can catch a cold managed method's <em>first</em> call at the prestub (MIXED-MODE.md Phase 7). It is a
/// read of the runtime's public symbols, nothing more — no code runs in the target, and the DAC stays
/// read-only. Non-fatal throughout: any failure (no PDB, no network, a mismatch) returns 0, and the
/// caller falls back to planting a held breakpoint when its method next compiles, which catches a later
/// call rather than the first.
/// </summary>
public static class CoreClrSymbols
{
    private const string SymbolServer = "https://msdl.microsoft.com/download/symbols";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Resolved RVAs by (runtime path, symbol), so a second lookup for the same build is free.</summary>
    private static readonly ConcurrentDictionary<string, uint> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The RVA of <c>PreStubWorker</c> in the given runtime image, or 0 if it cannot be resolved. Add
    /// the module's loaded base to get the address to plant at.
    /// </summary>
    public static uint PreStubWorkerRva(string runtimePath) => SymbolRva(runtimePath, "PreStubWorker");

    /// <summary>The RVA of a named public symbol in the runtime image, or 0. Cached per build.</summary>
    public static uint SymbolRva(string runtimePath, string symbol)
    {
        string key = $"{runtimePath}\0{symbol}";
        if (Cache.TryGetValue(key, out uint cached))
        {
            return cached;
        }

        uint rva = Resolve(runtimePath, symbol);
        Cache[key] = rva;
        return rva;
    }

    private static uint Resolve(string runtimePath, string symbol)
    {
        try
        {
            if (!File.Exists(runtimePath))
            {
                return 0;
            }

            var image = PeImage.Load(runtimePath);
            var codeView = image.Debug.Select(d => d.CodeView).FirstOrDefault(c => c is not null);
            if (codeView is null)
            {
                return 0;
            }

            string? pdbPath = LocalPdb(codeView);
            if (pdbPath is null)
            {
                return 0;
            }

            var pdb = PdbFile.TryLoad(pdbPath, out _);
            if (pdb is null || !pdb.Matches(codeView))
            {
                return 0;
            }

            foreach (var s in pdb.PublicSymbols)
            {
                if (!string.Equals(s.Name, symbol, StringComparison.Ordinal))
                {
                    continue;
                }

                // A public symbol's address is section-relative; the section table turns it into an RVA.
                if (s.Segment == 0 || s.Segment > image.Sections.Count)
                {
                    return 0;
                }

                return image.Sections[s.Segment - 1].VirtualAddress + s.Offset;
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The PDB for this image, from the cache or fetched into it. Laid out the way the symbol server
    /// itself is — <c>&lt;name&gt;/&lt;guid+age&gt;/&lt;name&gt;</c> — so a cache shared with other tools
    /// interoperates. Null when there is no PDB to be had.
    /// </summary>
    private static string? LocalPdb(CodeViewInfo codeView)
    {
        string pdbName = Path.GetFileName(codeView.PdbPath);
        if (pdbName.Length == 0)
        {
            pdbName = "coreclr.pdb";
        }

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spydate", "symbols", pdbName, codeView.SymbolKey);
        string cached = Path.Combine(dir, pdbName);
        if (File.Exists(cached))
        {
            return cached;
        }

        // Beside the runtime, in case a matching PDB is already on disk (a self-contained publish, a
        // local build) — no download needed then.
        if (Path.GetDirectoryName(codeView.PdbPath) is { Length: > 0 } && File.Exists(codeView.PdbPath))
        {
            return codeView.PdbPath;
        }

        try
        {
            string url = $"{SymbolServer}/{pdbName}/{codeView.SymbolKey}/{pdbName}";
            using var response = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Directory.CreateDirectory(dir);
            string temp = cached + "." + Environment.ProcessId + ".part";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, cached, overwrite: true);
            return cached;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
