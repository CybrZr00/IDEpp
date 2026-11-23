using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// The open solution's conversation transcripts as a searchable secondary memory.
///
/// Recording and embedding are deliberately separate. Record writes the entry to disk straight away
/// (TranscriptStore) and that is the only step that has to succeed: an entry is never lost because an
/// embedding could not be had. Vectors are filled in behind it by a background pump (see
/// TranscriptMemoryService.Vectors.cs), so a missing model or a stopped server costs search quality,
/// not data, and the backlog is embedded as soon as a model is there.
///
/// Search (TranscriptMemoryService.Search.cs) fuses a similarity ranking with a keyword ranking, so it
/// still answers when no embedding model is available.
///
/// Everything is held in memory, with the same brute-force approach VectorIndex takes for code. Only
/// the first <see cref="MaxEmbeddedPassagesPerEntry"/> passages of an entry are embedded (about ten
/// thousand characters), which bounds the cost of a very long reasoning trace; the rest of it is
/// still stored and still found by keyword.
/// </summary>
public sealed partial class TranscriptMemoryService
{
    public const int MaxResults = 10;
    public const int MaxEmbeddedPassagesPerEntry = 8;

    private readonly IEmbeddingClient _embeddingClient;
    private readonly TranscriptStore _store;
    private readonly TimeSpan _retryDelay;
    private volatile State _state = new(null);

    /// <param name="retryDelay">How long background embedding waits after a failed attempt before it
    /// tries again on its own (default one minute). Tests shorten it.</param>
    public TranscriptMemoryService(IEmbeddingClient embeddingClient, TranscriptStore store, TimeSpan? retryDelay = null)
    {
        _embeddingClient = embeddingClient;
        _store = store;
        _retryDelay = retryDelay ?? TimeSpan.FromMinutes(1);
    }

    public string? SolutionDirectory => _state.Directory;

    /// <summary>Why this solution's saved transcripts could not be read in full, if they could not.
    /// New entries are still recorded, since the store only ever appends, but search then sees only
    /// what was recorded since the solution was opened.</summary>
    public string? LoadError => _state.LoadError;

    public int EntryCount
    {
        get
        {
            var state = _state;
            lock (state.Gate)
            {
                return state.EntryCount;
            }
        }
    }

    /// <summary>Switches to a solution's transcripts (or to none, with null), loading what is saved.
    /// Whatever the previous solution was embedding is stopped.</summary>
    public void SetSolution(string? solutionDirectory)
    {
        var state = new State(solutionDirectory);
        if (solutionDirectory is not null)
        {
            try
            {
                lock (state.Gate)
                {
                    foreach (var entry in _store.LoadAll(solutionDirectory))
                    {
                        AddEntryLocked(state, entry);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.LoadError = ex.Message;
            }

            // Attaches the vectors saved for the current model, and queues whatever has none.
            EnsureVectorModel(state);
        }

        // Published only once loaded. Nothing records during a solution switch (the running turn is
        // stopped first), so no entry can fall between the load above and this line.
        Interlocked.Exchange(ref _state, state).Cancellation.Cancel();
        SignalPump(state);
    }

    /// <summary>Starts a new conversation's transcript and returns its id. Null when there is no
    /// solution open or the folder cannot be written.</summary>
    public string? StartConversation() => StartConversation(scheduled: false);

    private string? StartConversation(bool scheduled)
    {
        if (_state.Directory is not { } directory)
        {
            return null;
        }

        try
        {
            return _store.StartConversation(directory, scheduled: scheduled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts a conversation of its own for a run that is not part of any chat (a scheduled
    /// automation) and returns a recorder for it. Null when that solution is no longer the open one,
    /// or nothing can be written.
    /// </summary>
    public TranscriptRecorder? StartRecorder(string solutionDirectory, string source)
    {
        if (_state.Directory is not { } directory || !SameDirectory(directory, solutionDirectory))
        {
            return null;
        }

        return StartConversation(scheduled: true) is { } conversationId
            ? new TranscriptRecorder(this, solutionDirectory, conversationId, source)
            : null;
    }

    /// <summary>The newest conversation on disk, for carrying on with the one that was open when the
    /// IDE last closed.</summary>
    public string? LatestConversationId()
    {
        if (_state.Directory is not { } directory)
        {
            return null;
        }

        try
        {
            return _store.LatestConversationId(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes one entry to the conversation's transcript, then makes it searchable. Returns false only
    /// when it could not be written; blank text is not an error and records nothing. Never throws:
    /// a transcript is a convenience and must not be able to cost a chat turn.
    /// </summary>
    /// <param name="onlyIfSolution">For a run that outlives a solution switch (a sub-agent, a scheduled
    /// run): when set, nothing is recorded unless this is still the open solution, so a late entry
    /// cannot land in the next solution's transcripts.</param>
    public bool Record(string conversationId, string kind, string text, string? source = null, string? onlyIfSolution = null)
    {
        var state = _state;
        if (state.Directory is not { } directory)
        {
            return false;
        }

        if (onlyIfSolution is not null && !SameDirectory(directory, onlyIfSolution))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        TranscriptEntry entry;
        try
        {
            entry = _store.Append(directory, conversationId, kind, text, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }

        lock (state.Gate)
        {
            AddEntryLocked(state, entry);
        }

        SignalPump(state);
        return true;
    }

    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Splits an entry into passages and adds them. Caller holds the state's gate.</summary>
    private static void AddEntryLocked(State state, TranscriptEntry entry)
    {
        var chunks = TranscriptChunker.Chunk(entry.Text);
        for (var i = 0; i < chunks.Count; i++)
        {
            var passage = new Passage(entry, i, chunks[i]);
            state.Passages.Add(passage);
            state.ByKey[passage.Key] = passage;
            if (passage.Embeddable)
            {
                state.Pending.Enqueue(passage);
            }
        }

        state.EntryCount++;
    }

    private sealed class State(string? directory)
    {
        public string? Directory { get; } = directory;

        public object Gate { get; } = new();

        /// <summary>Every passage of every entry, oldest first.</summary>
        public List<Passage> Passages { get; } = [];

        public Dictionary<string, Passage> ByKey { get; } = new(StringComparer.Ordinal);

        /// <summary>Embeddable passages with no vector yet for the current model, oldest first.</summary>
        public Queue<Passage> Pending { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>The model the passages' vectors belong to; null until the first look.</summary>
        public string? VectorModelId { get; set; }

        public string? LoadError { get; set; }

        public int EntryCount { get; set; }

        /// <summary>UTC ticks before which background embedding does not try again, after a failure.</summary>
        public long RetryAfterTicks;

        /// <summary>1 while a background embedding run is in progress.</summary>
        public int PumpRunning;

        public Task Pump { get; set; } = Task.CompletedTask;
    }

    private sealed class Passage(TranscriptEntry entry, int index, string text)
    {
        public TranscriptEntry Entry { get; } = entry;

        public int Index { get; } = index;

        public string Text { get; } = text;

        public string Key => $"{Entry.Id}/{Index}";

        public bool Embeddable => Index < MaxEmbeddedPassagesPerEntry;

        /// <summary>For the state's VectorModelId. Written by the background run, read by searches.</summary>
        public volatile float[]? Vector;
    }
}
