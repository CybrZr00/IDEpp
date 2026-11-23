namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// What one agent may read of the run folder, as the backing for its <c>artifact_read</c> tool. The
/// design reads the surveys; a judge reads the design and the loop digest; an owner reads the design
/// and its own notes - and nobody reads another agent's verdict while it is still forming its own.
/// Each entry of the allow-list is either one file ("design.json") or a folder, written with a
/// trailing slash ("survey/"), which allows everything beneath it.
///
/// Every path is checked twice: against the allow-list as the model wrote it, and then by the store,
/// which refuses anything that escapes the run folder.
/// </summary>
public sealed class PipelineArtifactReader
{
    /// <summary>Longer artifacts are cut here with a note saying how much is missing, so one big file
    /// cannot take a whole agent's context.</summary>
    public const int DefaultMaxChars = 24_000;

    private readonly PipelineArtifactStore _store;
    private readonly List<(string Path, bool IsFolder)> _allowed = [];
    private readonly int _maxChars;

    public PipelineArtifactReader(PipelineArtifactStore store, IEnumerable<string> allowedPaths, int maxChars = DefaultMaxChars)
    {
        _store = store;
        _maxChars = maxChars > 0 ? maxChars : DefaultMaxChars;
        foreach (var entry in allowedPaths)
        {
            var isFolder = entry.EndsWith('/') || entry.EndsWith('\\');
            if (PipelineArtifactStore.NormalizeRelativePath(entry.TrimEnd('/', '\\')) is { } normalized)
            {
                _allowed.Add((normalized, isFolder));
            }
        }
    }

    /// <summary>The allow-list as it was given, so a refusal can say what the agent may read.</summary>
    public IReadOnlyList<string> Allowed =>
        [.. _allowed.Select(a => a.IsFolder ? a.Path + "/" : a.Path)];

    public bool IsAllowed(string? path)
    {
        if (PipelineArtifactStore.NormalizeRelativePath(path) is not { } normalized)
        {
            return false;
        }

        foreach (var (allowed, isFolder) in _allowed)
        {
            if (isFolder
                    ? normalized.StartsWith(allowed + "/", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(normalized, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The artifacts this agent may read that exist right now.</summary>
    public IReadOnlyList<string> ListReadable() => [.. _store.List().Where(IsAllowed)];

    /// <summary>The artifact's text in the shape a tool result takes: an error flag and the text, with
    /// the reason in the text when it is an error.</summary>
    public async Task<(bool IsError, string Text)> ReadAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(path))
        {
            return (true, $"Refused: '{path}' is not an artifact you may read. You may read: "
                          + (_allowed.Count == 0 ? "nothing." : string.Join(", ", Allowed) + "."));
        }

        var text = await _store.ReadTextAsync(path!, cancellationToken);
        if (text is null)
        {
            return (true, $"There is no artifact '{path}' yet.");
        }

        return text.Length > _maxChars
            ? (false, text[.._maxChars] + $"\n[truncated: {text.Length - _maxChars:N0} more characters]")
            : (false, text);
    }
}
