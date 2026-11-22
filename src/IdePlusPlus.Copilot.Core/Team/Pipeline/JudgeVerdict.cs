using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

public enum JudgeVerdictKind
{
    Pass,
    Fail,
}

/// <summary>Who can fix a finding: the owner of the file, the design (a contract is wrong), or the user
/// (it is out of scope here - it goes to the suggested next prompts).</summary>
public enum FindingFixBy
{
    Owner,
    Design,
    User,
}

/// <summary>Where a finding stands across the loop. Open, Escalated and Disputed block a PASS;
/// Unverified (its evidence did not check out), Fixed and Dismissed do not.</summary>
public enum FindingStatus
{
    Open,
    Fixed,
    Disputed,
    Escalated,
    Unverified,
    Dismissed,
}

/// <summary>Reads "high", "medium", "low" - and the synonyms the board already accepts ("critical",
/// "blocker", "moderate") - rather than refusing a verdict over a word.</summary>
public sealed class LenientSeverityConverter : JsonConverter<TeamFindingSeverity>
{
    public override TeamFindingSeverity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => TeamFinding.SeverityOf(reader.GetString()),
            JsonTokenType.Number when reader.TryGetInt32(out var number) =>
                (TeamFindingSeverity)Math.Clamp(number, (int)TeamFindingSeverity.Low, (int)TeamFindingSeverity.High),
            _ => throw new JsonException("A severity is one of: low, medium, high."),
        };

    public override void Write(Utf8JsonWriter writer, TeamFindingSeverity value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

/// <summary>Reads owner/design/user, and the older board's builders/me.</summary>
public sealed class LenientFixByConverter : JsonConverter<FindingFixBy>
{
    public override FindingFixBy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? (reader.GetString() ?? "").Trim().ToLowerInvariant() switch
            {
                "" or "owner" or "owners" or "builder" or "builders" => FindingFixBy.Owner,
                "design" or "designer" or "me" or "orchestrator" => FindingFixBy.Design,
                "user" => FindingFixBy.User,
                var other => throw new JsonException($"fixBy '{other}' is not one of: owner, design, user."),
            }
            : throw new JsonException("fixBy is one of: owner, design, user.");

    public override void Write(Utf8JsonWriter writer, FindingFixBy value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

/// <summary>One thing a judge found. <paramref name="Quote"/> is a line or two of the code, copied
/// exactly: it is how a finding about the code proves it is about the code.</summary>
public sealed record JudgeFinding(
    string Title,
    [property: JsonConverter(typeof(LenientSeverityConverter))] TeamFindingSeverity Severity,
    string Category,
    IReadOnlyList<string> Files,
    string? Evidence = null,
    string? Quote = null,
    string? Fix = null,
    [property: JsonConverter(typeof(LenientFixByConverter))] FindingFixBy FixBy = FindingFixBy.Owner);

/// <summary>The security judge's answer for one of the design's misuse cases: "covered", "partial" or
/// "uncovered", with the evidence.</summary>
public sealed record CoverageEntry(string Id, string Result, string? Evidence = null);

public sealed record JudgeVerdict(
    JudgeVerdictKind Verdict,
    string Summary,
    IReadOnlyList<JudgeFinding>? Findings = null,
    IReadOnlyList<CoverageEntry>? Coverage = null);

/// <summary>A finding that passed the validator, and whether its evidence checked out.</summary>
public sealed record ValidatedFinding(JudgeFinding Finding, FindingStatus Status, string? Note = null);

public sealed record ValidatedVerdict(
    JudgeVerdictKind Verdict,
    string Summary,
    IReadOnlyList<ValidatedFinding> Findings,
    IReadOnlyList<CoverageEntry> Coverage)
{
    /// <summary>True when a finding that checked out is at or above <paramref name="threshold"/> - what
    /// keeps the loop from passing. A verdict that says FAIL only on findings that could not be verified
    /// does not block.</summary>
    public bool BlocksPass(TeamFindingSeverity threshold) =>
        Findings.Any(f => f.Status == FindingStatus.Open && f.Finding.Severity >= threshold);
}

/// <summary>
/// What a verdict is checked against. <paramref name="ReadFileAtHead"/> gives a repository-relative file's
/// text as it is on the team branch now, or null if it is not there - the quote of a finding has to be
/// found in it. <paramref name="MisuseCaseIds"/> is set for the security judge only.
/// </summary>
public sealed record VerdictContext(
    TeamFindingSeverity Threshold,
    Func<string, string?> ReadFileAtHead,
    IReadOnlySet<string>? MisuseCaseIds = null,
    bool RequireBranchEvidenceForHigh = false);

public static class VerdictValidator
{
    public const int MaxFindings = 25;
    public const int MaxTitleLength = 120;
    public const int MaxSummaryLength = 600;
    public const int MaxEvidenceLength = 400;
    public const int MaxQuoteLength = 600;
    public const int MaxFixLength = 600;
    public const int MaxFilesPerFinding = 8;

    /// <summary>A quote shorter than this proves nothing: "x" is in every file.</summary>
    public const int MinQuoteLength = 8;

    /// <summary>
    /// Checks a judge's verdict. The verdict word has to agree with the findings: PASS cannot carry a
    /// finding at or above the threshold and FAIL needs one. Anything at or above medium has to come
    /// with evidence and a quote of the code it is about; if the quote is not in any listed file at the
    /// team head the finding is kept but marked unverified, which keeps a judge's invention from
    /// sending an owner to repair something that is not there. For the security judge every misuse
    /// case must be answered exactly once.
    /// </summary>
    public static PipelineCheck<ValidatedVerdict> Validate(JudgeVerdict? verdict, VerdictContext context)
    {
        if (verdict is null)
        {
            return PipelineCheck<ValidatedVerdict>.Failed(["The verdict is empty."]);
        }

        var errors = new ErrorList();
        var summary = Cut(verdict.Summary, MaxSummaryLength);
        if (summary.Length == 0)
        {
            errors.Add("The verdict needs a summary: one or two sentences on what you checked and what you concluded.");
        }

        var raw = verdict.Findings ?? [];
        if (raw.Count > MaxFindings)
        {
            errors.Add($"{raw.Count} findings is too many; report the {MaxFindings} that matter most.");
        }

        var fileText = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Normalized(string path)
        {
            if (!fileText.TryGetValue(path, out var text))
            {
                text = context.ReadFileAtHead(path) is { } content ? PipelineText.ForComparison(content) : null;
                fileText[path] = text;
            }

            return text;
        }

        var findings = new List<ValidatedFinding>();
        var declaredBlocking = false;
        foreach (var (item, index) in raw.Take(MaxFindings).Select((item, index) => (item, index)))
        {
            var at = PipelineText.Item("findings", index);
            if (item is null)
            {
                errors.Add($"{at} is empty.");
                continue;
            }

            var title = Cut(item.Title, MaxTitleLength);
            var category = Category(item.Category);
            var ok = true;
            if (title.Length == 0)
            {
                errors.Add($"{at} needs a title.");
                ok = false;
            }

            if (category.Length == 0)
            {
                errors.Add($"{at} needs a category (security, correctness, maintainability, gap, ...).");
                ok = false;
            }

            var files = new List<string>();
            foreach (var file in (item.Files ?? []).Take(MaxFilesPerFinding))
            {
                if (PipelineArtifactStore.NormalizeRelativePath(file) is { } normalizedPath)
                {
                    files.Add(normalizedPath);
                }
                else
                {
                    errors.Add($"{at}: '{PipelineText.Clean(file)}' is not a plain relative file path.");
                    ok = false;
                }
            }

            var severity = item.Severity;
            var needsProof = severity >= TeamFindingSeverity.Medium;
            if (severity >= context.Threshold)
            {
                declaredBlocking = true;
            }

            var evidence = Cut(item.Evidence, MaxEvidenceLength);
            var quote = CutQuote(item.Quote);
            if (needsProof)
            {
                if (files.Count == 0 && ok)
                {
                    errors.Add($"{at} ({severity.ToString().ToLowerInvariant()}) needs the file or files it is about.");
                    ok = false;
                }

                if (evidence.Length == 0)
                {
                    errors.Add($"{at} ({severity.ToString().ToLowerInvariant()}) needs evidence: how you know, or a failing test.");
                    ok = false;
                }

                if (quote.Length < MinQuoteLength)
                {
                    errors.Add($"{at} ({severity.ToString().ToLowerInvariant()}) needs a quote of the code it is about, copied "
                               + $"exactly (at least {MinQuoteLength} characters).");
                    ok = false;
                }

                if (severity == TeamFindingSeverity.High && context.RequireBranchEvidenceForHigh
                    && !evidence.Contains("branch:", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{at} is high, which for you needs a failing test committed on your own branch, quoted as "
                               + "evidence=branch:<sha>. Without one, report it as medium.");
                    ok = false;
                }
            }

            if (!ok)
            {
                continue;
            }

            var status = FindingStatus.Open;
            string? note = null;
            if (needsProof)
            {
                (status, note) = Verify(quote, files, Normalized);
            }

            findings.Add(new ValidatedFinding(
                new JudgeFinding(
                    title,
                    severity,
                    category,
                    files,
                    evidence.Length == 0 ? null : evidence,
                    quote.Length == 0 ? null : quote,
                    NullIfEmpty(Cut(item.Fix, MaxFixLength)),
                    item.FixBy),
                status,
                note));
        }

        if (verdict.Verdict == JudgeVerdictKind.Pass && declaredBlocking)
        {
            errors.Add($"A PASS cannot carry a finding at {context.Threshold.ToString().ToLowerInvariant()} or above. "
                       + "Either remove it, or make the verdict FAIL.");
        }
        else if (verdict.Verdict == JudgeVerdictKind.Fail && !declaredBlocking)
        {
            errors.Add($"A FAIL needs at least one finding at {context.Threshold.ToString().ToLowerInvariant()} or above that "
                       + "says what failed. Lower findings go with a PASS.");
        }

        var coverage = ValidateCoverage(verdict.Coverage, context.MisuseCaseIds, errors);
        return errors.ResultOr(() => new ValidatedVerdict(verdict.Verdict, summary, findings, coverage));
    }

    /// <summary>Whether the quote appears (whitespace aside) in at least one of the finding's files.</summary>
    private static (FindingStatus Status, string? Note) Verify(string quote, List<string> files, Func<string, string?> normalized)
    {
        var wanted = PipelineText.ForComparison(quote);
        var missing = new List<string>();
        foreach (var file in files)
        {
            if (normalized(file) is not { } text)
            {
                missing.Add(file);
            }
            else if (text.Contains(wanted, StringComparison.Ordinal))
            {
                return (FindingStatus.Open, null);
            }
        }

        return missing.Count == files.Count
            ? (FindingStatus.Unverified, $"not checked: {(files.Count == 1 ? "the file is" : "none of the files are")} at the current head ({string.Join(", ", missing)})")
            : (FindingStatus.Unverified, "not checked: the quoted code was not found in " + string.Join(", ", files.Except(missing)));
    }

    private static IReadOnlyList<CoverageEntry> ValidateCoverage(
        IReadOnlyList<CoverageEntry>? entries, IReadOnlySet<string>? expectedIds, ErrorList errors)
    {
        if (expectedIds is null || expectedIds.Count == 0)
        {
            return [];
        }

        var seen = new Dictionary<string, CoverageEntry>(StringComparer.Ordinal);
        foreach (var (entry, index) in (entries ?? []).Select((entry, index) => (entry, index)))
        {
            var at = PipelineText.Item("coverage", index);
            var id = PipelineText.Clean(entry?.Id).ToUpperInvariant();
            if (!expectedIds.Contains(id))
            {
                errors.Add($"{at}: '{id}' is not one of the design's misuse cases ({string.Join(", ", expectedIds.Order())}).");
                continue;
            }

            if (seen.ContainsKey(id))
            {
                errors.Add($"Misuse case {id} is answered twice.");
                continue;
            }

            if (ParseCoverageResult(entry!.Result) is not { } result)
            {
                errors.Add($"{at}: result for {id} must be covered, partial or uncovered.");
                continue;
            }

            var evidence = Cut(entry.Evidence, MaxEvidenceLength);
            if (evidence.Length == 0)
            {
                errors.Add($"{at}: {id} needs evidence for the answer - where the code handles it, or fails to.");
                continue;
            }

            seen[id] = new CoverageEntry(id, result, evidence);
        }

        foreach (var missing in expectedIds.Order().Where(id => !seen.ContainsKey(id)))
        {
            errors.Add($"Misuse case {missing} has no answer: say whether it is covered, partial or uncovered.");
        }

        return [.. expectedIds.Order().Where(seen.ContainsKey).Select(id => seen[id])];
    }

    private static string? ParseCoverageResult(string? result) =>
        PipelineText.Clean(result).ToLowerInvariant().Replace("-", " ").Replace("_", " ") switch
        {
            "covered" or "handled" => "covered",
            "partial" or "partly" or "partly covered" or "partially covered" => "partial",
            "uncovered" or "not covered" or "unhandled" => "uncovered",
            _ => null,
        };

    /// <summary>Lower-case letters, digits, hyphen and underscore only, at most 40 characters.</summary>
    private static string Category(string? category)
    {
        var cleaned = new string(PipelineText.Clean(category).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : c is '-' or '_' ? c : ' ')
            .ToArray());
        return PipelineText.Clean(cleaned).Replace(' ', '-').Trim('-') is { Length: > 0 } slug
            ? slug[..Math.Min(40, slug.Length)]
            : "";
    }

    private static string Cut(string? text, int max) =>
        PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(text)), max, out _);

    /// <summary>A quote cut to its limit stays a plain prefix of what was written - no "[truncated]" mark,
    /// which is not in the file and would make the quote fail the check it exists for.</summary>
    private static string CutQuote(string? text)
    {
        var cleaned = PipelineText.Clean(text);
        return cleaned.Length > MaxQuoteLength ? cleaned[..MaxQuoteLength] : cleaned;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
