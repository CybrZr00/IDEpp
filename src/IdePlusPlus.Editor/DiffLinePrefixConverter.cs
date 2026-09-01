using System.Globalization;
using System.Windows.Data;

namespace IdePlusPlus.Editor.Diff;

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
