using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// Recognises the same finding across loops. A judge does not remember last round's verdict and will
/// word a finding differently the second time, so identity is two things together: the same judge
/// about the same primary file in the same category (the key), and a title that shares at least half
/// its words with the earlier one.
/// </summary>
public static partial class FindingFingerprint
{
    /// <summary>The share of words (Jaccard) two titles must have in common to be the same finding.</summary>
    public const double MatchThreshold = 0.5;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "an", "is", "in", "of", "to", "and", "or", "for", "on", "not", "no", "with", "this", "that",
        "are", "be", "it", "as", "at", "by", "can", "may", "does", "do", "when", "if", "from",
    };

    /// <summary>A short stable key for (judge, primary file, category): twelve hex characters.</summary>
    public static string Key(string judge, string? primaryFile, string category)
    {
        var file = PipelineArtifactStore.NormalizeRelativePath(primaryFile)?.ToLowerInvariant() ?? "";
        var raw = $"{judge.Trim().ToLowerInvariant()}|{file}|{category.Trim().ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..12].ToLowerInvariant();
    }

    /// <summary>How alike two titles are, 0 to 1: the share of their words (case aside, short and
    /// filler words left out) that they have in common. Identical text is 1 even if it is all filler.</summary>
    public static double TitleSimilarity(string a, string b)
    {
        if (string.Equals(PipelineText.Clean(a), PipelineText.Clean(b), StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var left = Words(a);
        var right = Words(b);
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var shared = left.Intersect(right).Count();
        return (double)shared / left.Union(right).Count();
    }

    public static bool SameFinding(string judge, JudgeFinding a, JudgeFinding b) =>
        Key(judge, a.Files.FirstOrDefault(), a.Category) == Key(judge, b.Files.FirstOrDefault(), b.Category)
        && TitleSimilarity(a.Title, b.Title) >= MatchThreshold;

    private static HashSet<string> Words(string text) =>
        [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 2 && !StopWords.Contains(w)).Select(Stem)];

    /// <summary>
    /// Folds the endings a judge varies between rounds - "ignores", "ignored", "ignoring", "ignore" -
    /// to one stem, so rewording a title does not make a known finding look new. Deliberately crude: it
    /// only has to make two phrasings of one problem agree, not be right about English.
    /// </summary>
    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            word = word[..^3];
        }
        else if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            word = word[..^2];
        }
        else if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal))
        {
            word = word[..^2];
        }
        else if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            word = word[..^1];
        }

        return word.Length > 3 && word.EndsWith('e') ? word[..^1] : word;
    }

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex WordPattern();
}

/// <summary>A finding as the run tracks it across loops.</summary>
public sealed record LedgerFinding(
    string Id,
    string Fingerprint,
    string Judge,
    JudgeFinding Finding,
    int FirstSeenLoop,
    int LastSeenLoop,
    int SeenCount,
    FindingStatus Status,
    string? Note = null)
{
    public bool IsBlocking(TeamFindingSeverity threshold) =>
        Status is FindingStatus.Open or FindingStatus.Escalated or FindingStatus.Disputed
        && Finding.Severity >= threshold;
}

/// <summary>
/// Every finding of the run, across loops. A judge's round is recorded with <see cref="RecordRound"/>;
/// a finding it reports again is the same finding with a higher seen count, and one reported a second
/// time - after a repair was tried - is escalated rather than handed to the same owner a third time.
/// <see cref="CompleteRound"/> then marks what a judge did not report again as fixed: it looked, and it
/// is gone. Thread-safe, since results arrive from agents running in parallel.
/// </summary>
public sealed class FindingLedger
{
    /// <summary>Reported in this many different loops, an open finding is escalated.</summary>
    public const int EscalateAfterSeen = 2;

    private readonly object _gate = new();
    private readonly List<LedgerFinding> _findings = [];
    private readonly Dictionary<int, int> _numberInLoop = [];

