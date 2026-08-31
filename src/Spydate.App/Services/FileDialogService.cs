using System.Windows;
using Microsoft.Win32;
using Spydate.App.Views;

namespace Spydate.App.Services;

public interface IFileDialogService
{
    /// <summary>Shows an Open dialog for PE files; returns the selected path or null.</summary>
    string? OpenPeFile();

    /// <summary>
    /// Asks for one line of text. Returns null when the user cancels, which is different from an empty
    /// string: empty means "clear it".
    /// </summary>
    string? AskForText(string title, string label, string? hint = null, string? initial = null);

    /// <summary>Asks where to write a file; returns the chosen path or null.</summary>
    string? SaveFile(string title, string filter, string suggestedName);
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

    public string? AskForText(string title, string label, string? hint = null, string? initial = null)
    {
        var prompt = new PromptWindow(title, label, hint, initial)
        {
            Owner = Application.Current?.MainWindow,
        };

        return prompt.ShowDialog() == true ? prompt.Value : null;
    }

    public string? SaveFile(string title, string filter, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
