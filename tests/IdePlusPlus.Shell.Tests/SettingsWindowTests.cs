using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.Views;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The Settings window is a left-hand navigation and one page at a time, each page a stack of cards.
/// Loading it catches what a build cannot - a StaticResource that does not exist, a template that
/// fails to parse - and walking the navigation checks that each item shows its own page and no other.
///
/// Set IDEPP_SETTINGS_PNG_DIR to a folder and every page is also rendered there as a PNG, in a dark and a
/// light theme, for looking at.
/// </summary>
public sealed class SettingsWindowTests : IDisposable
{
    private static readonly string[] NavNames =
        ["NavProviders", "NavAssistant", "NavUltra", "NavSubAgents", "NavMcp", "NavReverseEngineering", "NavBuild"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-settings-{Guid.NewGuid():N}");

    public SettingsWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private SettingsViewModel CreateViewModel()
    {
        var secrets = new SecureSettingsStore(Path.Combine(_root, "secrets.json"));
        var httpClient = new HttpClient();
        return new SettingsViewModel(
            secrets,
            new AppSettingsStore(Path.Combine(_root, "settings.json")),
            new McpServerManager(
                new McpServerConfigStore(Path.Combine(_root, "mcp-servers.json")),
                new McpProjectTrustStore(Path.Combine(_root, "mcp-trust.json"))),
            new McpServerConfigStore(Path.Combine(_root, "mcp-servers.json")),
            new DeepSeekFimClient(httpClient, secrets),
            new ExternalToolchainStore(Path.Combine(_root, "tools")),
            new AnthropicChatClient(httpClient, secrets));
    }

    private static void ApplyTheme(ResourceDictionary resources, bool dark)
    {
        // The values of DarkTheme.xaml and LightTheme.xaml for the brushes the window uses. The real
        // dictionaries are compiled into the app, whose pack URIs a test has no Application to resolve.
        (string Key, string Dark, string Light)[] brushes =
        [
            ("WindowBackgroundBrush", "#252526", "#F0F0F0"),
            ("WindowForegroundBrush", "#DCDCDC", "#1E1E1E"),
            ("PanelBackgroundBrush", "#1E1E1E", "#FFFFFF"),
            ("MutedForegroundBrush", "#9D9D9D", "#808080"),
            ("EditProposalBackgroundBrush", "#2D2D30", "#FAFAFA"),
            ("EditProposalBorderBrush", "#454545", "#DDDDDD"),
            ("MenuHighlightBackgroundBrush", "#3C3C3C", "#E5E5E5"),
            ("AccentBrush", "#0E639C", "#0067C0"),
            ("OnAccentBrush", "#FFFFFF", "#FFFFFF"),
        ];
        foreach (var (key, darkColor, lightColor) in brushes)
        {
            resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor));
        }
    }

    private static void OnStaThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
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

    private static void Render(FrameworkElement root, string path)
    {
        const int width = 960;

        // Taller on request, to see the lower cards of a long page without scrolling to them.
        var height = int.TryParse(Environment.GetEnvironmentVariable("IDEPP_SETTINGS_PNG_HEIGHT"), out var tall) ? tall : 700;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>A combo box's drop-down is drawn in a window of its own, which a snapshot of the page
    /// leaves out - so its content is laid out and drawn separately, on the page's own background.</summary>
    private static void RenderDropDown(ComboBox combo, Brush background, string path)
    {
        combo.ApplyTemplate();
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        var content = (FrameworkElement)popup.Child;
        const int width = 300;
        const int height = 220;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static IReadOnlyList<ScrollViewer> VisiblePages(Window window) =>
        ((Panel)window.FindName("PagesHost")).Children.OfType<ScrollViewer>().Where(page => page.Visibility == Visibility.Visible).ToList();

    [Fact]
    public void TheWindowLoads_AndEachNavigationItemShowsItsOwnPageAndNoOther() => OnStaThread(() =>
    {
        var window = new SettingsWindow(CreateViewModel());
        ApplyTheme(window.Resources, dark: true);
        var pages = ((Panel)window.FindName("PagesHost")).Children.OfType<ScrollViewer>().ToList();
        pages.Count.ShouldBe(NavNames.Length, "one page per navigation item");

        VisiblePages(window).Count.ShouldBe(1);
        ((RadioButton)window.FindName("NavProviders")).IsChecked.ShouldBe(true, "it opens on the first page");

        var shown = new HashSet<ScrollViewer>();
        foreach (var name in NavNames)
        {
            ((RadioButton)window.FindName(name)).IsChecked = true;

            var visible = VisiblePages(window).ShouldHaveSingleItem($"{name} should show exactly one page");
            shown.Add(visible).ShouldBeTrue($"{name} should show a page no other item shows");
        }
    });

    [Fact]
    public void EveryPageLaysOutAndRenders_InADarkAndALightTheme() => OnStaThread(() =>
    {
        var outputDirectory = Environment.GetEnvironmentVariable("IDEPP_SETTINGS_PNG_DIR");
        foreach (var dark in new[] { true, false })
        {
            var window = new SettingsWindow(CreateViewModel());
            ApplyTheme(window.Resources, dark);

            foreach (var name in NavNames)
            {
                ((RadioButton)window.FindName(name)).IsChecked = true;
                var root = (FrameworkElement)window.Content;
                if (outputDirectory is not null)
                {
                    Directory.CreateDirectory(outputDirectory);
                    Render(root, Path.Combine(outputDirectory, $"settings-{(dark ? "dark" : "light")}-{name[3..]}.png"));
                }
                else
                {
                    root.Measure(new Size(960, 700));
                    root.Arrange(new Rect(0, 0, 960, 700));
                    root.UpdateLayout();
                }

                root.ActualWidth.ShouldBe(960);
            }

            // Every combo box's template has to apply and lay out, closed and with its drop-down built.
            foreach (var combo in Descendants<ComboBox>(window))
            {
                combo.ApplyTemplate();
                combo.Template.FindName("PART_Popup", combo).ShouldNotBeNull();
            }

            if (outputDirectory is not null)
            {
                ((RadioButton)window.FindName("NavUltra")).IsChecked = true;
                var firstCombo = Descendants<ComboBox>(window).First();
                RenderDropDown(
                    firstCombo,
                    (Brush)window.Resources["PanelBackgroundBrush"],
                    Path.Combine(outputDirectory, $"settings-{(dark ? "dark" : "light")}-dropdown.png"));
            }
        }
    });
}
