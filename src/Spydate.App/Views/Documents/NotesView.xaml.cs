using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Spydate.Agent.Text;
using Spydate.App.ViewModels.Documents;
using Spydate.App.Views.Chat;

namespace Spydate.App.Views.Documents;

public partial class NotesView : UserControl
{
    private NotesDocumentViewModel? _vm;

    public NotesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as NotesDocumentViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }

        RenderDocument();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-render when the view is turned on, and when a section changes while it is on.
        if (e.PropertyName is nameof(NotesDocumentViewModel.ShowAsDocument) or nameof(NotesDocumentViewModel.CombinedMarkdown))
        {
            RenderDocument();
        }
    }

    /// <summary>
    /// Builds the rendered document from the combined Markdown, using the same renderer the assistant's
    /// answers use. Only when the view is on, so the editor's typing does not pay to lay out a document
    /// nobody is looking at.
    /// </summary>
    private void RenderDocument()
    {
        if (_vm is null || !_vm.ShowAsDocument)
        {
            return;
        }

        string markdown = _vm.CombinedMarkdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            DocumentHost.Content = null;
            return;
        }

        var (root, _) = MarkdownView.Render(Markdown.Parse(markdown));
        DocumentHost.Content = root;
    }
}
