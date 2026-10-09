using System.Windows;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// A Yes/No consent prompt whose long part scrolls instead of growing the window off the screen.
/// Defaults to No; a dismissed dialog is a No.
/// </summary>
public partial class ApprovalDialog : Window
{
    /// <summary>How much of the work area the window may take. Leaves the title bar and taskbar
    /// comfortably reachable, and room to see the IDE behind it.</summary>
    private const double MaxWorkAreaFraction = 0.85;

    private ApprovalDialog(string title, string intro, string detail, string? note, string? warning)
    {
        InitializeComponent();

        Title = title;
        IntroText.Text = intro;
        DetailBox.Text = detail;

        if (!string.IsNullOrWhiteSpace(note))
        {
            NoteText.Text = note;
            NoteScroller.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }

        var workArea = SystemParameters.WorkArea;
        MaxHeight = workArea.Height * MaxWorkAreaFraction;
        MaxWidth = workArea.Width * MaxWorkAreaFraction;
        Width = Math.Min(Width, MaxWidth);
    }

    /// <summary>
    /// Shows the prompt modally and returns whether the user allowed it.
    /// </summary>
    /// <param name="intro">What is being asked, in a sentence. Wraps; keep it short.</param>
    /// <param name="detail">The exact text being consented to (a command, a URL, a list of commands).
    /// Shown whole, verbatim, in a monospace box that scrolls - never shortened.</param>
    /// <param name="note">Optional extra the model supplied (its stated reason). Scrolls in its own
    /// capped box, since it is as unbounded as the detail.</param>
    /// <param name="warning">Optional fixed text that always stays visible under the detail.</param>
    public static bool Ask(Window owner, string title, string intro, string detail, string? note = null, string? warning = null) =>
        Create(owner, title, intro, detail, note, warning).ShowDialog() == true;

    /// <summary>Builds the dialog without showing it - <see cref="Ask"/> is this plus ShowDialog, split
    /// out so layout can be tested without a blocking modal.</summary>
    public static ApprovalDialog Create(Window owner, string title, string intro, string detail, string? note = null, string? warning = null)
    {
        var dialog = new ApprovalDialog(title, intro, detail, note, warning);
        if (owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog;
    }

    private void ApprovalDialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        // Focus on Deny, not the text: Enter must never approve something the user hasn't read.
        DenyButton.Focus();

        // Layout has run, so the box knows whether the text fits. If it doesn't, say so - the risk with
        // a scrolling box is approving without having seen the part below the fold.
        if (DetailBox.ExtentHeight > DetailBox.ViewportHeight + 1)
        {
            var lines = DetailBox.LineCount;
            ScrollHint.Text = $"This is longer than the visible area ({lines} line{(lines == 1 ? "" : "s")}). " +
                              "Scroll to read all of it before allowing.";
            ScrollHint.Visibility = Visibility.Visible;
        }
    }

    private void AllowButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
