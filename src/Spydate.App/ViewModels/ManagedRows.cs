using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Spydate.Debugger.Managed;

namespace Spydate.App.ViewModels;

/// <summary>
/// One row of the Locals tree: a value, how deep it sits, and whether it is open.
///
/// The tree is a flat list with indentation rather than a TreeView, because a Locals window is a
/// grid first — Name, Value and Type have to line up in columns however deep a row is, and a TreeView
/// nests its rows inside each other and cannot. Opening a row inserts its children after it; closing
/// takes them out again.
/// </summary>
public sealed partial class VariableRow : ObservableObject
{
    /// <summary>How far each level is indented. Enough to see the nesting, not so much a deep row falls off.</summary>
    private const double IndentPerLevel = 14;

    private readonly Action<VariableRow> _toggled;
    private bool _quiet;

    internal VariableRow(ManagedVariable variable, int depth, Action<VariableRow> toggled)
    {
        Name = variable.Name;
        Value = variable.Value;
        Type = variable.Type;
        Kind = variable.Kind;
        Expandable = variable.Expandable;
        Path = variable.Path;
        Depth = depth;
        _toggled = toggled;
    }

    public string Name { get; }

    public string Value { get; }

    public string Type { get; }

    /// <summary>What the value text is, which decides its colour.</summary>
    public ManagedValueKind Kind { get; }

    public bool Expandable { get; }

    public int Depth { get; }

    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

    /// <summary>How to find this value again from the frame, which is what opening it walks.</summary>
    internal ManagedValuePath Path { get; }

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Opens or closes the row without asking for its children — for reopening what was open.</summary>
    internal void SetExpandedQuietly(bool value)
    {
        _quiet = true;
        IsExpanded = value;
        _quiet = false;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!_quiet)
        {
            _toggled(this);
        }
    }
}

/// <summary>One thread of a stopped .NET process, as the Threads pane shows it.</summary>
public sealed record ManagedThreadRow(
    uint Id,
    string ManagedId,
    string Category,
    string Name,
    string Location,
    string Priority,
    string AppDomain,
    string State,
    bool IsStopped,
    bool IsSelected)
{
    /// <summary>The OS thread id in hex, the way every Windows tool writes it.</summary>
    public string IdText => "0x" + Id.ToString("X8", CultureInfo.InvariantCulture);

    internal static ManagedThreadRow From(ManagedThread thread) => new(
        thread.Id,
        thread.ManagedId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        thread.Category,
        thread.Name,
        thread.Location,
        thread.Priority,
        thread.AppDomain,
        thread.State,
        thread.IsStopped,
        thread.IsSelected);
}
