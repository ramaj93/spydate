using Microsoft.Win32;

namespace Spydate.App.Services;

public interface IFileDialogService
{
    /// <summary>Shows an Open dialog for PE files; returns the selected path or null.</summary>
    string? OpenPeFile();
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenPeFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open executable",
            Filter = "PE files (*.exe;*.dll;*.sys;*.ocx;*.scr;*.drv;*.efi;*.mui)|*.exe;*.dll;*.sys;*.ocx;*.scr;*.drv;*.efi;*.mui|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
