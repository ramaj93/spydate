using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Spydate.Core.Project;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels.Documents;

/// <summary>One note section as the list shows it: its key, who wrote it, and when.</summary>
public sealed record NoteRow(string Key, string By, string Modified);

/// <summary>
/// What has been learned about this binary as a whole — the keyed sections an analyst or the assistant
/// records, edited here the way a project keeps a CLAUDE.md. Unlike the Annotations document, which is
/// a read-only review of address-bound work, this one is an editor: a section belongs to no address, so
/// there is nowhere else to write it.
///
/// The sections live in the same <see cref="NoteStore"/> the assistant writes to, so a section it
/// records appears here and one typed here is in front of it. Saving writes the project file through the
/// same merge as the annotations, so the two do not overwrite each other.
/// </summary>
public sealed partial class NotesDocumentViewModel : DocumentViewModel
{
    private readonly NoteStore _notes;
    private readonly Func<string?> _save;

    public NotesDocumentViewModel(NoteStore notes, Func<string?> save)
        : base("notes", "Notes", SymbolRegular.Note24)
    {
        _notes = notes;
        _save = save;
    }

    public ObservableCollection<NoteRow> Rows { get; } = new();

    /// <summary>The section being edited. Selecting one loads its key and text into the editor.</summary>
    [ObservableProperty]
    private NoteRow? _selectedRow;

    [ObservableProperty]
    private string _editKey = string.Empty;

    [ObservableProperty]
    private string _editText = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>
    /// Whether the pane shows the sections as one rendered Markdown document rather than the editor.
    /// A reading view, not an editing one — the whole point is to see the notes together.
    /// </summary>
    [ObservableProperty]
    private bool _showAsDocument;

    /// <summary>What the view-switch button says: it names the view it goes to, not the one you are in.</summary>
    public string ViewToggleLabel => ShowAsDocument ? "View sections" : "View as document";

    partial void OnShowAsDocumentChanged(bool value) => OnPropertyChanged(nameof(ViewToggleLabel));

    /// <summary>Switches between the section editor and the one-document reading view.</summary>
    [RelayCommand]
    private void ToggleView() => ShowAsDocument = !ShowAsDocument;

    /// <summary>
    /// Every section as one Markdown document, each under its key as a heading, in the order the list
    /// shows them (alphabetical by key — the store keeps no other order). This is what the document
    /// view renders; it is rebuilt whenever the sections change.
    /// </summary>
    public string CombinedMarkdown
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (key, note) in _notes.Snapshot())
            {
                sb.Append("## ").Append(key).Append("\n\n").Append(note.Text).Append("\n\n");
            }

            return sb.ToString().TrimEnd();
        }
    }

    partial void OnSelectedRowChanged(NoteRow? value)
    {
        if (value is not null && _notes.Get(value.Key) is { } note)
        {
            EditKey = value.Key;
            EditText = note.Text;
            Status = string.Empty;
        }
    }

    public override Task LoadAsync(CancellationToken cancellationToken)
    {
        Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rebuilds the list from the store, keeping the reader on the section they were on. Kept by key
    /// rather than by row, since a re-read makes a new row — editing the section you are looking at
    /// should not throw you back to the top.
    /// </summary>
    public void Refresh()
    {
        string? was = SelectedRow?.Key;

        Rows.Clear();
        foreach (var (key, note) in _notes.Snapshot())
        {
            Rows.Add(new NoteRow(
                key,
                note.Source == AnnotationSource.Agent ? "agent" : "you",
                note.Modified is { } when ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : string.Empty));
        }

        SelectedRow = was is { } keep ? Rows.FirstOrDefault(r => r.Key == keep) : SelectedRow;

        Summary = Rows.Count switch
        {
            0 => "no notes yet",
            1 => "1 section",
            _ => $"{Rows.Count} sections",
        };

        OnPropertyChanged(nameof(CombinedMarkdown));
    }

    /// <summary>Copies every section, as the combined Markdown document, to the clipboard.</summary>
    [RelayCommand]
    private void CopyDocument()
    {
        string markdown = CombinedMarkdown;
        if (string.IsNullOrEmpty(markdown))
        {
            Status = "Nothing to copy yet.";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(markdown);
            Status = Rows.Count == 1 ? "Copied the note." : $"Copied {Rows.Count} sections.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is held by another process often enough to be worth not throwing over.
            Status = "Could not reach the clipboard; try again.";
        }
    }

    /// <summary>Clears the editor for a section that does not exist yet.</summary>
    [RelayCommand]
    private void NewSection()
    {
        SelectedRow = null;
        EditKey = string.Empty;
        EditText = string.Empty;
        Status = "New section — give it a key and text, then Save.";
    }

    /// <summary>Writes the section being edited and saves the project. Over-long text shows the overage rather than saving.</summary>
    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditKey))
        {
            Status = "Give the section a key, such as overview or string-xor.";
            return;
        }

        string? stored = NoteStore.CleanKey(EditKey);
        try
        {
            _notes.Set(EditKey, EditText);
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;   // the over-length overage, or an empty key
            return;
        }

        Persist("Saved.");
        Refresh();

        // Land on the section that was just written, in its canonical key form, so a new one is not
        // left unselected and the key box shows what was actually stored.
        if (stored is not null)
        {
            SelectedRow = Rows.FirstOrDefault(r => r.Key == stored);
        }
    }

    /// <summary>Removes the selected section and saves.</summary>
    [RelayCommand]
    private void Remove()
    {
        if (SelectedRow is null)
        {
            return;
        }

        _notes.Set(SelectedRow.Key, null);
        EditKey = string.Empty;
        EditText = string.Empty;
        Persist("Removed.");
        Refresh();
    }

    private void Persist(string done)
    {
        try
        {
            _save();
            Status = done;
        }
        catch (IOException ex)
        {
            Status = $"Not saved: {ex.Message}";
        }
    }
}
