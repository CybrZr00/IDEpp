using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// A single-line text prompt for actions that need one name before they can run - currently "New
/// Folder" in the Solution Explorer, which has no dialog of its own because it creates nothing but a
/// directory and a csproj entry.
///
/// <para>Modelled on <see cref="GoToLineWindow"/>: same themed brushes, same OK/Cancel shape. The
/// difference is that the caller owns the rules, so <paramref name="validate"/> runs on every
/// keystroke and keeps OK disabled while the name would be rejected - the alternative is letting the
/// user press OK and finding out from a half-created folder or a thrown IO exception.</para>
/// </summary>
public partial class TextPromptDialog : Window
{
    private readonly Func<string, string?> _validate;

    /// <param name="title">The window title.</param>
    /// <param name="label">The label above the text box.</param>
    /// <param name="validate">Returns null when the text is acceptable, otherwise the message to show
    /// under the box.</param>
    public TextPromptDialog(string title, string label, Func<string, string?> validate)
    {
        InitializeComponent();

        Title = title;
        PromptLabel.Text = label;
        _validate = validate;

        Validate();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The trimmed text the user accepted, or null if the dialog was cancelled.</summary>
    public string? Result { get; private set; }

    private void NameBox_OnTextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void NameBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        // IsDefault already routes Enter to OK, but only while OK is enabled - and a disabled default
        // button still swallows the key in some focus states. Accept() re-checks validity anyway.
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e) => Accept();

    private void Validate()
    {
        // The validator is always handed the trimmed text: trailing spaces are what the file system
        // would silently strip anyway.
        var error = _validate(NameBox.Text.Trim());
        ValidationText.Text = error ?? string.Empty;
        ValidationText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = error is null;
    }

    private void Accept()
    {
        var text = NameBox.Text.Trim();
        if (_validate(text) is not null)
        {
            return;
        }

        Result = text;
        DialogResult = true;
    }
}
