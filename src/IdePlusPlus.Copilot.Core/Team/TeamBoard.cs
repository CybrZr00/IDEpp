using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Copilot.Core.Team;

public enum TeamBoardEntryType
{
    Observed,
    Fact,
    Fail,
    Claim,
    PatchSummary,

    /// <summary>A problem a specialist lane found in merged work - a survived mutant, an unanswered
    /// what-if, a conformance break, a gap against the plan, or a follow-up worth doing later. Its
    /// text carries the fields <see cref="TeamFinding"/> reads: severity, kind, evidence, fix_by.
    /// A high-severity one interrupts other workers the way a FAIL does.</summary>
    Finding,

    /// <summary>A backlog item - seeded from an approved plan, carried from an earlier run, or added
    /// by a worker splitting a big piece. Claimed by naming its id in a CLAIM, done when a
    /// PATCH_SUMMARY names it.</summary>
    Task,
}

public enum TeamTaskStatus
{
    Open,
    Claimed,
    Done,

    /// <summary>Not claimed, and waiting for another TASK to be merged first (its
    /// <see cref="TeamBoardEntry.After"/>). Offered to nobody until then.</summary>
    Blocked,
}

/// <param name="After">For a TASK: the numbers of the TASK entries that must be merged before it can be
/// started. Null or empty when it can start at once.</param>
/// <param name="Lane">The role lane an entry belongs to: a role's name prefix - "b" for a builder,
/// "mut", "adv", "lint", "gap", "next" - or null for something that concerns the whole team (the
/// runner's TASKs, the lead's entries, a FACT any worker may act on). <see cref="TeamBoard.CycleDigest"/>
/// shows a worker's own lane first and only counts the others, so one lane's chatter costs the other
/// lanes nothing.</param>
public sealed record TeamBoardEntry(
    int Number, TeamBoardEntryType Type, string Author, string Text, string? Detail, DateTimeOffset Time,
    IReadOnlyList<int>? After = null, string? Lane = null)
{
    /// <summary>The handle peers pass to board_unfold. "G" for the Agensh paper's gist labels,
    /// which this board is modelled on.</summary>
    public string Id => $"G{Number}";

    public string TypeName => TeamBoard.TypeNameOf(Type);
}

public enum TeamFindingSeverity
{
    Low,
    Medium,
    High,
}

/// <summary>
/// A FINDING's text read back into fields. The text keeps the board's one-line gist shape, with the
/// machine-readable part in front:
/// <c>severity=high kind=security fix_by=builders evidence=branch:3f9a | ..\..\ etc\passwd is read</c>.
/// Only severity and evidence are required - the runner turns findings into TASKs on those two.
/// </summary>
public sealed record TeamFinding(TeamFindingSeverity Severity, string? Kind, string? FixBy, string? Evidence, string Text)
{
    private static readonly Regex Field = new(
        @"\b(?<key>severity|kind|fix_by|evidence)\s*=\s*(?<value>[^\s|,;]+)", RegexOptions.IgnoreCase);

    /// <summary>Null when the text carries no usable severity - a FINDING that cannot be triaged.</summary>
    public static TeamFinding? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? severity = null, kind = null, fixBy = null, evidence = null;
        foreach (var match in Field.Matches(text).Cast<Match>())
        {
            var value = match.Groups["value"].Value.Trim();
            switch (match.Groups["key"].Value.ToLowerInvariant())
            {
                case "severity": severity ??= value; break;
                case "kind": kind ??= value; break;
                case "fix_by": fixBy ??= value; break;
                case "evidence": evidence ??= value; break;
            }
        }

        return severity is null ? null : new TeamFinding(SeverityOf(severity), kind, fixBy, evidence, text);
    }

    public static TeamFindingSeverity SeverityOf(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "high" or "critical" or "blocker" => TeamFindingSeverity.High,
        "medium" or "moderate" => TeamFindingSeverity.Medium,
        _ => TeamFindingSeverity.Low,
    };

    /// <summary>Whether a FINDING entry's text should interrupt other workers mid-step.</summary>
    public static bool IsHigh(string? text) => TryParse(text)?.Severity == TeamFindingSeverity.High;

    /// <summary>The severity as a tool argument or a report would name it.</summary>
    public string SeverityName => Severity.ToString().ToLowerInvariant();
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
public sealed partial class TeamBoard
{
    public const int MaxTextLength = 100;
    public const int MaxPatchSummaryLength = 300;

