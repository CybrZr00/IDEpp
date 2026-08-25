using ICSharpCode.AvalonEdit.Highlighting;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

/// <summary>
/// Captures AvalonEdit's shipped syntax palette so a test can hand it back untouched afterwards.
/// HighlightingManager.Instance hands out one shared HighlightingColor instance per named color,
/// process-wide, and SyntaxHighlightingRecolorer.ApplyTheme mutates those instances in place - that
/// is the behavior under test, not an accident. Left unmanaged it makes the recoloring tests
/// order-dependent: whichever one runs first leaves the palette already themed, so the next one's
/// "before" snapshot is really an "after" and its own ApplyTheme call is a no-op. Capture() records
/// the pristine foregrounds and Restore() puts them back, so each test starts from the shipped
/// palette no matter what ran before it.
/// </summary>
internal sealed class BuiltInHighlightingPalette
{
    private readonly List<(HighlightingColor Color, HighlightingBrush? Foreground)> originals;

    private BuiltInHighlightingPalette(List<(HighlightingColor Color, HighlightingBrush? Foreground)> originals) =>
        this.originals = originals;

    public static BuiltInHighlightingPalette Capture()
    {
        var originals = new List<(HighlightingColor Color, HighlightingBrush? Foreground)>();

        foreach (var definition in HighlightingManager.Instance.HighlightingDefinitions)
        {
            foreach (var namedColor in definition.NamedHighlightingColors)
            {
                originals.Add((namedColor, namedColor.Foreground));
            }
        }

        return new BuiltInHighlightingPalette(originals);
    }

    public void Restore()
    {
        foreach (var (color, foreground) in originals)
        {
            // A color nothing reassigned needs no write - and skipping it also keeps Restore from
            // throwing on a frozen HighlightingColor, which for the same reason can never have been
            // the one that changed (SyntaxHighlightingRecolorer swallows that same failure).
            if (!ReferenceEquals(color.Foreground, foreground))
            {
                color.Foreground = foreground;
            }
        }
    }
}

/// <summary>
/// Serializes every test class that recolors AvalonEdit's process-wide highlighting definitions.
/// xUnit runs separate test classes in parallel, which would let one class's ApplyTheme land inside
/// another's capture/restore window; putting them all in one collection means only the in-class
/// snapshot below has to be right.
/// </summary>
[CollectionDefinition(BuiltInHighlightingCollection.Name, DisableParallelization = true)]
public sealed class BuiltInHighlightingCollection
{
    public const string Name = "BuiltInHighlighting";
}
