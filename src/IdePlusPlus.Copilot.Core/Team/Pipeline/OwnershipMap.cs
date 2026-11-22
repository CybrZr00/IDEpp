namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// Who owns which file - the thing the dispatcher enforces. Built from the validated design; a file has
/// exactly one owner, and an agent may edit a file only if it is that owner. Paths are repository-
/// relative with forward slashes and compared without regard to case (the files live on Windows, where
/// "Foo.cs" and "foo.cs" are one file and a lookup that told them apart would be a way round the rule).
///
/// Immutable: a claim for a new file gives back a new map, so the runner can swap it in atomically and
/// no agent ever sees one half-updated.
/// </summary>
public sealed class OwnershipMap
{
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

    private OwnershipMap(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (path, owner) in entries)
        {
            _owners[path] = owner;
        }
    }

    public static OwnershipMap Empty { get; } = new([]);

    /// <summary>The map for a design that has passed <see cref="DesignValidator"/>.</summary>
    public static OwnershipMap From(DesignPlan plan) =>
        new(plan.Files.Select(f => KeyValuePair.Create(f.Path, f.Owner)));

    /// <summary>Reads back a map written with <see cref="Entries"/>. Entries whose path is not a plain
    /// relative path are dropped, so a damaged file can never grant ownership of something outside.</summary>
    public static OwnershipMap FromEntries(IEnumerable<KeyValuePair<string, string>> entries) =>
        new(entries
            .Select(e => (Path: PipelineArtifactStore.NormalizeRelativePath(e.Key), e.Value))
            .Where(e => e.Path is not null && !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => KeyValuePair.Create(e.Path!, e.Value.Trim().ToLowerInvariant())));

    /// <summary>Path to owner, for writing to ownership.json.</summary>
    public IReadOnlyDictionary<string, string> Entries => _owners;

    public IReadOnlyList<string> Files => [.. _owners.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> Owners => [.. _owners.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The owner of <paramref name="path"/>, or null for a file nobody owns or a path that is
    /// not a plain relative one.</summary>
    public string? OwnerOf(string? path) =>
        PipelineArtifactStore.NormalizeRelativePath(path) is { } normalized && _owners.TryGetValue(normalized, out var owner)
            ? owner
            : null;

    public bool IsOwnedBy(string? owner, string? path) =>
        !string.IsNullOrWhiteSpace(owner)
        && string.Equals(OwnerOf(path), owner.Trim(), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> FilesOf(string owner) =>
        [.. _owners.Where(e => string.Equals(e.Value, owner, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Key)
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// A new map with <paramref name="path"/> given to <paramref name="owner"/>, for an owner that has to
    /// create a file the design did not list. Refused for a path that is not a plain relative one, a file
    /// that someone already owns (even the asker - there is nothing to claim), or an owner the map does
    /// not know.
    /// </summary>
    public (OwnershipMap? Map, string? Error) TryClaim(string? owner, string? path)
    {
        var who = owner?.Trim().ToLowerInvariant() ?? "";
        if (!_owners.Values.Contains(who, StringComparer.Ordinal))
        {
            return (null, $"'{who}' is not one of the owners ({string.Join(", ", Owners)}).");
        }

        if (PipelineArtifactStore.NormalizeRelativePath(path) is not { } normalized)
        {
            return (null, $"'{PipelineText.Clean(path)}' is not a plain relative path inside the solution directory.");
        }

        if (_owners.TryGetValue(normalized, out var existing))
        {
            return (null, $"{normalized} is already owned by {existing}.");
        }

        return (new OwnershipMap([.. _owners, KeyValuePair.Create(normalized, who)]), null);
    }
}