    /// <summary>Long enough for a role's name prefix ("lint", "next"), short enough that a lane tag
    /// never takes up much of a digest line.</summary>
    public const int MaxLaneLength = 12;

    /// <summary>How much of the board a cycle's opening carries before pointing at board_read and
    /// board_grep for the rest. Small on purpose: every cycle of every worker pays for it.</summary>
    public const int DigestNewEntries = 60;
    public const int DigestFirstCycleEntries = 30;
    public const int DigestTasks = 60;
    public const int MaxDetailLength = 4_000;
    public const int ReadWindow = 200;
    public const int MaxGrepResults = 100;

    private readonly object _lock = new();
    private readonly List<TeamBoardEntry> _entries = [];
    private readonly Dictionary<string, int> _noticeCursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _digestCursors = new(StringComparer.Ordinal);
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
        TeamBoardEntryType.Finding => "FINDING",
        TeamBoardEntryType.Task => "TASK",
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

    /// <summary>A lane tag as the board stores it: trimmed, lower case, null when nothing was given.</summary>
    public static string? NormalizeLane(string? lane)
    {
        var trimmed = lane?.Trim().Trim('[', ']', ' ');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, MaxLaneLength)].ToLowerInvariant();
    }

    /// <summary>Whether a worker viewing as <paramref name="viewerLane"/> should be shown an entry in
    /// <paramref name="entryLane"/>: any untagged entry, anything when the viewer has no lane, and
    /// otherwise only its own lane.</summary>
    public static bool InLane(string? entryLane, string? viewerLane) =>
        entryLane is null
        || viewerLane is null
        || string.Equals(entryLane, viewerLane, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds an entry, or returns why not. Refusals say how to fix the entry rather than
    /// just that it is wrong - the caller is a model that will try again with whatever it is told.</summary>
    /// <param name="after">For a TASK: the numbers of earlier TASKs on the board that must be merged
    /// before this one can be started.</param>
    /// <param name="lane">The role lane the entry belongs to ("b", "mut", "adv", "lint", "gap",
    /// "next"), or null for the whole team. See <see cref="TeamBoardEntry.Lane"/>.</param>
    public (TeamBoardEntry? Entry, string? Error) Write(
        string author, string? typeName, string? text, string? detail, IReadOnlyList<int>? after = null,
        string? lane = null)
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

        var limit = type is TeamBoardEntryType.PatchSummary or TeamBoardEntryType.Task or TeamBoardEntryType.Finding
            ? MaxPatchSummaryLength
            : MaxTextLength;
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

        if (type == TeamBoardEntryType.Finding
            && (!text.Contains("severity=", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("evidence=", StringComparison.OrdinalIgnoreCase)))
        {
            return (null,
                "A FINDING has the form \"severity=<high|medium|low> kind=<mutation|security|what-if|conformity|gap|"
                + "followup> [fix_by=builders|me|user] evidence=<how you know: a failing test, a command's output, "
                + "branch:<sha>> | <what is wrong and where>\". Severity and evidence are what decide whether the "
                + "runner turns it into a TASK, so both are required.");
        }

        detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        if (detail is { Length: > MaxDetailLength })
        {
            detail = detail[..MaxDetailLength] + " [truncated]";
        }

        TeamBoardEntry? entry = null;
        string? afterError = null;
        lock (_lock)
        {
            IReadOnlyList<int>? prerequisites = null;
            if (after is { Count: > 0 })
            {
                var distinct = after.Distinct().Order().ToList();
                var notTasks = distinct.Where(n => n < 1 || n > _entries.Count || _entries[n - 1].Type != TeamBoardEntryType.Task).ToList();
                if (type != TeamBoardEntryType.Task)
                {
                    afterError = "Only a TASK can wait on other tasks. Leave 'after' out of any other entry.";
                }
                else if (notTasks.Count > 0)
                {
                    afterError = $"{string.Join(", ", notTasks.Select(n => $"G{n}"))} {(notTasks.Count == 1 ? "is" : "are")} not a TASK on the board. "
                                 + "'after' names TASKs already on the board by id (e.g. \"G4\").";
                }
                else
                {
                    prerequisites = distinct;
                }
            }

            if (afterError is null)
            {
                entry = new TeamBoardEntry(
                    _entries.Count + 1, type, author, text, detail, _time.GetUtcNow(), prerequisites, NormalizeLane(lane));
                _entries.Add(entry);
            }
        }

        if (entry is null)
        {
            return (null, afterError);
        }

        AppendToJournal(entry);
        EntryAdded?.Invoke(entry);
        return (entry, null);
    }

    /// <summary>The TASK ids in a tool's 'after' argument - strings such as "G4" or "4", or plain numbers -
    /// as entry numbers. Anything that is not an id is left out.</summary>
    public static IReadOnlyList<int> ParseTaskIds(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonArray items
            ? items
                .Select(item => item?.ToString().Trim().TrimStart('G', 'g'))
                .Select(text => int.TryParse(text, out var number) ? number : 0)
                .Where(number => number > 0)
                .Distinct()
                .ToList()
            : [];

    /// <summary>The TASK id a CLAIM starts with ("G4: login window" is 4), or null. A claim names the
    /// task it takes first; an id further along is only mentioned.</summary>
    public static int? LeadingTaskNumber(string? claimText) =>
        claimText is not null && LeadingId().Match(claimText) is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;

    [GeneratedRegex(@"^\s*G(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingId();

    /// <summary>
    /// Adds entries from another run's board - its journal, read with <see cref="ReadJournal"/> -
    /// keeping their text and detail but not their ids, which this board numbers itself. Authors are
    /// marked as from that run, so a worker can tell a peer's FACT from one inherited. Journalled like
    /// any other entry, so a run carried on from this one inherits them in turn.
    /// </summary>
    /// <remarks>
    /// Ids in their text are renumbered with them: a carried PATCH_SUMMARY that says it finished G3
    /// must not mark whatever is G3 on this board as done. A mention of an entry that was not carried
    /// (a finished TASK, a claim) becomes "earlier-3", which no id here can match.
    /// </remarks>
    public void Import(IEnumerable<TeamBoardEntry> entries, string origin)
    {
        var incoming = entries.ToList();
        List<TeamBoardEntry> added;
        lock (_lock)
        {
            var renumbered = incoming
                .Select((e, i) => (Old: e.Number, New: _entries.Count + 1 + i))
                .ToDictionary(p => p.Old, p => p.New);
            string Renumber(string text) => IdMention().Replace(
                text, m => renumbered.TryGetValue(int.Parse(m.Groups[1].Value), out var n) ? $"G{n}" : $"earlier-{m.Groups[1].Value}");

            // A task that waited on one that was not carried (it was finished) waits on nothing now.
            IReadOnlyList<int>? RenumberAfter(IReadOnlyList<int>? after) =>
                after?.Where(renumbered.ContainsKey).Select(n => renumbered[n]).ToList() is { Count: > 0 } kept ? kept : null;

            added = incoming
                .Select(e => new TeamBoardEntry(
                    renumbered[e.Number], e.Type, $"{e.Author} ({origin})", Renumber(e.Text),
                    e.Detail is null ? null : Renumber(e.Detail), _time.GetUtcNow(), RenumberAfter(e.After),
                    NormalizeLane(e.Lane)))
                .ToList();
            _entries.AddRange(added);
        }

        foreach (var entry in added)
        {
            AppendToJournal(entry);
            EntryAdded?.Invoke(entry);
        }
    }

    [GeneratedRegex(@"\bG(\d+)\b")]
    private static partial Regex IdMention();

    /// <summary>A board's journal (board.jsonl) read back - lines that do not parse are skipped,
    /// since the journal is written best-effort and a half-written last line is possible.</summary>
    public static IReadOnlyList<TeamBoardEntry> ReadJournal(string path)
    {
        var entries = new List<TeamBoardEntry>();
        if (!File.Exists(path))
        {
            return entries;
        }

        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!TryParseType(root.GetProperty("type").GetString(), out var type))
                {
                    continue;
                }

                var number = int.TryParse(root.GetProperty("id").GetString()?.TrimStart('G'), out var n) ? n : entries.Count + 1;
                entries.Add(new TeamBoardEntry(
                    number,
                    type,
                    root.GetProperty("author").GetString() ?? "?",
                    root.GetProperty("text").GetString() ?? "",
                    root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null,
                    root.TryGetProperty("time", out var time) && time.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.MinValue,
                    root.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.Array
                        ? after.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetInt32()).ToList()
                        : null,
                    root.TryGetProperty("lane", out var lane) && lane.ValueKind == JsonValueKind.String
                        ? NormalizeLane(lane.GetString())
                        : null));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
        }

        return entries;
    }

    /// <summary>
    /// What became of a TASK, read off the entries after it: done once a PATCH_SUMMARY names its id,
    /// claimed while an active CLAIM does, open otherwise. Naming the id is the whole protocol - no
    /// tool to learn, and a worker that forgets just leaves the task looking open.
    /// </summary>
    public static TeamTaskStatus StatusOf(TeamBoardEntry task, IReadOnlyList<TeamBoardEntry> entries, IReadOnlyCollection<TeamBoardEntry> activeClaims)
    {
        if (IsDone(task, entries))
        {
            return TeamTaskStatus.Done;
        }

        var mention = new Regex($@"\b{Regex.Escape(task.Id)}\b", RegexOptions.IgnoreCase);
        if (activeClaims.Any(c => c.Number > task.Number && mention.IsMatch(c.Text)))
        {
            return TeamTaskStatus.Claimed;
        }

        return UnmetPrerequisites(task, entries).Count > 0 ? TeamTaskStatus.Blocked : TeamTaskStatus.Open;
    }

    private static bool IsDone(TeamBoardEntry task, IReadOnlyList<TeamBoardEntry> entries)
    {
        var mention = new Regex($@"\b{Regex.Escape(task.Id)}\b", RegexOptions.IgnoreCase);
        return entries.Any(e => e.Number > task.Number && e.Type == TeamBoardEntryType.PatchSummary && mention.IsMatch(e.Text));
    }

    /// <summary>The TASKs <paramref name="task"/> waits for that are not merged yet - empty when it can be
    /// started. A prerequisite counts as met the way any task is done: a PATCH_SUMMARY names it.</summary>
    public static IReadOnlyList<TeamBoardEntry> UnmetPrerequisites(TeamBoardEntry task, IReadOnlyList<TeamBoardEntry> entries) =>
        task.After is not { Count: > 0 }
            ? []
            : task.After
                .Select(number => entries.FirstOrDefault(e => e.Number == number && e.Type == TeamBoardEntryType.Task))
                .OfType<TeamBoardEntry>()
                .Where(prerequisite => !IsDone(prerequisite, entries))
                .ToList();

    /// <summary>The open (and claimed) TASKs, for carrying a backlog into a later run.</summary>
    public static IReadOnlyList<TeamBoardEntry> UnfinishedTasks(IReadOnlyList<TeamBoardEntry> entries) =>
        entries
            .Where(e => e.Type == TeamBoardEntryType.Task && StatusOf(e, entries, []) != TeamTaskStatus.Done)
            .ToList();

    /// <summary>The FINDINGs on the board, oldest first, with the severity parsed out. A finding the
    /// text cannot be parsed out of is left out - <see cref="Write"/> does not accept one.</summary>
    public static IReadOnlyList<(TeamBoardEntry Entry, TeamFinding Finding)> Findings(IReadOnlyList<TeamBoardEntry> entries) =>
        entries
            .Where(e => e.Type == TeamBoardEntryType.Finding)
            .Select(e => (Entry: e, Finding: TeamFinding.TryParse(e.Text)))
            .Where(pair => pair.Finding is not null)
            .Select(pair => (pair.Entry, pair.Finding!))
            .ToList();

    /// <summary>
    /// Whether a FINDING has been answered: a later PATCH_SUMMARY names its id - the same rule that
    /// marks a TASK done - or a later OBSERVED says it was dismissed. A dismissed finding is the
    /// lead's call to record and drop; it must stop holding up wrap-up, or one dismissal the lead
    /// already made would keep the team working past the plan.
    /// </summary>
    public static bool IsAnswered(TeamBoardEntry finding, IReadOnlyList<TeamBoardEntry> entries)
    {
        var mention = new Regex($@"\b{Regex.Escape(finding.Id)}\b", RegexOptions.IgnoreCase);
        return entries.Any(e => e.Number > finding.Number
                                && mention.IsMatch(e.Text)
                                && (e.Type == TeamBoardEntryType.PatchSummary
                                    || (e.Type == TeamBoardEntryType.Observed
                                        && e.Text.Contains("dismiss", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>The findings nobody has answered yet, oldest first - what the lead triages and what
    /// the runner turns into builder TASKs.</summary>
    public static IReadOnlyList<(TeamBoardEntry Entry, TeamFinding Finding)> OpenFindings(
        IReadOnlyList<TeamBoardEntry> entries) =>
        Findings(entries).Where(f => !IsAnswered(f.Entry, entries)).ToList();

    /// <summary>The high-severity findings nobody has answered yet - what holds up wrap-up, and
    /// what the runner turns into a builder TASK.</summary>
    public static IReadOnlyList<(TeamBoardEntry Entry, TeamFinding Finding)> OpenHighFindings(
        IReadOnlyList<TeamBoardEntry> entries) =>
        OpenFindings(entries).Where(f => f.Finding.Severity == TeamFindingSeverity.High).ToList();

    /// <summary>
    /// The board as a cycle's opening carries it - only what this worker has not seen, not the
    /// whole board every time. Every cycle of every worker pays for this text, and the full board
    /// re-sent each time was most of a cycle's opening. Active claims and the backlog always come;
    /// then, on the first cycle, every FAIL and PATCH_SUMMARY so far (what not to retry, what is
    /// already done) plus the latest few entries, and on later cycles just what is new since.
    /// </summary>
    /// <param name="lane">The viewer's role lane. Entries and TASKs in it - and untagged ones, which
    /// concern the whole team - are listed; other lanes' are only counted. Null shows everything,
    /// which is what a worker without a lane, or the lead, wants.</param>
    public string CycleDigest(string viewer, string? lane = null)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var claims = ActiveClaimsLocked(now);
            var cursor = _digestCursors.GetValueOrDefault(viewer);
            _digestCursors[viewer] = _entries.Count;

            if (_entries.Count == 0)
            {
                return "The board is empty - nobody has posted anything yet.";
            }

            var report = new StringBuilder();
            report.Append(claims.Count == 0 ? "Active claims: none.\n" : "Active claims:\n");
            foreach (var claim in claims)
            {
                report.Append("  ").Append(Format(claim)).Append('\n');
            }

            var tasks = _entries.Where(e => e.Type == TeamBoardEntryType.Task).ToList();
            if (tasks.Count > 0)
            {
                var statuses = tasks.Select(t => (Task: t, Status: StatusOf(t, _entries, claims))).ToList();
                var done = statuses.Count(s => s.Status == TeamTaskStatus.Done);
                var waiting = statuses.Count(s => s.Status == TeamTaskStatus.Blocked);
                if (statuses.Count == done)
                {
                    // Said in every cycle's opening, not only broadcast once: a worker whose cycle began after
                    // the runner's notice never saw it.
                    report.Append($"\nBacklog: 0 open or claimed, {done} done. {TeamPrompts.WrapUpNotice}\n");
                }
                else
                {
                    report.Append($"\nBacklog: {statuses.Count - done} open or claimed, {done} done"
                                  + (waiting > 0 ? $" ({waiting} waiting for others)" : "")
                                  + ". Claim one by naming its id in a CLAIM; name it in your PATCH_SUMMARY when merged."
                                  + (waiting > 0 ? " A [blocked] task cannot be claimed until what it waits for is merged." : "")
                                  + '\n');
                }

                // Open first, so the first line is something a worker can take; blocked last.
                foreach (var (task, status) in statuses
                             .Where(s => s.Status != TeamTaskStatus.Done && InLane(s.Task.Lane, lane))
                             .OrderBy(s => s.Status switch { TeamTaskStatus.Open => 0, TeamTaskStatus.Claimed => 1, _ => 2 })
                             .Take(DigestTasks))
                {
                    report.Append("  ").Append(Format(task)).Append(status switch
                    {
                        TeamTaskStatus.Claimed => "  [claimed]",
                        TeamTaskStatus.Blocked =>
                            $"  [blocked: waits for {string.Join(", ", UnmetPrerequisites(task, _entries).Select(p => p.Id))}]",
                        _ => "",
                    }).Append('\n');
                }

                // Another lane's TASKs are that lane's work: counted so the reader knows the backlog is
                // not empty, but not listed, which would invite a builder into a specialist's queue.
                var elsewhere = statuses
                    .Where(s => s.Status != TeamTaskStatus.Done && !InLane(s.Task.Lane, lane))
                    .GroupBy(s => s.Task.Lane!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => $"{g.Key} {g.Count()}")
                    .ToList();
                if (elsewhere.Count > 0)
                {
                    report.Append($"  {string.Join(", ", elsewhere)} other-lane TASK(s) - those belong to their lane's role, ")
                        .Append("not yours (board_grep \"lane:<name>\" to read them).\n");
                }
            }

            var others = _entries.Where(e => e.Type is not (TeamBoardEntryType.Claim or TeamBoardEntryType.Task)).ToList();
            var foreign = others.Where(e => !InLane(e.Lane, lane)).ToList();
            others = others.Where(e => InLane(e.Lane, lane)).ToList();
            if (cursor == 0)
            {
                var lessons = others.Where(e => e.Type is TeamBoardEntryType.Fail or TeamBoardEntryType.PatchSummary).ToList();
                var latest = others.Except(lessons).TakeLast(DigestFirstCycleEntries).ToList();
                if (lessons.Count > 0)
                {
                    report.Append("\nFailed approaches and merged work so far:\n");
                    foreach (var entry in lessons.TakeLast(DigestNewEntries))
                    {
                        report.Append("  ").Append(Format(entry)).Append('\n');
                    }
                }

                if (latest.Count > 0)
                {
                    report.Append("\nLatest other entries:\n");
                    foreach (var entry in latest)
                    {
                        report.Append("  ").Append(Format(entry)).Append('\n');
                    }
                }
            }
            else
            {
                var fresh = others.Where(e => e.Number > cursor && e.Author != viewer).ToList();
                report.Append(fresh.Count == 0 ? "\nNothing new since your last cycle.\n" : $"\nNew since your last cycle ({fresh.Count}):\n");
                foreach (var entry in fresh.TakeLast(DigestNewEntries))
                {
                    report.Append("  ").Append(Format(entry)).Append('\n');
                }

                if (fresh.Count > DigestNewEntries)
                {
                    report.Append($"  ({fresh.Count - DigestNewEntries} older new entries not shown)\n");
                }
            }

            if (foreign.Count > 0)
            {
                var byLane = foreign
                    .GroupBy(e => e.Lane!, StringComparer.OrdinalIgnoreCase)
                    .Select(g => $"{g.Key} {g.Count()}")
                    .ToList();
                report.Append($"\nOther lanes ({string.Join(", ", byLane)} entries - counted only, they are their ")
                    .Append("lane's business): board_grep \"lane:<name>\" to read them.\n");
            }
            report.Append($"\n({_entries.Count} entries in all - board_read for the latest, board_grep to search everything.)");
            return report.ToString();
        }
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
                    var haystack = $"{e.TypeName} {e.Author} {e.Text} {e.Detail} lane:{e.Lane}";
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
        $"{entry.Id} [{entry.TypeName}]"
        + (entry.Lane is null ? "" : $" [lane:{entry.Lane}]")
        + $" {entry.Author}: {entry.Text}"
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
            after = entry.After,
            lane = entry.Lane,
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
