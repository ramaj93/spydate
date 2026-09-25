using System.Globalization;
using Spydate.App.Services;
using Spydate.Core.Android;
using Spydate.Core.Binary;
using Spydate.Core.Elf;
using Spydate.Core.Jvm;
using Spydate.Core.PE;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>Summary of the loaded file: identity, key header facts, debug info, warnings.</summary>
public sealed class OverviewDocumentViewModel : DocumentViewModel
{
    public OverviewDocumentViewModel(OpenedBinary binary) : base("overview", "Overview", SymbolRegular.Info24)
    {
        if (binary.Image is ElfImage elf)
        {
            FileName = elf.FileName;
            FilePath = elf.Path ?? "(memory)";
            Kind = $"{(elf.IsLibrary ? "Shared object" : "Linux executable")} ({ArchitectureName(elf)})";
            General = ElfGeneral(elf, binary);
            Security = ElfDocuments.Hardening(elf).ToList();
            SecurityTitle = "Hardening";
            Version = new List<PropertyRow>();
            Signature = new List<PropertyRow>();
            BuildTitle = "Build";
            Build = new List<PropertyRow>
            {
                new("Build ID", elf.BuildId ?? "(none)", elf.BuildId is null ? "the project file is matched on a hash of the headers instead" : "GNU build-id note"),
            };
            Build.AddRange(elf.SectionHeaders.Where(s => s.Name is ".comment").Select(s => new PropertyRow("Compiler", Comment(elf, s))));
            Debug = elf.SectionHeaders
                .Where(s => s.Name.StartsWith(".debug_", StringComparison.Ordinal) || s.Name == ".gnu_debuglink")
                .Select(s => new PropertyRow(s.Name, $"{s.Size:N0} bytes", s.Name == ".gnu_debuglink" ? "debug info is in a separate file" : null))
                .ToList();
            Warnings = elf.Warnings.ToList();
            if (binary.Analysis is null)
            {
                Warnings.Insert(0, $"{elf.Header.MachineName} code is not something the native disassembler reads (x86, x64 and ARM64 only); the structure is still shown.");
            }

            return;
        }

        if (binary.Image is JarImage jar)
        {
            FileName = jar.FileName;
            FilePath = jar.Path ?? "(memory)";
            var files = jar.Archive.Entries.Where(e => !e.IsDirectory).ToList();
            int classes = files.Count(e => e.Name.EndsWith(".class", StringComparison.OrdinalIgnoreCase));
            Kind = jar.IsLibrary ? "Java library (JAR)" : "Java application (JAR)";
            General = new List<PropertyRow>
            {
                new("File", jar.Path ?? "(memory)"),
                new("Size", $"{jar.Length:N0} bytes"),
                new("Entries", $"{files.Count:N0} files", $"{classes:N0} class files, {files.Count - classes:N0} other"),
                new("Main-Class", jar.Manifest?["Main-Class"] ?? "(none)", jar.IsLibrary ? "a library: java -jar has nothing to run" : "what java -jar runs"),
                new("Module", jar.ModuleName ?? "(none)", jar.ModuleName is null ? "on the class path, or an automatic module" : "declared by module-info.class"),
                new("Fingerprint", jar.Fingerprint, "a hash of the zip directory; the project file is matched on it"),
            };

            ManagedTitle = "Java";
            Managed = new List<PropertyRow>();
            if (binary.Bytecode is { } reading)
            {
                Managed.Add(new PropertyRow("Archive", reading.FullName));
                Managed.Add(new PropertyRow("Needs", reading.Platform, $"the newest class file is version {reading.FormatVersion}"));
                Managed.Add(new PropertyRow("Packages", reading.Namespaces.Count.ToString(CultureInfo.InvariantCulture)));
                Managed.Add(new PropertyRow("Classes", jar.Classes.Count.ToString(CultureInfo.InvariantCulture), jar.VersionedClasses > 0 ? $"+{jar.VersionedClasses} multi-release versions not shown" : null));
                if (reading.EntryPoint is { } main)
                {
                    Managed.Add(new PropertyRow("Entry point", main.Signature));
                }

                if (reading.Requires.Count > 0)
                {
                    Managed.Add(new PropertyRow("Class-Path", string.Join(", ", reading.Requires)));
                }
            }

            VersionTitle = "Manifest";
            Version = jar.Manifest?.Main.Select(a => new PropertyRow(a.Key, a.Value)).ToList() ?? new List<PropertyRow>();
            Security = new List<PropertyRow>();
            Signature = jar.Archive.Entries
                .Where(e => e.Name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                            && (e.Name.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
                                || e.Name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)))
                .Select(e => new PropertyRow("Signature file", e.Name, "the JAR is signed; the signature is listed, not verified"))
                .ToList();
            Build = new List<PropertyRow>();
            BuildTitle = "Build";
            foreach (string key in (string[])["Created-By", "Build-Jdk", "Build-Jdk-Spec", "Built-By", "Bnd-LastModified"])
            {
                if (jar.Manifest?[key] is { } value)
                {
                    Build.Add(new PropertyRow(key, value));
                }
            }

            Debug = new List<PropertyRow>();
            Warnings = jar.Warnings.ToList();
            return;
        }

