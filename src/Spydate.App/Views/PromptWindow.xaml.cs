using System.Windows;

namespace Spydate.App.Views;

/// <summary>One-line text prompt: what the rename and comment commands ask through.</summary>
public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string? hint, string? initial)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        HintText.Text = hint ?? string.Empty;
        HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        Input.Text = initial ?? string.Empty;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>What the user typed, once the dialog has been accepted.</summary>
    public string Value => Input.Text;

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
