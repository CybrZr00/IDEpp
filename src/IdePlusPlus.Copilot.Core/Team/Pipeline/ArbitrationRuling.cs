namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>An owner refused a change, the requester appealed, and this is what the orchestrator
/// submits: who is right and what both are to do about it. <see cref="Ruling"/> is "requester" or
/// "owner", read leniently.</summary>
public sealed record ArbitrationSubmission(string? Request, string? Ruling, string? Instruction);

/// <summary>The orchestrator's final decision on an appeal. For the requester, the owner has to make
/// the change; for the owner, the refusal stands. <see cref="Instruction"/> tells whoever has to act
/// what to do.</summary>
public sealed record ArbitrationRuling(bool RequesterWins, string Instruction);

public static class ArbitrationValidator
{
    public const int MinInstructionLength = 20;
    public const int MaxInstructionLength = 600;

    /// <summary>
    /// Checks a ruling on <paramref name="requestId"/>. The orchestrator is asked about one appeal at a time, so a ruling on
    /// another request is a mistake worth saying so about, not a decision to apply to the wrong one.
    /// </summary>
    public static PipelineCheck<ArbitrationRuling> Validate(ArbitrationSubmission? submission, string requestId)
    {
        if (submission is null)
        {
            return PipelineCheck<ArbitrationRuling>.Failed(["The ruling is empty."]);
        }

        var errors = new ErrorList();
        var asked = PipelineText.Clean(submission.Request);
        if (!string.Equals(asked, requestId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"You were asked to rule on {requestId}, not '{asked}'. Name {requestId} in 'request'.");
        }

        var ruling = PipelineText.Clean(submission.Ruling).ToLowerInvariant();
        bool? requesterWins = ruling.StartsWith("requester", StringComparison.Ordinal) ? true
            : ruling.StartsWith("owner", StringComparison.Ordinal) ? false
            : null;
        if (requesterWins is null)
        {
            errors.Add($"ruling '{ruling}' is not \"requester\" (the owner must make the change) or \"owner\" (the refusal stands).");
        }

        var instruction = PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(submission.Instruction)), MaxInstructionLength, out _).TrimEnd();
        if (instruction.Length < MinInstructionLength)
        {
            errors.Add($"The ruling needs an instruction (at least {MinInstructionLength} characters): "
                       + "for the requester, exactly what the owner is to change; for the owner, what the requester does instead.");
        }

        return errors.ResultOr(() => new ArbitrationRuling(requesterWins!.Value, instruction));
    }
}
