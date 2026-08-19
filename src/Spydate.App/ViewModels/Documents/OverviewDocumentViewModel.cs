using System.Globalization;
using Spydate.App.Services;
using Spydate.Core.PE;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>Summary of the loaded file: identity, key header facts, debug info, warnings.</summary>
public sealed class OverviewDocumentViewModel : DocumentViewModel
{
    public OverviewDocumentViewModel(OpenedBinary binary) : base("overview", "Overview", SymbolRegular.Info24)
    {
        var pe = binary.Image;
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

        Build = pe.RichHeader is { } rich
            ? rich.Entries
                .OrderByDescending(e => e.UseCount)
                .Select(e => new PropertyRow(
                    $"tool 0x{e.ProductId:X4}",
                    $"{e.UseCount:N0} object{(e.UseCount == 1 ? string.Empty : "s")}",
                    e.BuildNumber == 0 ? "no build stamp" : $"build {e.BuildNumber}"))
                .ToList()
            : new List<PropertyRow>();

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

            if (binary.Managed is { } m)
            {
                Managed.Add(new PropertyRow("Assembly", m.FullName));
                Managed.Add(new PropertyRow("Target framework", m.TargetFramework));
                Managed.Add(new PropertyRow("Metadata version", m.RuntimeVersion));
                Managed.Add(new PropertyRow("Types", m.Namespaces.Sum(n => n.Types.Count).ToString(CultureInfo.InvariantCulture)));
            }
            else if (binary.ManagedLoadError is { } err)
            {
                Managed.Add(new PropertyRow("Decompiler", $"failed to load: {err}"));
            }
        }

        Debug = pe.Debug.Select(d => new PropertyRow(
            d.Type.ToString(),
            d.CodeView is { } cv ? cv.PdbPath : $"size 0x{d.SizeOfData:X} at 0x{d.PointerToRawData:X}",
            d.CodeView is { } cv2 ? $"{cv2.Guid:D} age {cv2.Age}" : null)).ToList();

        Warnings = pe.Warnings.ToList();
        if (binary.Analysis is null && !pe.IsManaged)
        {
            Warnings.Insert(0, $"Machine type {pe.Machine} is not supported by the native disassembler (x86/x64 only).");
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
    public bool HasSecurity => Security.Count > 0;
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
