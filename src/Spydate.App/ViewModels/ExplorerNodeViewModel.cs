using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Spydate.App.ViewModels;

/// <summary>What clicking a tree node opens. Implementations are simple records matched by <see cref="MainViewModel"/>.</summary>
public abstract record NodeTarget;

public sealed record OverviewTarget : NodeTarget;
public sealed record HeadersTarget : NodeTarget;
public sealed record SectionsTarget : NodeTarget;
public sealed record ImportsTarget : NodeTarget;
public sealed record ExportsTarget : NodeTarget;
public sealed record FunctionsTarget : NodeTarget;
public sealed record ResourcesTarget : NodeTarget;
public sealed record StringsTarget : NodeTarget;
/// <summary>A resource leaf that can be shown as text rather than bytes.</summary>
public sealed record ResourcePreviewTarget(uint TypeId, uint Id, uint DataRva, uint DataSize, string Title) : NodeTarget;
public sealed record HexTarget(long Offset) : NodeTarget;
public sealed record DisassemblyTarget(ulong Va, string Name) : NodeTarget;
public sealed record RangeDisassemblyTarget(ulong Va, int Bytes, string Title) : NodeTarget;
public sealed record ManagedAssemblyTarget : NodeTarget;
public sealed record ManagedTypeTarget(Spydate.Decompiler.Managed.ManagedType Type) : NodeTarget;
public sealed record ManagedMemberTarget(Spydate.Decompiler.Managed.ManagedType Type, Spydate.Decompiler.Managed.ManagedMember Member) : NodeTarget;

/// <summary>A node in the explorer tree. Children may be materialised lazily via <see cref="ChildrenFactory"/>.</summary>
public sealed partial class ExplorerNodeViewModel : ObservableObject
{
    private static readonly ExplorerNodeViewModel Placeholder = new("Loading…", SymbolRegular.MoreHorizontal24);
    private bool _childrenLoaded;

    public ExplorerNodeViewModel(string title, SymbolRegular icon, NodeTarget? target = null, string? subtitle = null)
    {
        Title = title;
        Icon = icon;
        Target = target;
        Subtitle = subtitle;
        Children = new ObservableCollection<ExplorerNodeViewModel>();
    }

    public string Title { get; }

    public string? Subtitle { get; }

    public SymbolRegular Icon { get; }

    public NodeTarget? Target { get; }

    public ObservableCollection<ExplorerNodeViewModel> Children { get; }

    /// <summary>When set, children are produced on first expansion.</summary>
    public Func<IEnumerable<ExplorerNodeViewModel>>? ChildrenFactory
    {
        get => _childrenFactory;
        set
        {
            _childrenFactory = value;
            if (value is not null && Children.Count == 0)
            {
                Children.Add(Placeholder);
                _childrenLoaded = false;
            }
        }
    }

    private Func<IEnumerable<ExplorerNodeViewModel>>? _childrenFactory;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureChildren();
        }
    }

    public void EnsureChildren()
    {
        if (_childrenLoaded || _childrenFactory is null)
        {
            return;
        }

        _childrenLoaded = true;
        Children.Clear();
        foreach (var child in _childrenFactory())
        {
            Children.Add(child);
        }
    }

    /// <summary>Replaces the children (used when analysis results arrive).</summary>
    public void SetChildren(IEnumerable<ExplorerNodeViewModel> children)
    {
        _childrenFactory = null;
        _childrenLoaded = true;
        Children.Clear();
        foreach (var c in children)
        {
            Children.Add(c);
        }
    }

    public ExplorerNodeViewModel Add(ExplorerNodeViewModel child)
    {
        Children.Add(child);
        return child;
    }

    public override string ToString() => Title;
}