    public FindingLedger()
    {
    }

    /// <summary>A ledger carrying on from findings already recorded (read back from findings.json).</summary>
    public FindingLedger(IEnumerable<LedgerFinding> existing)
    {
        _findings.AddRange(existing);
        foreach (var finding in _findings)
        {
            var dot = finding.Id.IndexOf('.');
            if (dot > 1 && int.TryParse(finding.Id[1..dot], out var loop) && int.TryParse(finding.Id[(dot + 1)..], out var number))
            {
                _numberInLoop[loop] = Math.Max(_numberInLoop.GetValueOrDefault(loop), number);
            }
        }
    }

    public IReadOnlyList<LedgerFinding> Findings
    {
        get
        {
            lock (_gate)
            {
                return [.. _findings];
            }
        }
    }

    public LedgerFinding? Find(string id)
    {
        lock (_gate)
        {
            return _findings.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The findings that keep the loop from passing at <paramref name="threshold"/>.</summary>
    public IReadOnlyList<LedgerFinding> Blocking(TeamFindingSeverity threshold)
    {
        lock (_gate)
        {
            return [.. _findings.Where(f => f.IsBlocking(threshold))];
        }
    }

    /// <summary>The blocking findings an owner can still be asked to repair: open, not yet escalated or
    /// disputed.</summary>
    public IReadOnlyList<LedgerFinding> Repairable(TeamFindingSeverity threshold)
    {
        lock (_gate)
        {
            return [.. _findings.Where(f => f.Status == FindingStatus.Open && f.Finding.Severity >= threshold)];
        }
    }

    /// <summary>
    /// Adds what <paramref name="judge"/> reported in <paramref name="loop"/>. Returns the ledger entries
    /// those findings became or updated, in order.
    /// </summary>
    public IReadOnlyList<LedgerFinding> RecordRound(int loop, string judge, IEnumerable<ValidatedFinding> findings)
    {
        var touched = new List<LedgerFinding>();
        lock (_gate)
        {
            foreach (var reported in findings)
            {
                var index = IndexOfMatch(judge, reported.Finding);
                if (index >= 0)
                {
                    var existing = _findings[index];
                    if (existing.LastSeenLoop == loop)
                    {
                        touched.Add(existing); // The same judge said it twice in one round.
                        continue;
                    }

                    if (reported.Status != FindingStatus.Open)
                    {
                        // Reported again but its evidence no longer checks out (the code it quotes
                        // changed): that is not the finding coming back, so it is left for CompleteRound.
                        continue;
                    }

                    _findings[index] = Reappeared(existing, reported.Finding, loop);
                    touched.Add(_findings[index]);
                    continue;
                }

                var number = _numberInLoop[loop] = _numberInLoop.GetValueOrDefault(loop) + 1;
                var entry = new LedgerFinding(
                    $"F{loop}.{number}",
                    FindingFingerprint.Key(judge, reported.Finding.Files.FirstOrDefault(), reported.Finding.Category),
                    judge,
                    reported.Finding,
                    loop,
                    loop,
                    1,
                    reported.Status,
                    reported.Note);
                _findings.Add(entry);
                touched.Add(entry);
            }
        }

        return touched;
    }

    /// <summary>
    /// Ends <paramref name="loop"/>'s judging: every blocking finding of a judge that reported this loop,
    /// and did not report that one again, is marked fixed. A judge that gave no verdict (it was
    /// unavailable) leaves its findings as they were.
    /// </summary>
    public IReadOnlyList<LedgerFinding> CompleteRound(int loop, IEnumerable<string> judgesThatReported)
    {
        var reported = judgesThatReported.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fixedNow = new List<LedgerFinding>();
        lock (_gate)
        {
            for (var i = 0; i < _findings.Count; i++)
            {
                var finding = _findings[i];
                if (finding.LastSeenLoop < loop
                    && reported.Contains(finding.Judge)
                    && finding.Status is FindingStatus.Open or FindingStatus.Escalated or FindingStatus.Disputed)
                {
                    _findings[i] = finding with { Status = FindingStatus.Fixed, Note = $"not reported again in loop {loop}" };
                    fixedNow.Add(_findings[i]);
                }
            }
        }

        return fixedNow;
    }

    /// <summary>Records an outcome decided outside judging: an owner's rejection (disputed), an
    /// arbitration (dismissed), a finding handed to the user.</summary>
    public bool SetStatus(string id, FindingStatus status, string? note = null)
    {
        lock (_gate)
        {
            var index = _findings.FindIndex(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            _findings[index] = _findings[index] with { Status = status, Note = note ?? _findings[index].Note };
            return true;
        }
    }

    private int IndexOfMatch(string judge, JudgeFinding finding)
    {
        var key = FindingFingerprint.Key(judge, finding.Files.FirstOrDefault(), finding.Category);
        var best = -1;
        var bestSimilarity = 0.0;
        for (var i = 0; i < _findings.Count; i++)
        {
            var candidate = _findings[i];
            if (!string.Equals(candidate.Judge, judge, StringComparison.OrdinalIgnoreCase) || candidate.Fingerprint != key)
            {
                continue;
            }

            var similarity = FindingFingerprint.TitleSimilarity(candidate.Finding.Title, finding.Title);
            if (similarity >= FindingFingerprint.MatchThreshold && similarity > bestSimilarity)
            {
                best = i;
                bestSimilarity = similarity;
            }
        }

        return best;
    }

    private static LedgerFinding Reappeared(LedgerFinding existing, JudgeFinding latest, int loop)
    {
        var seen = existing.SeenCount + 1;
        var status = existing.Status switch
        {
            // Back after being fixed: a regression, and a repair has already been tried once for it.
            FindingStatus.Fixed => seen >= EscalateAfterSeen ? FindingStatus.Escalated : FindingStatus.Open,
            FindingStatus.Open => seen >= EscalateAfterSeen ? FindingStatus.Escalated : FindingStatus.Open,
            FindingStatus.Unverified => FindingStatus.Open,
            var other => other, // Escalated, Disputed and Dismissed stay as they are.
        };
        return existing with
        {
            Finding = latest,
            LastSeenLoop = loop,
            SeenCount = seen,
            Status = status,
            Note = status == FindingStatus.Escalated && existing.Status != FindingStatus.Escalated
                ? $"reported again after a repair (seen in {seen} loops)"
                : existing.Note,
        };
    }
}

/// <summary>
/// Watches whether the loop is getting anywhere. The score is 3 for each open high finding and 1 for
/// each open medium one; if it does not fall from one judge round to the next, that is a stall, and
/// two in a row end the run (<see cref="NoProgress"/>) instead of repairing until the cap.
/// </summary>
public sealed class ConvergenceTracker(int stallLimit = 2)
{
    private readonly List<int> _history = [];

    public IReadOnlyList<int> History => _history;

    /// <summary>How many judge rounds in a row have failed to lower the score.</summary>
    public int Stalled { get; private set; }

    public bool NoProgress => Stalled >= Math.Max(1, stallLimit);

    public static int Score(IEnumerable<LedgerFinding> blocking) =>
        blocking.Sum(f => f.Finding.Severity switch
        {
            TeamFindingSeverity.High => 3,
            TeamFindingSeverity.Medium => 1,
            _ => 0,
        });

    /// <summary>Records a judge round's score and returns how many rounds in a row have stalled. The
    /// first round has nothing to compare with; after that, a score that is not lower than the last
    /// one is a stall and a lower one clears the count.</summary>
    public int Observe(int score)
    {
        if (_history.Count > 0)
        {
            Stalled = score >= _history[^1] ? Stalled + 1 : 0;
        }

        _history.Add(score);
        return Stalled;
    }
}
