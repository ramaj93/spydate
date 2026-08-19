using System.IO;
using Spydate.Core.PE;
using Spydate.Decompiler.Managed;
using Spydate.Decompiler.Native;
using Spydate.Disassembly;

namespace Spydate.App.Services;

/// <summary>Everything loaded for one file: the PE image plus the native and/or managed analysis objects.</summary>
public sealed class OpenedBinary : IDisposable
{
    public OpenedBinary(PeImage image, BinaryAnalysis? analysis, ManagedAssembly? managed, string? managedLoadError)
    {
        Image = image;
        Analysis = analysis;
        Managed = managed;
        ManagedLoadError = managedLoadError;
        NativeDecompiler = analysis is null ? null : new NativeDecompiler(analysis);
    }

    public PeImage Image { get; }

    /// <summary>Native analysis session; null when the machine type is not x86/x64.</summary>
    public BinaryAnalysis? Analysis { get; }

    public NativeDecompiler? NativeDecompiler { get; }

    /// <summary>Managed assembly wrapper; null for native images or when loading failed.</summary>
    public ManagedAssembly? Managed { get; }

    public string? ManagedLoadError { get; }

    public string DisplayName => Image.FileName;

    public void Dispose() => Managed?.Dispose();
}

/// <summary>Loads files and owns the current <see cref="OpenedBinary"/>.</summary>
public sealed class WorkspaceService
{
    public OpenedBinary? Current { get; private set; }

    public event EventHandler? CurrentChanged;

    public async Task<OpenedBinary> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var opened = await Task.Run(() => Load(path), cancellationToken).ConfigureAwait(true);
        Current?.Dispose();
        Current = opened;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return opened;
    }

    public void Close()
    {
        Current?.Dispose();
        Current = null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    private static OpenedBinary Load(string path)
    {
        var pe = PeImage.Load(path);
        BinaryAnalysis? analysis = pe.IsX86Family ? new BinaryAnalysis(pe) : null;

        ManagedAssembly? managed = null;
        string? managedError = null;
        if (pe.IsManaged)
        {
            try
            {
                managed = ManagedAssembly.Load(path);
                _ = managed.Namespaces; // force metadata load off the UI thread
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException)
            {
                managedError = ex.Message;
            }
        }

        return new OpenedBinary(pe, analysis, managed, managedError);
    }
}
