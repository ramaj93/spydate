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
    /// The JIT prestub in a runtime image: the RVA to plant on, and which register holds the MethodDesc
    /// being compiled when it is hit. The two runtimes differ. CoreCLR has a free
    /// <c>PreStubWorker(TransitionBlock*, MethodDesc*)</c>, so the MethodDesc is the second argument — rdx
    /// on x64. .NET Framework has no such free function; its prestub is the member
    /// <c>MethodDesc::DoPrestub(MethodTable*)</c>, so the MethodDesc is <c>this</c> — rcx. Not resolvable
    /// (no PDB, no network) leaves <see cref="Ok"/> false and the caller falls back to a later call.
    /// </summary>
    public readonly record struct PrestubInfo(uint Rva, string MethodDescRegister)
    {
        public bool Ok => Rva != 0 && MethodDescRegister.Length > 0;

        public static PrestubInfo None => new(0, string.Empty);
    }

    /// <summary>
    /// The prestub of the runtime at <paramref name="runtimePath"/> — coreclr.dll or clr.dll. With
    /// <paramref name="allowFetch"/> false the PDB is read only if it is already on disk, never fetched
    /// from the symbol server: the debug loop resolves this way at a module-load stop, where the target
    /// is frozen and a network round-trip would hang it, so it must arm from a warm cache or not at all.
    /// </summary>
    public static PrestubInfo ResolvePrestub(string runtimePath, bool allowFetch = true)
    {
        uint rva = SymbolRva(runtimePath, "PreStubWorker", allowFetch);
        if (rva != 0)
        {
            return new PrestubInfo(rva, "rdx");
        }

        rva = SymbolRva(runtimePath, "?DoPrestub@MethodDesc@@QEAA_KPEAVMethodTable@@@Z", allowFetch);
        if (rva != 0)
        {
            return new PrestubInfo(rva, "rcx");
        }

        return PrestubInfo.None;
    }

    /// <summary>
    /// The RVA of <c>PreStubWorker</c> in the given runtime image, or 0 if it cannot be resolved. Add
    /// the module's loaded base to get the address to plant at. CoreCLR only — see
    /// <see cref="ResolvePrestub"/> for the runtime-agnostic form.
    /// </summary>
    public static uint PreStubWorkerRva(string runtimePath) => SymbolRva(runtimePath, "PreStubWorker");

    /// <summary>The RVA of a named public symbol in the runtime image, or 0. Cached per build. With
    /// <paramref name="allowFetch"/> false the PDB is not downloaded, only read if already on disk.</summary>
    public static uint SymbolRva(string runtimePath, string symbol, bool allowFetch = true)
    {
        string key = $"{runtimePath}\0{symbol}";
        if (Cache.TryGetValue(key, out uint cached))
        {
            return cached;
        }

        uint rva = Resolve(runtimePath, symbol, allowFetch);

        // A hit is cached always; a miss only when a fetch was allowed, so it was a definitive miss. A
        // no-fetch miss (the PDB simply was not on disk yet) is left uncached, so a later fetch retries.
        if (rva != 0 || allowFetch)
        {
            Cache[key] = rva;
        }

        return rva;
    }

    /// <summary>
    /// The PDB file for a module, from the shared cache or fetched from the symbol server into it —
    /// the same machinery the runtime symbols use, for any module. A file path, for a caller that
    /// wants to read the whole PDB (all its functions), not one symbol's RVA. Null when the module has
    /// no CodeView record, no PDB is to be had, or a fetch was disallowed and none is cached. The
    /// caller reads and matches it (GUID) itself, since matching depends on what it is for.
    /// </summary>
    public static string? PdbFor(string modulePath, bool allowFetch = true)
    {
        try
        {
            if (!File.Exists(modulePath))
            {
                return null;
            }

            var image = PeImage.Load(modulePath);
            var codeView = image.Debug.Select(d => d.CodeView).FirstOrDefault(c => c is not null);
            return codeView is null ? null : LocalPdb(codeView, allowFetch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static uint Resolve(string runtimePath, string symbol, bool allowFetch)
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

            string? pdbPath = LocalPdb(codeView, allowFetch);
            if (pdbPath is null)
            {
                return 0;
            }

            // Matched on the build GUID alone, not GUID-and-age. The GUID uniquely identifies the build;
            // the age is a revision counter that legitimately differs between a PE's debug directory and
            // the PDB the symbol server returns for it (a real case: clr.dll says age 3, its clr.pdb says
            // 4). Requiring both, as PdbFile.Matches does for its own stricter purpose, rejects the very
            // PDB the server keyed by this GUID.
            var pdb = PdbFile.TryLoad(pdbPath, out _);
            if (pdb is null || pdb.Guid != codeView.Guid)
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
    private static string? LocalPdb(CodeViewInfo codeView, bool allowFetch)
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

        // Not on disk. The loop-thread resolve stops here rather than reach for the network, which would
        // freeze the frozen target; the background resolve is the one that fetches and warms the cache.
        if (!allowFetch)
        {
            return null;
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
