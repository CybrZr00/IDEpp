using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Copilot.Embeddings;

public sealed partial class TranscriptMemoryService
{
    /// <summary>Short, because a question is waiting on it. No answer just means keyword matches only.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Reciprocal rank fusion's damping constant; 60 is the value the technique is usually
    /// given with, and nothing here depends on it being tuned.</summary>
    private const int RankFusionK = 60;

    /// <summary>
    /// Finds the entries closest to a question. An entry is ranked by its best passage, so one long
    /// reasoning trace cannot fill every slot, and each hit carries just that passage.
    /// </summary>
    public async Task<TranscriptSearchResult> SearchAsync(
        string query, TranscriptKindFilter kinds, int limit, CancellationToken cancellationToken)
    {
        var state = _state;
        if (state.Directory is null || string.IsNullOrWhiteSpace(query))
        {
            return new TranscriptSearchResult([], SemanticAvailable: false, UnembeddedPassages: 0);
        }

        limit = Math.Clamp(limit, 1, MaxResults);
        EnsureVectorModel(state);

        Passage[] candidates;
        lock (state.Gate)
        {
            candidates = state.Passages.Where(p => Includes(kinds, p.Entry.Kind)).ToArray();
        }

        // Not worth a model call when nothing has a vector to compare it with.
        var queryVector = candidates.Any(p => p.Vector is not null)
            ? await _embeddingClient.GetEmbeddingAsync(query, cancellationToken, QueryTimeout, isQuery: true)
            : null;

        var terms = TranscriptKeywordMatcher.Terms(query);
        var semantic = RankBySimilarity(candidates, queryVector);
        var keyword = RankByKeywords(candidates, terms);

        var hits = Fuse(semantic, keyword, terms.Count)
            .Take(limit)
            .Select(x => new TranscriptSearchHit(x.Passage.Entry, x.Passage.Text, x.Score))
            .ToList();

        return new TranscriptSearchResult(
            hits,
            SemanticAvailable: semantic.Count > 0,
            UnembeddedPassages: candidates.Count(p => p.Embeddable && p.Vector is null));
    }

    private static bool Includes(TranscriptKindFilter filter, string kind) => filter switch
    {
        TranscriptKindFilter.Messages => kind is TranscriptKinds.User or TranscriptKinds.Assistant,
        TranscriptKindFilter.Thoughts => kind == TranscriptKinds.Thought,
        _ => true,
    };

    /// <summary>One passage per entry (its closest), best first.</summary>
    private static List<(Passage Passage, double Score)> RankBySimilarity(Passage[] candidates, float[]? queryVector)
    {
        if (queryVector is null)
        {
            return [];
        }

        return candidates
            .Where(p => p.Vector is { } v && v.Length == queryVector.Length)
            .Select(p => (Passage: p, Score: Cosine(queryVector, p.Vector!)))
            .Where(x => x.Score > 0) // Nothing in common is not a match, however few entries there are.
            .GroupBy(x => x.Passage.Entry.Id)
            .Select(g => g.MaxBy(x => x.Score))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Passage.Entry.Timestamp)
            .ToList();
    }

    /// <summary>One passage per entry (the one holding most of the terms), best first, newest first on a tie.</summary>
    private static List<(Passage Passage, double Score)> RankByKeywords(Passage[] candidates, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return [];
        }

        return candidates
            .Select(p => (Passage: p, Score: TranscriptKeywordMatcher.Score(p.Text, terms)))
            .Where(x => x.Score > 0)
            .GroupBy(x => x.Passage.Entry.Id)
            .Select(g => g.MaxBy(x => x.Score))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Passage.Entry.Timestamp)
            .ThenByDescending(x => x.Passage.Entry.Sequence)
            .ToList();
    }

    /// <summary>
    /// Reciprocal rank fusion: an entry scores 1/(k+rank) in each list it appears in and the scores
    /// add, so one that both rankings like beats one only a single ranking likes, without having to
    /// compare a cosine similarity with a keyword count.
    ///
    /// Which passage of the entry to show: one that holds every word of the question, if there is one,
    /// since that is where the asker's own words are. Failing that, the one closest in meaning.
    /// </summary>
    /// <param name="termCount">How many search terms the question had; a keyword score of ten per
    /// term (see TranscriptKeywordMatcher.Score) or more means a passage has all of them.</param>
    private static IEnumerable<(Passage Passage, double Score)> Fuse(
        List<(Passage Passage, double Score)> semantic, List<(Passage Passage, double Score)> keyword, int termCount)
    {
        var fused = new Dictionary<string, (Passage Passage, double Score)>(StringComparer.Ordinal);

        void Add(List<(Passage Passage, double Score)> ranking, bool isKeyword)
        {
            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var (passage, rawScore) = ranking[rank];
                var contribution = 1.0 / (RankFusionK + rank + 1);
                var holdsEveryTerm = isKeyword && rawScore >= termCount * 10;
                var id = passage.Entry.Id;
                fused[id] = fused.TryGetValue(id, out var existing)
                    ? (holdsEveryTerm ? passage : existing.Passage, existing.Score + contribution)
                    : (passage, contribution);
            }
        }

        Add(semantic, isKeyword: false);
        Add(keyword, isKeyword: true);

        return fused.Values
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Passage.Entry.Timestamp)
            .ThenByDescending(x => x.Passage.Entry.Sequence);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
