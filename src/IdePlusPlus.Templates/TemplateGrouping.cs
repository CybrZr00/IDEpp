namespace IdePlusPlus.Templates;

/// <summary>
/// Collapses raw cache entries into the rows a user picks from.
///
/// <para>This is not a tidying step. 48 of the 56 C# project-template groups in a stock .NET 10 cache
/// have more than one entry, because the SDK installs the 8.0, 9.0 and 10.0 template packages side by
/// side and each ships its own copy: listing the cache unfiltered shows "Class Library" three times,
/// "NUnit Test Item" twice under two different names, and Class once per language.</para>
/// </summary>
public static class TemplateGrouping
{
    /// <summary>
    /// Groups by group identity, ordered by name. Pass <paramref name="kind"/> to keep only project,
    /// item or solution templates - the New Item list would otherwise be 149 project templates deep.
    /// </summary>
    public static IReadOnlyList<TemplateGroup> Group(
        IEnumerable<TemplateInfo> templates, TemplateKind? kind = null)
    {
        var filtered = kind is null ? templates : templates.Where(t => t.Kind == kind);

        return filtered
            .GroupBy(t => t.GroupIdentity, StringComparer.Ordinal)
            .Select(group => new TemplateGroup(group.Key, group.ToList()))
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The groups matching a free-text query across name, short names, description and tags -
    /// everything the SDK's own table shows, since a user searching "api" is as likely to be thinking
    /// of the "Web/ASP.NET" tag as the words in the title. An empty query keeps everything.
    /// </summary>
    public static IReadOnlyList<TemplateGroup> Search(IEnumerable<TemplateGroup> groups, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return groups.ToList();
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return groups.Where(group => terms.All(term => Matches(group, term))).ToList();
    }

    private static bool Matches(TemplateGroup group, string term) =>
        Contains(group.Name, term) ||
        Contains(group.Description, term) ||
        group.Templates.Any(t => t.ShortNames.Any(shortName => Contains(shortName, term))) ||
        group.Classifications.Any(classification => Contains(classification, term));

    private static bool Contains(string? text, string term) =>
        text is not null && text.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}
