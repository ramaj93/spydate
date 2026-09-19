using System.Windows;
using Microsoft.Win32;
using Spydate.App.Views;
using Spydate.Disassembly;

namespace Spydate.App.Services;

public interface IFileDialogService
{
    /// <summary>Shows an Open dialog for PE files; returns the selected path or null.</summary>
    string? OpenPeFile();

    /// <summary>
    /// Asks for a folder; returns the chosen path or null when cancelled. Opens at
    /// <paramref name="initialDirectory"/> when it names one that exists.
    /// </summary>
    string? OpenFolder(string? initialDirectory = null);

    /// <summary>
    /// Asks for one line of text. Returns null when the user cancels, which is different from an empty
    /// string: empty means "clear it".
    /// </summary>
    string? AskForText(string title, string label, string? hint = null, string? initial = null);

    /// <summary>
    /// Asks for a patch, as assembly or as bytes. Returns what to assemble — a line of assembly, or
    /// <c>bytes: ...</c> for hex — or null when cancelled.
    /// </summary>
    string? AskForPatch(PatchPrompt prompt);

    /// <summary>
    /// Asks where a newly opened file should go when one is already open. Null when the reader
    /// cancelled, which is different from either answer: nothing is opened at all.
    /// </summary>
    Spydate.Core.Project.OpenDestination? AskWhereToOpen(string incoming, string current, bool currentIsDebugging);

    /// <summary>Asks where to write a file; returns the chosen path or null.</summary>
    string? SaveFile(string title, string filter, string suggestedName);
}

/// <summary>
/// Everything the patch dialog needs to show assembly and bytes as each other, without knowing what
/// a binary is. The three functions are the whole of it: text to bytes, bytes back to text, and how
/// far a replacement of a given size reaches at this address.
/// </summary>
/// <param name="Original">The bytes there now — what both boxes start out saying.</param>
public sealed record PatchPrompt(
    string Title,
    string Subtitle,
    string Hint,
    byte[] Original,
    Func<string, AssembleResult> Assemble,
    Func<byte[], string> ReadBack,
    Func<int, string> Fit);

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

    public string? OpenFolder(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Working directory",
        };

        if (initialDirectory is { Length: > 0 } start && System.IO.Directory.Exists(start))
        {
            dialog.InitialDirectory = start;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? AskForText(string title, string label, string? hint = null, string? initial = null)
    {
        var prompt = new PromptWindow(title, label, hint, initial)
        {
            Owner = Application.Current?.MainWindow,
        };

        return prompt.ShowDialog() == true ? prompt.Value : null;
    }

    public Spydate.Core.Project.OpenDestination? AskWhereToOpen(string incoming, string current, bool currentIsDebugging)
    {
        var chooser = new OpenDestinationWindow(incoming, current, currentIsDebugging)
        {
            Owner = Application.Current?.MainWindow,
        };

        return chooser.ShowDialog() == true ? chooser.Choice : null;
    }

    public string? AskForPatch(PatchPrompt prompt)
    {
        var dialog = new PatchWindow(prompt)
        {
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
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
