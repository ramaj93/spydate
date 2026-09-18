using System.Windows;
using System.Windows.Controls;
using Spydate.App.Services;

namespace Spydate.App.Views;

/// <summary>
/// The patch dialog: an assembly box and a hex box that are the same patch seen two ways.
///
/// Both exist because both are how patches actually arrive. A line edited from the listing is the
/// common case and reads back six months later; bytes pasted from a diff, a write-up or another
/// tool are the other, and making those go through an assembler that has to recognise every
/// mnemonic first is how a one-byte change turns into an argument with a parser. Typing in either
/// box fills the other, so nothing has to be translated by hand and the two can be checked against
/// each other before anything is committed.
/// </summary>
public partial class PatchWindow : Window
{
    private readonly PatchPrompt _prompt;

    /// <summary>Guards the two boxes against answering each other's edits in a loop.</summary>
    private bool _syncing;

    /// <summary>
    /// Which box the analyst last typed in. What comes back is that one's text, so a patch entered
    /// as assembly is recorded as assembly — the comment in the Patches list is the line they wrote,
    /// not the hex it happened to encode to.
    /// </summary>
    private bool _fromBytes;

    public PatchWindow(PatchPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        InitializeComponent();
        DarkChrome.Apply(this);
        _prompt = prompt;
        Title = prompt.Title;
        LabelText.Text = prompt.Subtitle;
        HintText.Text = prompt.Hint;
        HintText.Visibility = string.IsNullOrEmpty(prompt.Hint) ? Visibility.Collapsed : Visibility.Visible;

        string assembly = prompt.ReadBack(prompt.Original);
        _syncing = true;
        Assembly.Text = assembly;
        Bytes.Text = Spaced(prompt.Original);
        _syncing = false;

        // Start on the side that can actually reproduce what is there. Most instructions read back
        // and assemble again byte for byte; the ones that do not are exactly the ones an analyst
        // wants to work on in hex, and starting them on the assembly box would mean OK writes
        // something subtly different from what the listing shows.
        _fromBytes = !prompt.Assemble(assembly).Bytes.SequenceEqual(prompt.Original);
        Say(prompt.Fit(prompt.Original.Length), error: false);

        Loaded += (_, _) =>
        {
            var start = _fromBytes ? Bytes : Assembly;
            start.Focus();
            start.SelectAll();
        };
    }

    /// <summary>
    /// What to assemble, once accepted: the assembly line, or the hex behind the <c>bytes:</c>
    /// prefix that <see cref="Spydate.Disassembly.X86Assembler"/> reads raw bytes through.
    /// </summary>
    public string Value => _fromBytes ? $"bytes: {Bytes.Text}" : Assembly.Text;

    private void OnAssemblyChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _fromBytes = false;
        var encoded = _prompt.Assemble(Assembly.Text);
        if (!encoded.Ok)
        {
            Say(encoded.Problem!, error: true);
            return;
        }

        _syncing = true;
        Bytes.Text = Spaced(encoded.Bytes);
        _syncing = false;
        Say(_prompt.Fit(encoded.Bytes.Length), error: false);
    }

    private void OnBytesChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _fromBytes = true;

        // Through the assembler's own hex route rather than a second parser here, so the dialog and
        // the patch agree on what counts as bytes.
        var encoded = _prompt.Assemble($"bytes: {Bytes.Text}");
        if (!encoded.Ok)
        {
            Say(encoded.Problem!, error: true);
            return;
        }

        _syncing = true;
        Assembly.Text = _prompt.ReadBack(encoded.Bytes);
        _syncing = false;
        Say(_prompt.Fit(encoded.Bytes.Length), error: false);
    }

    private void Say(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            error ? "Semantic.Error" : "Text.Tertiary");

        // Nothing to commit while one side does not parse: the other box still holds the last good
        // bytes, and OK would quietly write those instead of what is on screen.
        AcceptButton.IsEnabled = !error;
    }

    private static string Spaced(IReadOnlyList<byte> bytes)
        => string.Join(' ', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
