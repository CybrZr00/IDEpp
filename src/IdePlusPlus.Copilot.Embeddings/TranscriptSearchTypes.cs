using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>Which transcript entries a search looks at.</summary>
public enum TranscriptKindFilter
{
    /// <summary>User messages, assistant messages and thoughts.</summary>
    Any,

    /// <summary>User and assistant messages, not the reasoning behind them.</summary>
    Messages,

    /// <summary>Only the assistant's reasoning.</summary>
    Thoughts,
}

/// <summary>One matching entry: the best-matching passage of it, not the whole entry, which for a long
/// reasoning trace could be many thousands of characters.</summary>
public sealed record TranscriptSearchHit(TranscriptEntry Entry, string Excerpt, double Score);

/// <summary>
/// What a search found, and how well it could look. SemanticAvailable is false when the question could
/// not be embedded (no model installed, server not running): the hits are then keyword matches only,
/// which the caller should say rather than present as a similarity ranking. UnembeddedPassages counts
/// the stored passages that had no vector yet and so were only reachable by keyword.
/// </summary>
public sealed record TranscriptSearchResult(
    IReadOnlyList<TranscriptSearchHit> Hits,
    bool SemanticAvailable,
    int UnembeddedPassages);
