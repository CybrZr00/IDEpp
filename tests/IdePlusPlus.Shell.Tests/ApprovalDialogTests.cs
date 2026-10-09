using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdePlusPlus.Shell.Views;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class ApprovalDialogTests
{
    private static string LongScript(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => $"    result_{i} = compute_something_rather_long(argument_one, argument_two, {i})  # step {i}"));

    [Fact]
    public void ALongScript_StaysOnScreen_WithTheButtonsReachable_AndScrolls() => OnSta(() =>
    {
        var dialog = ApprovalDialog.Create(
            new Window(), "IDE++ - run this command?", "The AI assistant wants to run a command on your machine:",
            LongScript(400), note: "Its reason: " + new string('x', 3000), warning: "It runs as you.");

        Show(dialog);
        var work = SystemParameters.WorkArea;

        dialog.ActualHeight.ShouldBeLessThanOrEqualTo(work.Height);
        dialog.ActualWidth.ShouldBeLessThanOrEqualTo(work.Width);

        // Buttons sit fully inside the window rather than below the fold.
        Bounds(dialog, dialog.AllowButton).Bottom.ShouldBeLessThanOrEqualTo(dialog.ActualHeight);
        Bounds(dialog, dialog.DenyButton).Bottom.ShouldBeLessThanOrEqualTo(dialog.ActualHeight);

        // The text is all there, not shortened, and the box scrolls to reach it.
        dialog.DetailBox.Text.ShouldBe(LongScript(400));
        dialog.DetailBox.ExtentHeight.ShouldBeGreaterThan(dialog.DetailBox.ViewportHeight);
        dialog.ScrollHint.Visibility.ShouldBe(Visibility.Visible);

        // The model-supplied reason is capped too, rather than pushing the buttons down.
        dialog.NoteScroller.ActualHeight.ShouldBeLessThanOrEqualTo(110);

        Render(dialog, "long-script");
        dialog.Close();
    });

    [Fact]
    public void AShortCommand_GetsASmallDialog_WithNoScrollHint() => OnSta(() =>
    {
        var dialog = ApprovalDialog.Create(
            new Window(), "IDE++ - run this command?", "The AI assistant wants to run a command on your machine:",
            "dotnet build", note: "Its reason: check it compiles", warning: "It runs as you.");

        Show(dialog);

        dialog.ActualHeight.ShouldBeLessThan(SystemParameters.WorkArea.Height / 2);
        dialog.ScrollHint.Visibility.ShouldBe(Visibility.Collapsed);
        dialog.DetailBox.ExtentHeight.ShouldBeLessThanOrEqualTo(dialog.DetailBox.ViewportHeight + 1);

        Render(dialog, "short-command");
        dialog.Close();
    });

    [Fact]
    public void ALongUnbrokenLine_WrapsInsteadOfScrollingSideways() => OnSta(() =>
    {
        var dialog = ApprovalDialog.Create(
            new Window(), "IDE++ - fetch this page?", "The AI assistant wants to fetch a webpage:",
            "https://example.com/" + new string('a', 4000));

        Show(dialog);

        // Wrapped, so there is no hidden far-right tail to approve unseen.
        dialog.DetailBox.HorizontalScrollBarVisibility.ShouldBe(ScrollBarVisibility.Disabled);
        dialog.DetailBox.ExtentWidth.ShouldBeLessThanOrEqualTo(dialog.DetailBox.ViewportWidth + 1);
        dialog.ActualHeight.ShouldBeLessThanOrEqualTo(SystemParameters.WorkArea.Height);
        dialog.Close();
    });

    [Fact]
    public void Deny_IsTheDefaultAndTheCancel_SoEnterEscAndCloseAllMeanNo() => OnSta(() =>
    {
        var dialog = ApprovalDialog.Create(new Window(), "t", "i", "d");

        dialog.DenyButton.IsDefault.ShouldBeTrue();
        dialog.DenyButton.IsCancel.ShouldBeTrue();
        dialog.AllowButton.IsDefault.ShouldBeFalse();
        dialog.AllowButton.IsCancel.ShouldBeFalse();
    });

    private static Rect Bounds(Window window, FrameworkElement element) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void Show(Window window)
    {
        window.Show();
        // Loaded and layout both run at or below Loaded priority; drain them before measuring.
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static void Render(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Environment.GetEnvironmentVariable("IDEPP_TEST_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, $"approval-{name}.png"));
        encoder.Save(stream);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
