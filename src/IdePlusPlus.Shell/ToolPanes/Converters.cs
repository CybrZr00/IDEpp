using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using IdePlusPlus.ProjectSystem;
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

/// <summary>Collapses on null or an empty string - used for a summary field that only exists once a
/// schedule has actually run (LastResultSummary), so the row doesn't show an empty line before then.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

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

/// <summary>Generic "does this bound enum equal that fixed enum member" check - used for the Theme
/// submenu's checkable MenuItems, where each item's IsChecked reflects whether it's the currently
/// active AppTheme. ConverterParameter is the enum member to compare against, e.g.
/// {x:Static editor:AppTheme.Dark}.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && value.Equals(parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Per-node icon glyph for Solution Explorer, keyed off SolutionTreeNodeKind and (for a
/// File node) its extension. Kind and Name are both set once at construction and never change (see
/// SolutionTreeNode), so a MultiBinding on them needs no live-refresh wiring the way a folder
/// open/closed variant driven off IsExpanded would. No icon assets exist anywhere in the codebase,
/// so this is a small glyph lookup - following the same static-Instance shape as
/// TestOutcomeToGlyphConverter above - rather than an icon font or image set.</summary>
public sealed class SolutionTreeIconConverter : IMultiValueConverter
{
    public static readonly SolutionTreeIconConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [SolutionTreeNodeKind kind, string name, ..])
        {
            return "";
        }

        return kind switch
        {
            SolutionTreeNodeKind.Solution => "🗂",
            SolutionTreeNodeKind.Project => "📦",
            SolutionTreeNodeKind.Folder => "📁",
            SolutionTreeNodeKind.File => IconForExtension(Path.GetExtension(name)),
            _ => "",
        };
    }

    private static string IconForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => "🔷",
        ".xaml" => "🎨",
        ".csproj" or ".vbproj" or ".fsproj" => "📦",
        ".sln" or ".slnx" => "🗂",
        ".json" => "🔧",
        ".md" => "📝",
        ".xml" or ".config" or ".resx" => "📃",
        _ => "📄",
    };

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
