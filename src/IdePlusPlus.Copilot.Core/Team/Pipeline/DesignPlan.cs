using System.Text;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>One file of the design: where it is, who owns it, what it is for, and how an owner and a
/// judge will know it is right. <paramref name="IsStub"/> marks the files the designer commits first as
/// compile-able interface stubs, so every owner builds against the same contract.</summary>
public sealed record DesignFile(string Path, string Owner, string Purpose, IReadOnlyList<string> Acceptance, bool IsStub = false);

/// <summary>A thing a careless or hostile user could try ("an empty list", "two saves at once"). The
/// security judge answers each by id.</summary>
public sealed record MisuseCase(string Id, string Scenario);

/// <summary>A survey's trim candidate that the designer judged necessary for the goal. Everything the
/// designer leaves out is only a suggested next prompt.</summary>
public sealed record DesignTrim(string What, string File, string Reason);

/// <summary>
/// The designer's plan: the files, one owner for each, the interface stubs, acceptance criteria, the
/// misuse cases the judges check, and the trims that are part of the work. The owners are named b1..bK;
/// a file's owner is the only agent that may edit it, and the way to change someone else's file is to
/// ask its owner.
/// </summary>
public sealed record DesignPlan(
    string Summary,
    IReadOnlyList<string> Owners,
    IReadOnlyList<DesignFile> Files,
    IReadOnlyList<MisuseCase> MisuseCases,
    IReadOnlyList<DesignTrim> Trims,
    int Waves = 1)
{
    /// <summary>The files an owner is responsible for, in the order the design lists them.</summary>
    public IReadOnlyList<DesignFile> FilesOf(string owner) =>
        [.. Files.Where(f => string.Equals(f.Owner, owner, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The plan as an agent's prompt shows it (to be fenced by the caller).</summary>
    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine($"Design: {Summary}");
        text.AppendLine($"Owners: {string.Join(", ", Owners)}  (waves: {Waves})");
        foreach (var owner in Owners)
        {
            text.AppendLine($"{owner} owns:");
            foreach (var file in FilesOf(owner))
            {
                text.AppendLine($"- {file.Path}{(file.IsStub ? " [interface stub, committed first]" : "")}: {file.Purpose}");
                foreach (var criterion in file.Acceptance)
                {
                    text.AppendLine($"    - accept: {criterion}");
                }
            }
        }

        if (MisuseCases.Count > 0)
        {
            text.AppendLine("Misuse cases:");
            foreach (var misuse in MisuseCases)
            {
                text.AppendLine($"- {misuse.Id}: {misuse.Scenario}");
            }
        }

        if (Trims.Count > 0)
        {
            text.AppendLine("Trims that are part of the work:");
            foreach (var trim in Trims)
            {
                text.AppendLine($"- {trim.What} ({trim.File}) - {trim.Reason}");
            }
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>What a run allows a design: how many owners and files, and how many misuse cases.</summary>
public sealed record DesignLimits(int MaxOwners = 6, int MaxFiles = 60, int MaxMisuseCases = 20);

public static partial class DesignValidator
{
    public const int MaxSummaryLength = 800;
    public const int MaxTextLength = 300;
    public const int MaxAcceptancePerFile = 8;
    public const int MaxWaves = 3;

    /// <summary>
    /// Checks and tidies a design: paths normalised and unique, every file owned by exactly one of
    /// b1..bK, every owner owning something, every file with a purpose and a way to check it. The
    /// refusals name the file and the owners involved - "x.cs is owned by both b1 and b2" is a sentence
    /// the designer can act on.
    /// </summary>
    public static PipelineCheck<DesignPlan> Validate(DesignPlan? plan, DesignLimits limits)
    {
        if (plan is null)
        {
            return PipelineCheck<DesignPlan>.Failed(["The design is empty."]);
        }

        var errors = new ErrorList();
        string Text(string? value, int max = MaxTextLength) =>
            PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(value)), max, out _);

        var summary = Text(plan.Summary, MaxSummaryLength);
        if (summary.Length == 0)
        {
            errors.Add("The design needs a summary: what is being built and how it fits together.");
        }

        // Owners: exactly b1..bK, in order, so the runner can start them by number.
        var owners = (plan.Owners ?? []).Select(o => PipelineText.Clean(o).ToLowerInvariant()).ToList();
        if (owners.Count == 0)
        {
            errors.Add("The design needs at least one owner (b1).");
        }
        else if (owners.Count > limits.MaxOwners)
        {
            errors.Add($"The design has {owners.Count} owners; at most {limits.MaxOwners} are allowed here. "
                       + "Give each owner a coherent cluster of files rather than one file each.");
        }
        else if (!owners.SequenceEqual(Enumerable.Range(1, owners.Count).Select(n => $"b{n}")))
        {
            errors.Add($"Owners must be named b1..b{owners.Count} in order, with no gaps; got {string.Join(", ", owners)}.");
        }

        var ownerSet = owners.ToHashSet(StringComparer.Ordinal);

        // Files.
        var files = new List<DesignFile>();
        var seen = new Dictionary<string, DesignFile>(StringComparer.OrdinalIgnoreCase);
        var rawFiles = plan.Files ?? [];
        if (rawFiles.Count == 0)
        {
            errors.Add("The design lists no files.");
        }
        else if (rawFiles.Count > limits.MaxFiles)
        {
            errors.Add($"The design lists {rawFiles.Count} files; at most {limits.MaxFiles} are allowed here.");
        }

        for (var index = 0; index < rawFiles.Count && rawFiles.Count <= limits.MaxFiles; index++)
        {
            var raw = rawFiles[index];
            var at = PipelineText.Item("files", index);
            var path = PipelineArtifactStore.NormalizeRelativePath(raw?.Path);
            if (path is null)
            {
                errors.Add($"{at}: '{PipelineText.Clean(raw?.Path)}' is not a plain relative path inside the solution directory.");
                continue;
            }

            var owner = PipelineText.Clean(raw!.Owner).ToLowerInvariant();
            if (!ownerSet.Contains(owner))
            {
                errors.Add($"{path} is owned by '{owner}', which is not in the owners list ({string.Join(", ", owners)}).");
            }

            var purpose = Text(raw.Purpose);
            if (purpose.Length == 0)
            {
                errors.Add($"{path} needs a purpose: what the file is for.");
            }

            var acceptance = (raw.Acceptance ?? []).Select(a => Text(a)).Where(a => a.Length > 0).Take(MaxAcceptancePerFile).ToList();
            if (acceptance.Count == 0)
            {
                errors.Add($"{path} needs at least one acceptance criterion an owner and a judge can check.");
            }

            if (seen.TryGetValue(path, out var earlier))
            {
                errors.Add(string.Equals(earlier.Owner, owner, StringComparison.OrdinalIgnoreCase)
                    ? $"{path} is listed twice."
                    : $"{path} is owned by both {earlier.Owner} and {owner}: every file has exactly one owner.");
                continue;
            }

            var file = new DesignFile(path, owner, purpose, acceptance, raw.IsStub);
            seen[path] = file;
            files.Add(file);
        }

        foreach (var owner in owners.Where(o => ownerSet.Contains(o)).Distinct())
        {
            if (files.Count > 0 && !files.Any(f => f.Owner == owner))
            {
                errors.Add($"{owner} owns no files. Give it some, or drop it from the owners list.");
            }
        }

        // Misuse cases.
        var misuseCases = new List<MisuseCase>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rawMisuse = plan.MisuseCases ?? [];
        if (rawMisuse.Count > limits.MaxMisuseCases)
        {
            errors.Add($"The design lists {rawMisuse.Count} misuse cases; at most {limits.MaxMisuseCases} are allowed here.");
        }

        foreach (var (raw, index) in rawMisuse.Take(limits.MaxMisuseCases).Select((raw, index) => (raw, index)))
        {
            var at = PipelineText.Item("misuseCases", index);
            var id = PipelineText.Clean(raw?.Id).ToUpperInvariant();
            if (!MisuseId().IsMatch(id))
            {
                errors.Add($"{at}: id '{id}' must look like M1, M2, ...");
                continue;
            }

            if (!ids.Add(id))
            {
                errors.Add($"Misuse case {id} appears twice.");
                continue;
            }

            var scenario = Text(raw!.Scenario);
            if (scenario.Length == 0)
            {
                errors.Add($"Misuse case {id} needs a scenario: what the user tries.");
                continue;
            }

            misuseCases.Add(new MisuseCase(id, scenario));
        }

        // Trims.
        var trims = new List<DesignTrim>();
        foreach (var (raw, index) in (plan.Trims ?? []).Select((raw, index) => (raw, index)))
        {
            var at = PipelineText.Item("trims", index);
            var what = Text(raw?.What);
            var reason = Text(raw?.Reason);
            var file = PipelineArtifactStore.NormalizeRelativePath(raw?.File);
            if (what.Length == 0 || reason.Length == 0 || file is null)
            {
                errors.Add($"{at} needs what is trimmed, a plain relative file path, and why the goal needs it.");
                continue;
            }

            trims.Add(new DesignTrim(what, file, reason));
        }

        if (plan.Waves < 1 || plan.Waves > MaxWaves)
        {
            errors.Add($"waves must be between 1 and {MaxWaves}; got {plan.Waves}.");
        }

        return errors.ResultOr(() => new DesignPlan(summary, owners, files, misuseCases, trims, plan.Waves));
    }

    [GeneratedRegex(@"^M[1-9][0-9]{0,2}\z")]
    private static partial Regex MisuseId();
}
