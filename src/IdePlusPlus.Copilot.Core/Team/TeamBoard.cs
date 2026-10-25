using System.Text;
using System.Text.Json;

namespace IdePlusPlus.Copilot.Core.Team;

public enum TeamBoardEntryType
{
    Observed,
    Fact,
    Fail,
    Claim,
    PatchSummary,
}

public sealed record TeamBoardEntry(
    int Number, TeamBoardEntryType Type, string Author, string Text, string? Detail, DateTimeOffset Time)
{
    /// <summary>The handle peers pass to board_unfold. "G" for the Agensh paper's gist labels,
    /// which this board is modelled on.</summary>
    public string Id => $"G{Number}";

    public string TypeName => TeamBoard.TypeNameOf(Type);
}

/// <summary>
/// An Ultra team's shared memory: the shared-context board from Agensh (arXiv 2609.26781). Workers
/// coordinate through it instead of through a lead - they read it before choosing work, claim an
/// area on it before editing, and record what they confirmed, what failed, and what they merged.
///
/// Entries are append-only and deliberately short. A board that peers have to skim at the start of
/// every cycle stops working once entries are paragraphs, so text is capped and anything longer
/// goes in Detail, which a peer expands with board_unfold only if the gist is relevant to them.
///
/// Claims are the one thing that changes after the fact: each author has at most one active claim -
/// their latest - and it lapses when it expires, when they post a PATCH_SUMMARY after it (the work
/// is done), or when they leave the run. Nothing is enforced by a claim; it only tells peers to
/// pick a different area, which is the whole of Agensh's conflict avoidance short of git itself.
///
/// Thread-safe: every worker writes to the same instance from its own thread-pool continuation.
/// </summary>
public sealed class TeamBoard
{
    public const int MaxTextLength = 100;
    public const int MaxPatchSummaryLength = 300;
    public const int MaxDetailLength = 4_000;
    public const int ReadWindow = 200;
    public const int MaxGrepResults = 100;

    private readonly object _lock = new();
    private readonly List<TeamBoardEntry> _entries = [];
    private readonly Dictionary<string, int> _noticeCursors = new(StringComparer.Ordinal);
    private readonly HashSet<int> _releasedClaims = [];
    private readonly TimeProvider _time;
    private readonly string? _journalPath;

    public TeamBoard(TimeProvider time, TimeSpan claimLifetime, string? journalPath = null)
    {
        _time = time;
        ClaimLifetime = claimLifetime;
        _journalPath = journalPath;
    }

    public TimeSpan ClaimLifetime { get; }

    /// <summary>Raised after an entry is added, outside the lock, on the writer's thread.</summary>
    public event Action<TeamBoardEntry>? EntryAdded;

    public static string TypeNameOf(TeamBoardEntryType type) => type switch
    {
        TeamBoardEntryType.Observed => "OBSERVED",
        TeamBoardEntryType.Fact => "FACT",
        TeamBoardEntryType.Fail => "FAIL",
        TeamBoardEntryType.Claim => "CLAIM",
        TeamBoardEntryType.PatchSummary => "PATCH_SUMMARY",
        _ => type.ToString().ToUpperInvariant(),
    };

    public static bool TryParseType(string? name, out TeamBoardEntryType type)
    {
        foreach (var candidate in Enum.GetValues<TeamBoardEntryType>())
        {
            if (string.Equals(TypeNameOf(candidate), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>Adds an entry, or returns why not. Refusals say how to fix the entry rather than
    /// just that it is wrong - the caller is a model that will try again with whatever it is told.</summary>
    public (TeamBoardEntry? Entry, string? Error) Write(string author, string? typeName, string? text, string? detail)
    {
        if (!TryParseType(typeName, out var type))
        {
            return (null, $"Unknown board entry type '{typeName}'. Use one of: "
                          + string.Join(", ", Enum.GetValues<TeamBoardEntryType>().Select(TypeNameOf)) + ".");
        }

        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            return (null, "A board entry needs some text.");
        }

        var limit = type == TeamBoardEntryType.PatchSummary ? MaxPatchSummaryLength : MaxTextLength;
        if (text.Length > limit)
        {
            return (null,
                $"That {TypeNameOf(type)} text is {text.Length} characters; the limit is {limit}. Keep the text "
                + "to the gist a peer needs to decide whether it matters to them, and put the rest in 'detail'.");
        }

        if (type == TeamBoardEntryType.PatchSummary
            && (!text.Contains("files=", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("evidence=", StringComparison.OrdinalIgnoreCase)))
        {
            return (null,
                "A PATCH_SUMMARY has the form \"files=<what changed> | idea=<what it does> | evidence=<the build "
                + "or test result that shows it works>\" - peers rely on the evidence part to trust it without "
                + "re-checking.");
        }

        detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        if (detail is { Length: > MaxDetailLength })
        {
            detail = detail[..MaxDetailLength] + " [truncated]";
        }

        TeamBoardEntry entry;
        lock (_lock)
        {
            entry = new TeamBoardEntry(_entries.Count + 1, type, author, text, detail, _time.GetUtcNow());
            _entries.Add(entry);
        }

        AppendToJournal(entry);
        EntryAdded?.Invoke(entry);
        return (entry, null);
    }

    /// <summary>Ends an author's claim without a PATCH_SUMMARY - they finished, or left the run.</summary>
    public void ReleaseClaims(string author)
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Where(e => e.Type == TeamBoardEntryType.Claim && e.Author == author))
            {
                _releasedClaims.Add(entry.Number);
            }
        }
    }

    public IReadOnlyList<TeamBoardEntry> ActiveClaims()
    {
        lock (_lock)
        {
            return ActiveClaimsLocked(_time.GetUtcNow());
        }
    }

    public IReadOnlyList<TeamBoardEntry> Snapshot()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    /// <summary>What board_read returns: every active claim first, since those are what decide
    /// what a worker should not pick, then the latest entries oldest-first so it reads as a log.</summary>
    public string Read()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_entries.Count == 0)
            {
                return "The board is empty - nobody has posted anything yet.";
            }

