using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.TestRunner;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows just the filename for a full path - used for Recent Files/Solutions menu items,
/// whose full path is shown instead as a tooltip.</summary>
public sealed class FileNameConverter : IValueConverter
{
    public static readonly FileNameConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path ? System.IO.Path.GetFileName(path) : value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Status-icon glyph for a Test Explorer method leaf's TestOutcome - paired with
/// TestOutcomeToBrushConverter for color. No existing enum-to-glyph converter in this file to mirror,
/// so this is a small new one following the same static-Instance IValueConverter shape as the rest.</summary>
public sealed class TestOutcomeToGlyphConverter : IValueConverter
{
    public static readonly TestOutcomeToGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TestOutcome.Passed => "✓",
        TestOutcome.Failed => "✗",
        TestOutcome.Skipped => "◐",
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TestOutcomeToBrushConverter : IValueConverter
{
    public static readonly TestOutcomeToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TestOutcome.Passed => Brushes.LimeGreen,
        TestOutcome.Failed => Brushes.OrangeRed,
        TestOutcome.Skipped => Brushes.Goldenrod,
        _ => Brushes.Gray,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DiffLinePrefixConverter : IValueConverter
{
    public static readonly DiffLinePrefixConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffLineKind.Added => "+ ",
        DiffLineKind.Removed => "- ",
        _ => "  ",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
