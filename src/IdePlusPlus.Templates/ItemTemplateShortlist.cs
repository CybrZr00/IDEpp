namespace IdePlusPlus.Templates;

/// <summary>
/// The handful of item templates that go straight onto File &gt; New, rather than behind the New Item
/// dialog.
///
/// <para>A shortlist is necessary because the catalog cannot be filtered down to something
/// menu-sized on its own: verified on the 10.0.301 SDK, <c>dotnet new list --type item --project</c>
/// a plain class library still offers API Controller and all seven MAUI item templates, because their
/// only constraint is the <c>CSharp</c> project capability. 33 rows is a dialog, not a menu.</para>
/// </summary>
public static class ItemTemplateShortlist
{
    /// <summary>In menu order, most-used first. Each is a template the SDK genuinely ships - they are
    /// resolved against the catalog rather than assumed, so a machine without one simply doesn't show
    /// it.</summary>
    public static readonly IReadOnlyList<string> ShortNames =
        ["class", "interface", "enum", "record", "apicontroller"];

    /// <summary>The shortlist entries present in <paramref name="itemGroups"/>, in shortlist order.</summary>
    public static IReadOnlyList<TemplateGroup> From(IEnumerable<TemplateGroup> itemGroups)
    {
        var groups = itemGroups.ToList();

        return ShortNames
            .Select(shortName => groups.FirstOrDefault(
                group => group.Templates.Any(t => t.ShortNames.Contains(shortName, StringComparer.OrdinalIgnoreCase))))
            .OfType<TemplateGroup>()
            .ToList();
    }
}