            var report = new StringBuilder();
            var claims = ActiveClaimsLocked(now);
            report.Append(claims.Count == 0 ? "Active claims: none.\n" : "Active claims:\n");
            foreach (var claim in claims)
            {
                var left = claim.Time + ClaimLifetime - now;
                report.Append($"  {Format(claim)} (lapses in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min)\n");
            }

            var latest = _entries
                .Where(e => e.Type != TeamBoardEntryType.Claim || !claims.Contains(e))
                .TakeLast(ReadWindow)
                .ToList();
            var shown = latest.Count < _entries.Count - claims.Count
                ? $"latest {latest.Count} of {_entries.Count}; board_grep searches all of them"
                : $"{latest.Count}";
            report.Append($"\nEntries ({shown}):\n");
            foreach (var entry in latest)
            {
                report.Append("  ").Append(Format(entry)).Append('\n');
            }

            return report.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Searches every entry's type, author, text and detail, case-insensitively. Agensh's syntax:
    /// a comma separates alternatives and an ampersand joins terms that must all match, so
    /// "build&amp;fail, flaky" means (build AND fail) OR flaky.
    /// </summary>
    public string Grep(string? query)
    {
        var groups = (query ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(g => g.Length > 0)
            .ToList();

        if (groups.Count == 0)
        {
            return "board_grep needs a query - keywords, with ',' for OR and '&' for AND.";
        }

        List<TeamBoardEntry> matches;
        lock (_lock)
        {
            matches = _entries
                .Where(e =>
                {
                    var haystack = $"{e.TypeName} {e.Author} {e.Text} {e.Detail}";
                    return groups.Any(g => g.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)));
                })
                .ToList();
        }

        if (matches.Count == 0)
        {
            return $"No board entries match '{query}'.";
        }

        var shown = matches.TakeLast(MaxGrepResults).ToList();
        var header = matches.Count > shown.Count
            ? $"{matches.Count} entries match; the latest {shown.Count} are shown:\n"
            : $"{matches.Count} entries match:\n";
        return header + string.Join("\n", shown.Select(e => "  " + Format(e)));
    }

    public string Unfold(string? id)
    {
        var number = int.TryParse(id?.Trim().TrimStart('G', 'g'), out var parsed) ? parsed : 0;
        lock (_lock)
        {
            if (number < 1 || number > _entries.Count)
            {
                return $"There is no board entry '{id}'. Ids look like G12.";
            }

            var entry = _entries[number - 1];
            return entry.Detail is null
                ? $"{Format(entry)}\n(no detail beyond the text)"
                : $"{Format(entry)}\n\n{entry.Detail}";
        }
    }

    /// <summary>
    /// Entries by other authors that this viewer has not been shown as a notice yet, limited to the
    /// given types, advancing the viewer's cursor past everything up to now either way. Used for
    /// the mid-turn [team notices] block, which carries only what should change what a worker is
    /// doing right now - not every OBSERVED, which can wait for their next board_read.
    /// </summary>
    public IReadOnlyList<TeamBoardEntry> TakeUnseen(string viewer, IReadOnlySet<TeamBoardEntryType> types)
    {
        lock (_lock)
        {
            var cursor = _noticeCursors.GetValueOrDefault(viewer);
            _noticeCursors[viewer] = _entries.Count;
            return _entries
                .Skip(cursor)
                .Where(e => e.Author != viewer && types.Contains(e.Type))
                .ToList();
        }
    }

    public static string Format(TeamBoardEntry entry) =>
        $"{entry.Id} [{entry.TypeName}] {entry.Author}: {entry.Text}"
        + (entry.Detail is null ? "" : $" (+detail: board_unfold {entry.Id})");

    private List<TeamBoardEntry> ActiveClaimsLocked(DateTimeOffset now)
    {
        var active = new List<TeamBoardEntry>();
        foreach (var author in _entries.Select(e => e.Author).Distinct())
        {
            var latestClaim = _entries.LastOrDefault(e => e.Author == author && e.Type == TeamBoardEntryType.Claim);
            if (latestClaim is null
                || _releasedClaims.Contains(latestClaim.Number)
                || now - latestClaim.Time >= ClaimLifetime
                || _entries.Any(e => e.Author == author
                                     && e.Type == TeamBoardEntryType.PatchSummary
                                     && e.Number > latestClaim.Number))
            {
                continue;
            }

            active.Add(latestClaim);
        }

        return active;
    }

    /// <summary>Best effort: the journal is for looking at a run afterwards, and a locked or full
    /// disk must not stop the team working.</summary>
    private void AppendToJournal(TeamBoardEntry entry)
    {
        if (_journalPath is null)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new
        {
            id = entry.Id,
            type = entry.TypeName,
            author = entry.Author,
            text = entry.Text,
            detail = entry.Detail,
            time = entry.Time,
        });

        try
        {
            lock (_lock)
            {
                File.AppendAllText(_journalPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