        if (binary.Image is ApkImage apk)
        {
            FileName = apk.FileName;
            FilePath = apk.Path ?? "(memory)";
            var files = apk.Archive?.Entries.Where(e => !e.IsDirectory).ToList() ?? [];
            var manifest = apk.Manifest;
            Kind = !apk.IsPackage ? "Android DEX file: an app's code without its package"
                : apk.IsLibrary ? "Android package (APK), no launcher activity" : "Android app (APK)";
            General = new List<PropertyRow>
            {
                new("File", apk.Path ?? "(memory)"),
                new("Size", $"{apk.Length:N0} bytes"),
            };
            if (apk.IsPackage)
            {
                General.Add(new("Entries", $"{files.Count:N0} files", $"{apk.DexFiles.Count} DEX file(s), {apk.NativeLibraries.Count} native libraries{(apk.HasResourceTable ? ", a resource table" : string.Empty)}"));
                General.Add(new("Package", manifest?.Package ?? "(no readable manifest)", manifest?.VersionName is { } versionName ? $"version {versionName} (code {manifest.VersionCode})" : null));
                General.Add(new("Launches", manifest?.MainActivity ?? "(none)", "the activity the launcher starts"));
            }

            if (apk.Resources is { } resources)
            {
                General.Add(new("Resources", $"{resources.Entries.Select(e => e.Id).Distinct().Count():N0} resources",
                    $"{resources.Entries.Count:N0} values in {resources.Entries.Select(e => e.Type).Distinct(StringComparer.Ordinal).Count()} types"));
            }

            General.Add(new("Fingerprint", apk.Fingerprint, apk.IsPackage ? "a hash of the zip directory; the project file is matched on it" : "a hash of the file; the project file is matched on it"));

            ManagedTitle = "Android";
            Managed = new List<PropertyRow>();
            if (binary.Bytecode is { } reading)
            {
                Managed.Add(new PropertyRow("App", reading.FullName));
                Managed.Add(new PropertyRow("Needs", reading.Platform, manifest is null ? "what its DEX version needs; there is no manifest to say more" : $"target SDK {manifest.TargetSdk ?? "?"}, compiled against {manifest.CompileSdk ?? "?"}"));
                Managed.Add(new PropertyRow("Code", $"{apk.Classes.Count:N0} classes", $"{reading.Namespaces.Count} packages, {reading.FormatVersion}"));
                if (reading.EntryPoint is { } main)
                {
                    Managed.Add(new PropertyRow("Entry point", main.Signature));
                }
            }

            if (manifest is not null)
            {
                Managed.Add(new PropertyRow("Debuggable", manifest.Debuggable ? "yes" : "no", manifest.Debuggable ? "android:debuggable is set: anyone can attach a debugger" : null));
                foreach (var group in manifest.Components.GroupBy(c => c.Kind))
                {
                    Managed.Add(new PropertyRow(char.ToUpperInvariant(group.Key[0]) + group.Key[1..] + "s", group.Count().ToString(CultureInfo.InvariantCulture),
                        string.Join(", ", group.Where(c => c.Exported).Select(c => c.Name.Split('.')[^1] + " (exported)").Take(4))));
                }
            }

            VersionTitle = "Permissions";
            Version = manifest?.Permissions.Select(p => new PropertyRow("uses-permission", p)).ToList() ?? new List<PropertyRow>();
            Security = new List<PropertyRow>();
            Signature = (apk.Archive?.Entries ?? [])
                .Where(e => e.Name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                            && (e.Name.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
                                || e.Name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)))
                .Select(e => new PropertyRow("Signature file", e.Name, "a v1 (JAR) signature; listed, not verified — newer APKs sign in a block this does not read"))
                .ToList();
            BuildTitle = "Native libraries";
            Build = apk.NativeLibraries.Select(l => new PropertyRow(l.Abi, l.Name, $"{l.Entry.Size:N0} bytes")).ToList();
            Debug = new List<PropertyRow>();
            Warnings = apk.Warnings.ToList();
            return;
        }

        var pe = binary.Image as PeImage ?? throw new NotSupportedException($"There is no overview for {binary.Image.Format} files.");
        FileName = pe.FileName;
        FilePath = pe.Path ?? "(memory)";
        Kind = Describe(pe);

        General = new List<PropertyRow>
        {
            new("File", pe.Path ?? "(memory)"),
            new("Size", $"{pe.Length:N0} bytes"),
            new("Machine", $"{pe.Machine} (0x{(ushort)pe.Machine:X4})"),
            new("Format", pe.Is64Bit ? "PE32+ (64-bit)" : "PE32 (32-bit)"),
            new("Type", pe.IsDll ? "DLL" : pe.FileHeader.Characteristics.HasFlag(ImageCharacteristics.ExecutableImage) ? "Executable" : "Object/other"),
            new("Subsystem", pe.Subsystem.ToString()),
            new("Image base", $"0x{pe.ImageBase:X}"),
            new("Entry point", pe.EntryPointRva == 0 ? "(none)" : $"RVA 0x{pe.EntryPointRva:X} → VA 0x{pe.EntryPointVa:X}"),
            new("Size of image", $"0x{pe.OptionalHeader.SizeOfImage:X}"),
            new("Sections", pe.Sections.Count.ToString(CultureInfo.InvariantCulture)),
            new("Timestamp", pe.FileHeader.Timestamp is { } ts ? $"{ts:yyyy-MM-dd HH:mm:ss} UTC (0x{pe.FileHeader.TimeDateStamp:X8})" : $"0x{pe.FileHeader.TimeDateStamp:X8}"),
            new("Linker", $"{pe.OptionalHeader.MajorLinkerVersion}.{pe.OptionalHeader.MinorLinkerVersion}"),
            new("OS version", $"{pe.OptionalHeader.MajorOperatingSystemVersion}.{pe.OptionalHeader.MinorOperatingSystemVersion}"),
            new("Subsystem version", $"{pe.OptionalHeader.MajorSubsystemVersion}.{pe.OptionalHeader.MinorSubsystemVersion}"),
            new("Characteristics", pe.FileHeader.Characteristics.ToString()),
            new("DLL characteristics", pe.OptionalHeader.DllCharacteristics.ToString()),
            new("Checksum", $"0x{pe.OptionalHeader.CheckSum:X8}"),
            new("Imports", $"{pe.Imports.Count} modules, {pe.Imports.Sum(m => m.Functions.Count)} functions" + (pe.DelayImports.Count > 0 ? $" (+{pe.DelayImports.Count} delay-load modules)" : string.Empty)),
            new("Exports", pe.Exports is { } ex ? $"{ex.Entries.Count} entries ({ex.Name})" : "(none)"),
            new("Resources", pe.Resources is { Children.Count: > 0 } res ? $"{res.Children.Count} types" : "(none)"),
            new("Relocations", pe.Relocations.Count > 0 ? $"{pe.RelocationCount:N0} fix-ups in {pe.Relocations.Count:N0} blocks" : "(none)"),
            new("Signature", pe.Signature is { } s ? $"{s.CertificateCount} certificate(s), {s.Length:N0} bytes" : "(unsigned or catalog-signed)"),
            new("Overlay", pe.Overlay.Length > 0 ? $"{pe.Overlay.Length:N0} bytes at 0x{pe.Overlay.Offset:X}" : "(none)"),
        };

        Security = new List<PropertyRow>();
        if (pe.LoadConfig is { } config)
        {
            Security.Add(new PropertyRow("Load config size", $"0x{config.Size:X}"));
            Security.Add(new PropertyRow("Security cookie", config.SecurityCookieVa == 0 ? "(none)" : $"0x{config.SecurityCookieVa:X}"));
            Security.Add(new PropertyRow("Guard flags", config.GuardFlags == GuardFlags.None ? "(none)" : config.GuardFlags.ToString()));
            Security.Add(new PropertyRow(
                "CFG targets",
                config.GuardCfFunctionRvas.Count > 0 ? $"{config.GuardCfFunctionRvas.Count:N0} valid call targets" : "(none)",
                config.GuardCfFunctionTableVa == 0 ? null : $"table at 0x{config.GuardCfFunctionTableVa:X}, {config.GuardCfFunctionTableStride} bytes/entry"));
            if (config.SeHandlerRvas.Count > 0)
            {
                Security.Add(new PropertyRow("SafeSEH handlers", $"{config.SeHandlerRvas.Count:N0}", $"table at 0x{config.SeHandlerTableVa:X}"));
            }
        }

        if (pe.Tls is { } tls)
        {
            Security.Add(new PropertyRow("TLS data", $"0x{tls.StartAddressOfRawData:X} … 0x{tls.EndAddressOfRawData:X}", $"{tls.RawDataSize:N0} bytes + {tls.SizeOfZeroFill:N0} zero-fill"));
            Security.Add(new PropertyRow(
                "TLS callbacks",
                tls.CallbackVas.Count == 0 ? "(none)" : string.Join(", ", tls.CallbackVas.Select(v => $"0x{v:X}")),
                tls.CallbackVas.Count > 0 ? "run before the entry point" : null));
        }

        Version = new List<PropertyRow>();
        if (FindVersionInfo(pe) is { } version)
        {
            Version.Add(new PropertyRow("File version", version.FileVersion?.ToString() ?? "(none)"));
            Version.Add(new PropertyRow("Product version", version.ProductVersion?.ToString() ?? "(none)"));
            foreach (var table in version.StringTables)
            {
                foreach (var (name, value) in table.Strings)
                {
                    Version.Add(new PropertyRow(name, value, table.LanguageCodePage));
                }
            }
        }

        Signature = new List<PropertyRow>();
        if (pe.Signature is { } sig)
        {
            Signature.Add(new PropertyRow("Format", $"{sig.Type} (revision 0x{sig.Revision:X4})", $"{sig.Length:N0} bytes at 0x{sig.Offset:X}"));
            if (sig.ParseError is { } error)
            {
                Signature.Add(new PropertyRow("Signature", "could not be decoded", error));
            }
            else
            {
                Signature.Add(new PropertyRow("Signed by", sig.SignerSubject ?? "(unknown)"));
                Signature.Add(new PropertyRow("Issued by", sig.SignerIssuer ?? "(unknown)"));
                Signature.Add(new PropertyRow("Serial", sig.SignerSerialNumber ?? "(none)"));
                Signature.Add(new PropertyRow("Digest", sig.DigestAlgorithm ?? "(unknown)", $"{sig.CertificateCount} certificate(s) in the chain"));
                Signature.Add(new PropertyRow(
                    "Certificate valid",
                    sig.NotBefore is { } from && sig.NotAfter is { } to ? $"{from:yyyy-MM-dd} … {to:yyyy-MM-dd}" : "(unknown)",
                    sig.CertificateCurrentlyValid switch { true => "current", false => "expired or not yet valid", null => null }));
                Signature.Add(new PropertyRow("Timestamped", sig.Timestamp is { } signedAt ? $"{signedAt:yyyy-MM-dd HH:mm:ss} UTC" : "(not timestamped)"));
            }

            // Being explicit: parsing the certificate is not the same as checking the file's hash.
            Signature.Add(new PropertyRow("Verification", "not performed", "the embedded certificate is described, not validated against the file"));
        }

        Build = new List<PropertyRow>();
        if (pe.RichHeader is { } richHeader)
        {
            Build.Add(new PropertyRow(
                "Checksum",
                richHeader.IsChecksumValid ? "valid" : "MISMATCH",
                richHeader.IsChecksumValid
                    ? $"0x{richHeader.Checksum:X8} — the header is the linker's own"
                    : $"stored 0x{richHeader.Checksum:X8}, computed 0x{richHeader.ComputedChecksum:X8} — edited or forged"));
        }

        Build.AddRange(pe.RichHeader is { } rich
            ? rich.Entries
                .OrderByDescending(e => e.UseCount)
                .Select(e => new PropertyRow(
                    $"tool 0x{e.ProductId:X4}",
                    $"{e.UseCount:N0} object{(e.UseCount == 1 ? string.Empty : "s")}",
                    e.BuildNumber == 0 ? "no build stamp" : $"build {e.BuildNumber}"))
                .ToList()
            : new List<PropertyRow>());

        if (pe.ClrHeader is { } clr)
        {
            Managed = new List<PropertyRow>
            {
                new("Runtime version", $"{clr.MajorRuntimeVersion}.{clr.MinorRuntimeVersion}"),
                new("Flags", clr.Flags.ToString()),
                new("Metadata", $"RVA 0x{clr.MetaData.Rva:X}, size 0x{clr.MetaData.Size:X}"),
                new("Entry point token", clr.Flags.HasFlag(CorFlags.NativeEntryPoint) ? $"native RVA 0x{clr.EntryPointTokenOrRva:X}" : $"0x{clr.EntryPointTokenOrRva:X8}"),
                new("Resources", clr.Resources.IsPresent ? $"RVA 0x{clr.Resources.Rva:X}, size 0x{clr.Resources.Size:X}" : "(none)"),
                new("Strong name", clr.StrongNameSignature.IsPresent ? $"RVA 0x{clr.StrongNameSignature.Rva:X}, size 0x{clr.StrongNameSignature.Size:X}" : "(none)"),
            };

            if (binary.Bytecode is { } m)
            {
                Managed.Add(new PropertyRow("Assembly", m.FullName));
                Managed.Add(new PropertyRow("Target framework", m.Platform));
                Managed.Add(new PropertyRow("Metadata version", m.FormatVersion));
                Managed.Add(new PropertyRow("Types", m.Namespaces.Sum(n => n.Types.Count).ToString(CultureInfo.InvariantCulture)));
            }
            else if (binary.ManagedLoadError is { } err)
            {
                Managed.Add(new PropertyRow("Decompiler", $"failed to load: {err}"));
            }
        }
        else if (binary.Bytecode is Decompiler.Jvm.JvmReading { Jar: { } embedded } java)
        {
            // A launcher: the native code only starts a JVM, and the program is the JAR appended to it.
            Kind = $"Java application in a native launcher ({ArchitectureName(pe)})";
            ManagedTitle = "Java (the embedded JAR)";
            Managed = new List<PropertyRow>
            {
                new("Launcher", "this PE starts a JVM on the JAR appended to it", "the native code is the launcher; the program is under Java packages"),
                new("Archive", java.FullName),
                new("Needs", java.Platform, $"the newest class file is version {java.FormatVersion}"),
                new("Classes", embedded.Classes.Count.ToString(CultureInfo.InvariantCulture), $"{java.Namespaces.Count} packages"),
                new("Main-Class", embedded.Manifest?["Main-Class"] ?? "(none)", java.EntryPoint?.Signature),
            };
        }

        if (binary.Analysis?.Pdb is { } pdb)
        {
            Security.Add(new PropertyRow(
                "PDB symbols",
                pdb.Loaded ? $"{pdb.SymbolsAdded:N0} loaded" : "not loaded",
                pdb.Loaded ? pdb.Path : pdb.Reason));
        }

        Debug = pe.Debug.Select(d => new PropertyRow(
            d.Type.ToString(),
            d.CodeView is { } cv ? cv.PdbPath : $"size 0x{d.SizeOfData:X} at 0x{d.PointerToRawData:X}",
            d.CodeView is { } cv2 ? $"{cv2.Guid:D} age {cv2.Age}" : null)).ToList();

        Warnings = pe.Warnings.ToList();
        if (binary.Analysis is null && !pe.IsManaged)
        {
            Warnings.Insert(0, $"Machine type {pe.Machine} is not supported by the native disassembler (x86, x64 and ARM64 only).");
        }
    }

    public string FileName { get; }
    public string FilePath { get; }
    public string Kind { get; }
    public List<PropertyRow> General { get; }
    public List<PropertyRow>? Managed { get; }
    public bool HasManaged => Managed is not null;
    /// <summary>Mitigations and loader-visible security data (load config, CFG, TLS).</summary>
    public List<PropertyRow> Security { get; }

    /// <summary>What that group is called: a PE's is load config and TLS, an ELF's is how it was hardened.</summary>
    public string SecurityTitle { get; } = "Security and TLS";

    /// <summary>The bytecode section's heading: the .NET runtime's for an assembly, Java's for a JAR.</summary>
    public string ManagedTitle { get; } = ".NET / CLR";

    public string VersionTitle { get; } = "Version info";

    public string BuildTitle { get; } = "Build toolchain (Rich header)";
    public bool HasSecurity => Security.Count > 0;
    /// <summary>Decoded VS_VERSIONINFO resource.</summary>
    public List<PropertyRow> Version { get; }
    public bool HasVersion => Version.Count > 0;
    /// <summary>Embedded Authenticode signature, described but not verified.</summary>
    public List<PropertyRow> Signature { get; }
    public bool HasSignature => Signature.Count > 0;
    /// <summary>Toolchain stamp decoded from the Rich header.</summary>
    public List<PropertyRow> Build { get; }
    public bool HasBuild => Build.Count > 0;
    public List<PropertyRow> Debug { get; }
    public bool HasDebug => Debug.Count > 0;
    public List<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;

    private static List<PropertyRow> ElfGeneral(ElfImage elf, OpenedBinary binary)
    {
        var rows = new List<PropertyRow>
        {
            new("File", elf.Path ?? "(memory)"),
            new("Size", $"{elf.Length:N0} bytes"),
            new("Machine", $"{elf.Header.MachineName} ({elf.Header.Machine})"),
            new("Format", $"{elf.Header.ClassName}, {elf.Header.ByteOrder}, {elf.Header.OsAbiName}"),
            new("Type", elf.Kind),
            new("Image base", $"0x{elf.ImageBase:X}"),
            new("Entry point", elf.EntryPointRva == 0 ? "(none)" : $"RVA 0x{elf.EntryPointRva:X} → VA 0x{elf.EntryPointVa:X}"),
            new("Size of image", $"0x{elf.ImageSize:X}"),
            new("Segments", elf.Segments.Count.ToString(CultureInfo.InvariantCulture), $"{elf.Segments.Count(s => s.IsLoad)} loadable"),
            new("Sections", elf.SectionHeaders.Count.ToString(CultureInfo.InvariantCulture)),
            new("Interpreter", elf.Interpreter ?? "(none)", elf.Interpreter is null && elf.Dynamic.Count == 0 ? "statically linked" : null),
            new("Libraries", elf.Needed.Count == 0 ? "(none)" : string.Join(", ", elf.Needed)),
            new("Imports", $"{elf.Imports.Count:N0} symbols", elf.PltStubs.Count > 0 ? $"{elf.PltStubs.Count:N0} called through PLT stubs" : null),
            new("Exports", elf.Exports.Count == 0 ? "(none)" : $"{elf.Exports.Count:N0} symbols", elf.SoName is { } soname ? $"SONAME {soname}" : null),
            new("Symbols", elf.StaticSymbols.Count > 0 ? $"{elf.StaticSymbols.Count:N0} in .symtab" : "stripped (no .symtab)", $"{elf.DynamicSymbols.Count:N0} in .dynsym"),
            new("Unwind table", elf.UnwindRanges.Count > 0 ? $"{elf.UnwindRanges.Count:N0} functions in .eh_frame" : "(none)"),
            new("Debugging", "not available", "the debugger runs Windows programs; an ELF is read, not run"),
        };

        if (binary.Analysis is not null)
        {
            rows.Insert(rows.Count - 1, new PropertyRow("Calling convention", Spydate.Disassembly.CallingConvention.For(elf).Name));
        }

        return rows;
    }

    /// <summary><c>.comment</c> holds the compiler's version strings, NUL-separated.</summary>
    private static string Comment(ElfImage elf, ElfSectionHeader section)
    {
        var text = System.Text.Encoding.UTF8.GetString(elf.SectionBytes(section)[..Math.Min((int)section.Size, 1024)]);
        return string.Join(" · ", text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());
    }

    private static string ArchitectureName(IBinaryImage image) => image.Architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "ARM64",
        Architecture.Arm => "ARM",
        _ => image is ElfImage elf ? elf.Header.MachineName : "unknown",
    };

    /// <summary>First RT_VERSION leaf in the resource tree, decoded.</summary>
    private static VersionInfo? FindVersionInfo(PeImage pe)
    {
        var type = pe.Resources?.Children?.FirstOrDefault(c => c.Name is null && c.Id == (uint)ResourceType.Version);
        var leaf = FirstLeaf(type);
        return leaf is null ? null : ResourceDecoder.ReadVersionInfo(ResourceDecoder.ReadData(pe, leaf).Span);
    }

    private static ResourceNode? FirstLeaf(ResourceNode? node)
    {
        if (node is null || !node.IsDirectory)
        {
            return node;
        }

        return node.Children!.Select(FirstLeaf).FirstOrDefault(n => n is not null);
    }

    private static string Describe(PeImage pe)
    {
        string arch = pe.Machine switch
        {
            MachineType.Amd64 => "x64",
            MachineType.I386 => "x86",
            MachineType.Arm64 => "ARM64",
            MachineType.ArmNt => "ARM",
            _ => pe.Machine.ToString(),
        };
        string kind = pe.IsDll ? "DLL" : "EXE";
        return pe.IsManaged ? $".NET {kind} ({arch})" : $"Native {kind} ({arch})";
    }
}
