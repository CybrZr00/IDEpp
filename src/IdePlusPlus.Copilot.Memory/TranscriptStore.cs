using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdePlusPlus.Copilot.Memory;

/// <summary>
/// The permanent record of every conversation, thoughts included. Unlike chat-history.json (the live
/// conversation, emptied by New Chat) and project-memory.md (a curated summary), nothing here is ever
/// rewritten or deleted by the app: each conversation is one append-only JSON Lines file under
/// .ideplusplus/copilot/transcripts, one entry per line, written the moment a message is complete.
/// That is what makes it safe as a secondary memory - clearing or summarising the live conversation
/// cannot lose anything that is in here.
///
/// Append-only is also why a failed read is not dangerous: a line that cannot be parsed (a write cut
/// short by a crash) is skipped, and the next append starts on a fresh line, so one bad line never
/// costs the entries around it.
/// </summary>
public sealed class TranscriptStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Local storage, never embedded in HTML: the default escaping turns every '<' and '>' in
        // code-heavy text into a six-character escape, and every non-ASCII letter into another.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();

    /// <summary>Per file: the last sequence number written and whether the file ends in a newline.
    /// Read from disk once, the first time a file is appended to by this process.</summary>
    private readonly Dictionary<string, FileTail> _tails = new(StringComparer.OrdinalIgnoreCase);

    public static string TranscriptsDirectory(string solutionDirectory) =>
        Path.Combine(ProjectMemoryStore.MemoryDirectory(solutionDirectory), "transcripts");

    /// <summary>Beside the transcripts, so everything derived from them travels with them.</summary>
    public static string VectorsFilePath(string solutionDirectory) =>
        Path.Combine(TranscriptsDirectory(solutionDirectory), "vectors-v1.jsonl");

    private static string ConversationPath(string solutionDirectory, string conversationId) =>
        Path.Combine(TranscriptsDirectory(solutionDirectory), conversationId + ".jsonl");

    /// <summary>Ids become file names, so only what StartConversation itself can produce is accepted.</summary>
    private static bool IsValidConversationId(string conversationId) =>
        conversationId.Length is > 0 and <= 64
        && conversationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>
    /// Reserves a new conversation and returns its id - a local timestamp, so ids sort in the order the
    /// conversations began. The file is created here, empty, which is what makes the id unique: two
    /// conversations started within the same second get "..._2" rather than the same file.
    /// </summary>
    public string StartConversation(string solutionDirectory, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            var directory = TranscriptsDirectory(solutionDirectory);
            Directory.CreateDirectory(directory);

            var stem = (now ?? DateTimeOffset.Now).ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            for (var suffix = 1; ; suffix++)
            {
                var id = suffix == 1 ? stem : $"{stem}_{suffix}";
                var path = ConversationPath(solutionDirectory, id);
                try
                {
                    using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                    }

                    _tails[path] = new FileTail(0, EndsWithNewline: true);
                    return id;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Taken - try the next suffix.
                }
            }
        }
    }

    /// <summary>The most recently started conversation, or null when there are none.</summary>
    public string? LatestConversationId(string solutionDirectory)
    {
        lock (_gate)
        {
            var directory = TranscriptsDirectory(solutionDirectory);
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.jsonl")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(id => id is not null && !id.StartsWith("vectors", StringComparison.Ordinal))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .LastOrDefault()
                : null;
        }
    }

    /// <summary>Adds one entry to the end of a conversation's transcript and returns it. The line is
    /// written in a single call, so a reader never sees half of it from this process.</summary>
    public TranscriptEntry Append(
        string solutionDirectory,
        string conversationId,
        string kind,
        string text,
        string? source = null,
        DateTimeOffset? now = null)
    {
        if (!IsValidConversationId(conversationId))
        {
            throw new ArgumentException($"'{conversationId}' is not a conversation id.", nameof(conversationId));
        }

        lock (_gate)
        {
            var path = ConversationPath(solutionDirectory, conversationId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tail = _tails.TryGetValue(path, out var known) ? known : ReadTail(path);
            var entry = new TranscriptEntry(conversationId, tail.LastSequence + 1, now ?? DateTimeOffset.Now, kind, text, source);

            // A file whose last line was cut short has no newline; appending straight onto it would
            // weld the new entry to the broken one and lose both.
            var line = (tail.EndsWithNewline ? "" : "\n") + JsonSerializer.Serialize(entry, JsonOptions) + "\n";
            File.AppendAllText(path, line, Utf8NoBom);

            _tails[path] = new FileTail(entry.Sequence, EndsWithNewline: true);
            return entry;
        }
    }

    /// <summary>Every entry of every conversation, oldest conversation first and in order within each.
    /// Unreadable lines are skipped.</summary>
    public IReadOnlyList<TranscriptEntry> LoadAll(string solutionDirectory)
    {
        lock (_gate)
        {
            var directory = TranscriptsDirectory(solutionDirectory);
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var entries = new List<TranscriptEntry>();
            var files = Directory.EnumerateFiles(directory, "*.jsonl")
                .Where(f => !Path.GetFileName(f).StartsWith("vectors", StringComparison.Ordinal))
                .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal);
            foreach (var file in files)
            {
                entries.AddRange(ReadEntries(file));
            }

            return entries;
        }
    }

    /// <summary>One conversation's entries in order; empty for an id that has none.</summary>
    public IReadOnlyList<TranscriptEntry> LoadConversation(string solutionDirectory, string conversationId)
    {
        if (!IsValidConversationId(conversationId))
        {
            return [];
        }

        lock (_gate)
        {
            var path = ConversationPath(solutionDirectory, conversationId);
            return File.Exists(path) ? ReadEntries(path) : [];
        }
    }

    private static FileTail ReadTail(string path)
    {
        if (!File.Exists(path))
        {
            return new FileTail(0, EndsWithNewline: true);
        }

        var lastSequence = 0;
        foreach (var entry in ReadEntries(path))
        {
            lastSequence = Math.Max(lastSequence, entry.Sequence);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var endsWithNewline = stream.Length == 0;
        if (!endsWithNewline)
        {
            stream.Seek(-1, SeekOrigin.End);
            endsWithNewline = stream.ReadByte() == '\n';
        }

        return new FileTail(lastSequence, endsWithNewline);
    }

    /// <summary>FileShare.ReadWrite: another part of this process may be appending to the same file.</summary>
    private static List<TranscriptEntry> ReadEntries(string path)
    {
        var entries = new List<TranscriptEntry>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<TranscriptEntry>(line, JsonOptions) is { } entry
                    && entry.Sequence > 0
                    && entry.Kind is not null
                    && entry.Text is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A line cut short by a crash. The entries either side of it are intact.
            }
        }

        return entries;
    }

    private readonly record struct FileTail(int LastSequence, bool EndsWithNewline);
}
