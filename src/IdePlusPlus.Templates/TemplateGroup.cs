namespace IdePlusPlus.Templates;

/// <summary>
/// One template as a user counts them - one row in the New Project / New Item list - collapsing the
/// several cache entries that share a group identity.
///
/// <para>The entries are kept rather than filtered down to the winner, because the framework picker
/// needs all of them: 48 of the 56 C# project-template groups in the installed cache have more than
/// one entry, and each band's entry offers a *different* Framework choice list. Showing net8.0 in the
/// dropdown and then running the 10.0 entry would silently produce a net10.0 project.</para>
/// </summary>
public sealed record TemplateGroup(string GroupIdentity, IReadOnlyList<TemplateInfo> Templates)
{
    /// <summary>The entry that <c>dotnet new &lt;shortname&gt;</c> would itself resolve to, and so the
    /// one whose name and description this group shows.</summary>
    public TemplateInfo Primary { get; } = Templates.OrderByDescending(t => t.Precedence).First();

    public string Name => Primary.Name;

    public string? Description => Primary.Description;

    public string? Author => Primary.Author;

    public IReadOnlyList<string> Classifications => Primary.Classifications;

    public TemplateKind Kind => Primary.Kind;

    public string ShortName => Primary.ShortName;

    /// <summary>Distinct languages across the group, most-preferred first, blank ones dropped
    /// (config-file templates like gitignore have no language).</summary>
    public IReadOnlyList<string> Languages { get; } = Templates
        .OrderByDescending(t => t.Precedence)
        .Select(t => t.Language)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l => l!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Every target framework any entry for <paramref name="language"/> offers, highest-precedence
    /// entry's choices first so the newest band leads the dropdown.
    /// </summary>
    public IReadOnlyList<string> FrameworkChoicesFor(string? language) =>
        EntriesFor(language)
            .SelectMany(t => t.FrameworkChoices)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The entry to actually instantiate. When <paramref name="framework"/> is given this is the
    /// highest-precedence entry that offers it - which is the whole point of keeping every entry:
    /// asking for net8.0 has to select the 8.0-band template, not pass a flag to the 10.0 one.
    /// Returns null only when the group has nothing for that language.
    /// </summary>
    public TemplateInfo? Resolve(string? language = null, string? framework = null)
    {
        var candidates = EntriesFor(language);
        if (framework is not null)
        {
            var offering = candidates
                .Where(t => t.FrameworkChoices.Contains(framework, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (offering.Count > 0)
            {
                return offering[0];
            }
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>Highest precedence first, so every caller above can just take the first match.</summary>
    private List<TemplateInfo> EntriesFor(string? language) => Templates
        .Where(t => language is null || string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(t => t.Precedence)
        .ToList();
}
