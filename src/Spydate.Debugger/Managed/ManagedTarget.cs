using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Spydate.Debugger.Managed;

/// <summary>
/// What a program will start as: which CLR will be in it, and how wide the process will be.
///
/// Both answers are needed before anything is launched, and neither can be asked of the process
/// afterwards, because by then it is too late to have attached. The two runtimes are reached by
/// completely different routes — dbgshim's startup handshake for .NET, mscoree's metahost for .NET
/// Framework — and a debugger that knows only one of them does not fail loudly on the other. It
/// registers for an event the target's runtime never signals, the program runs to completion
/// undebugged, and half a minute later the debugger reports that nothing ever became debuggable.
/// That is what a .NET Framework program looked like here: started, ran, exited, no breakpoint ever
/// hit, and a message about a runtime that had in fact been up the whole time.
///
/// Width matters for the same reason. A 64-bit debugger cannot drive a 32-bit debuggee through
/// ICorDebug at all, and the failure is the same silence.
/// </summary>
/// <param name="Framework">.NET Framework — the CLR in <c>%WINDIR%\Microsoft.NET</c>, not CoreCLR.</param>
/// <param name="Wide">The process will be 64-bit.</param>
/// <param name="Runtime">
/// The runtime version the file names — "v4.0.30319" for anything built since 2010. Only the
/// Framework route uses it, to ask the metahost for that CLR by name.
/// </param>
public sealed record ManagedTarget(bool Framework, bool Wide, string Runtime = "")
{
    /// <summary>
    /// Reads a file and says what starting it would produce.
    ///
    /// Three signals, cheapest and surest first. A <c>.runtimeconfig.json</c> beside the file is
    /// conclusive: it is what an apphost reads to find a runtime, and .NET Framework has no such
    /// thing — this is also the only signal a native apphost gives, since it carries no metadata of
    /// its own. Otherwise the assembly's own <c>TargetFrameworkAttribute</c> says which it was built
    /// for. Failing that, what it references does: .NET Framework code binds to <c>mscorlib</c> and
    /// .NET code to <c>System.Runtime</c>.
    ///
    /// Anything unreadable is reported as .NET, which is what this did before there was a choice.
    /// </summary>
    public static ManagedTarget Of(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);

            bool wide = Wide64(pe.PEHeaders);

            if (File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
            {
                return new ManagedTarget(false, wide);
            }

            if (!pe.HasMetadata)
            {
                return new ManagedTarget(false, wide);
            }

            var metadata = pe.GetMetadataReader();
            return new ManagedTarget(
                TargetFramework(metadata) is { } built
                    ? built.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase)
                    : BindsToMscorlib(metadata),
                wide,
                metadata.MetadataVersion);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            // A file that cannot be read here is one the launch is about to fail on anyway, and it
            // will fail saying what actually went wrong rather than guessing here.
            return new ManagedTarget(false, Environment.Is64BitProcess);
        }
    }

    /// <summary>
    /// Whether the process will be 64-bit.
    ///
    /// Not simply the machine field. An IL-only assembly built AnyCPU is stamped I386 and runs at
    /// whatever width the machine offers, so the CLR header has the deciding say: <c>Requires32Bit</c>
    /// is what actually forces a WOW64 process.
    /// </summary>
    private static bool Wide64(PEHeaders headers)
    {
        if (headers.CoffHeader.Machine is not (Machine.I386 or Machine.Unknown))
        {
            return true;
        }

        return headers.CorHeader is { } cor
               && (cor.Flags & CorFlags.ILOnly) != 0
               && (cor.Flags & CorFlags.Requires32Bit) == 0;
    }

    /// <summary>The framework the assembly was built for, as the attribute spells it, or null.</summary>
    private static string? TargetFramework(MetadataReader metadata)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (AttributeName(metadata, attribute) is not "TargetFrameworkAttribute")
            {
                continue;
            }

            try
            {
                var blob = metadata.GetBlobReader(attribute.Value);

                // Every custom attribute blob opens with this, and a blob that does not is not one
                // this can read positionally.
                if (blob.Length < 2 || blob.ReadUInt16() != 1)
                {
                    continue;
                }

                return blob.ReadSerializedString();
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The name of the type a custom attribute is of, however its constructor is referenced.</summary>
    private static string? AttributeName(MetadataReader metadata, CustomAttribute attribute)
    {
        try
        {
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                    var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    return member.Parent.Kind == HandleKind.TypeReference
                        ? metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Name)
                        : null;

                case HandleKind.MethodDefinition:
                    var method = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    return metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name);

                default:
                    return null;
            }
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether it binds to <c>mscorlib</c> rather than to <c>System.Runtime</c>.
    ///
    /// The fallback, for assemblies old enough to predate the attribute. Both names can appear at
    /// once — a .NET assembly may reference the <c>mscorlib</c> facade — so the question is which of
    /// the two is there, and only <c>mscorlib</c> alone means .NET Framework.
    /// </summary>
    private static bool BindsToMscorlib(MetadataReader metadata)
    {
        bool mscorlib = false;

        foreach (var handle in metadata.AssemblyReferences)
        {
            try
            {
                string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                mscorlib |= name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        return mscorlib;
    }
}
