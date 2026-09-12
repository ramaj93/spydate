using System.Runtime.InteropServices;
using System.Text;

namespace Spydate.Debugger.Managed;

/// <summary>
/// How a .NET Framework process is got hold of, which is not how a .NET one is.
///
/// dbgshim answers for CoreCLR and only for CoreCLR: it waits on the event that runtime signals when
/// it is debuggable, and the CLR in <c>%WINDIR%\Microsoft.NET</c> never signals it. Registering for
/// it against a Framework program is not an error — it is a wait that is never satisfied, so the
/// program simply runs to the end undebugged and the debugger says half a minute later that nothing
/// became debuggable. Every .NET Framework binary looked like that here.
///
/// The route for those is the metahost: ask mscoree for the installed runtime of the version the
/// file asks for, then ask that runtime for its debugging interface. What comes back is an ordinary
/// <see cref="ICorDebug"/>, and from there everything downstream — callbacks, breakpoints, stepping,
/// values — is the same code, because that interface is the same interface.
///
/// Only the leading slots of each vtable are declared. A COM interface is called by slot, so every
/// method up to the last one used has to be there and in order; nothing below that is needed, and
/// writing out the rest would be inventing signatures to be wrong about.
/// </summary>
internal static partial class MetaHost
{
    private static readonly Guid ClrMetaHost = new("9280188D-0E8E-4867-B30C-7FA83884E8DE");
    private static readonly Guid MetaHostInterface = new("D332DB9E-B9B3-4125-8207-A14884F53216");
    private static readonly Guid RuntimeInfoInterface = new("BD39D1D2-BA2F-486A-89B0-B4B0CB466891");

    /// <summary>
    /// <c>CLSID_CLRDebuggingLegacy</c> — the in-process debugging interface a v2-or-v4 CLR offers.
    /// "Legacy" is the CLR's own word for it and means only "not the CoreCLR route"; it is the
    /// current and only way to debug .NET Framework.
    /// </summary>
    private static readonly Guid DebuggingLegacy = new("DF8395B5-A4BA-450B-A77C-A9A47762C520");

    private static readonly Guid CorDebugInterface = new(CorDebugGuids.CorDebug);

    /// <summary>The version every .NET Framework 4.x program runs under, whatever it was built for.</summary>
    private const string Version4 = "v4.0.30319";

    [LibraryImport("mscoree.dll")]
    private static partial int CLRCreateInstance(in Guid clsid, in Guid riid, out IntPtr instance);

    /// <summary>
    /// The debugging interface for the CLR a file will run on, or null with the reason.
    ///
    /// The version asked for is the one the file's metadata names — "v4.0.30319" for anything built
    /// since 2010, "v2.0.50727" for what came before — and that older runtime is usually not
    /// installed any more, so a refusal falls back to v4, which is what the program itself would do.
    /// </summary>
    internal static ICorDebug? Debugger(string wantedVersion, out string? problem)
    {
        problem = null;

        IntPtr host = IntPtr.Zero;
        try
        {
            int hr = CLRCreateInstance(in ClrMetaHost, in MetaHostInterface, out host);
            if (hr < 0 || host == IntPtr.Zero)
            {
                problem = $"the .NET Framework host could not be reached: 0x{hr:X8}";
                return null;
            }

            if (Marshal.GetObjectForIUnknown(host) is not ICLRMetaHost metaHost)
            {
                problem = "what mscoree handed back is not an ICLRMetaHost";
                return null;
            }

            try
            {
                var info = Runtime(metaHost, wantedVersion) ?? Runtime(metaHost, Version4);
                if (info is null)
                {
                    problem = $"the .NET Framework runtime {wantedVersion} is not installed";
                    return null;
                }

                try
                {
                    hr = info.GetInterface(in DebuggingLegacy, in CorDebugInterface, out IntPtr debug);
                    if (hr < 0 || debug == IntPtr.Zero)
                    {
                        problem = $"that runtime offered no debugging interface: 0x{hr:X8}";
                        return null;
                    }

                    try
                    {
                        return Marshal.GetObjectForIUnknown(debug) as ICorDebug;
                    }
                    finally
                    {
                        Marshal.Release(debug);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(info);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(metaHost);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
        {
            problem = $"the .NET Framework debugging interface could not be set up: {ex.Message}";
            return null;
        }
        finally
        {
            if (host != IntPtr.Zero)
            {
                Marshal.Release(host);
            }
        }
    }

    private static ICLRRuntimeInfo? Runtime(ICLRMetaHost metaHost, string version)
    {
        if (version.Length == 0 || metaHost.GetRuntime(version, in RuntimeInfoInterface, out IntPtr runtime) < 0 || runtime == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.GetObjectForIUnknown(runtime) as ICLRRuntimeInfo;
        }
        finally
        {
            Marshal.Release(runtime);
        }
    }
}

[ComImport]
[Guid("D332DB9E-B9B3-4125-8207-A14884F53216")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICLRMetaHost
{
    [PreserveSig]
    int GetRuntime([MarshalAs(UnmanagedType.LPWStr)] string version, in Guid riid, out IntPtr runtime);

    [PreserveSig]
    int GetVersionFromFile(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer,
        ref uint size);
}

[ComImport]
[Guid("BD39D1D2-BA2F-486A-89B0-B4B0CB466891")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICLRRuntimeInfo
{
    [PreserveSig]
    int GetVersionString([MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, ref uint size);

    [PreserveSig]
    int GetRuntimeDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, ref uint size);

    [PreserveSig]
    int IsLoaded(IntPtr process, out int loaded);

    [PreserveSig]
    int LoadErrorString(uint id, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, ref uint size, int locale);

    [PreserveSig]
    int LoadLibrary([MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr module);

    [PreserveSig]
    int GetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr address);

    /// <summary>The one this is all for. Seventh slot, which is why the six above have to be here.</summary>
    [PreserveSig]
    int GetInterface(in Guid clsid, in Guid riid, out IntPtr unknown);
}
