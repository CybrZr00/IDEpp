using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Infrastructure.Hooks;

/// <summary>
/// What one hook run means. <see cref="Block"/> vetoes a PreToolUse call and, for PostToolUse, marks
/// the feedback as a problem the model should fix. <see cref="Feedback"/> is text for the model;
/// <see cref="Notice"/> is text for the user only (a hook that crashed or timed out is the user's
/// problem to fix, not something the model can act on).
/// </summary>
public sealed record HookDecision(bool Block, string? Feedback, string? Notice)
{
    public static HookDecision None { get; } = new(false, null, null);

    /// <summary>
    /// Follows Claude Code's contract so existing hook scripts work unchanged:
    /// exit 0 = fine, and stdout is ignored unless it is a JSON object;
    /// exit 2 = block, with stderr as the reason;
    /// any other exit = the hook itself failed, which is reported but never blocks anything.
    /// JSON on exit 0 may carry `decision: "block"` + `reason`, `hookSpecificOutput.permissionDecision:
    /// "deny"` + `permissionDecisionReason`, `hookSpecificOutput.additionalContext`, `continue: false`
    /// + `stopReason`, and `systemMessage`.
    /// </summary>
    public static HookDecision Interpret(HookRunResult result, string hookLabel)
    {
        if (result.StartError is not null)
        {
            return new HookDecision(false, null, $"Hook {hookLabel}: {result.StartError}");
        }

        if (result.TimedOut)
        {
            return new HookDecision(false, null, $"Hook {hookLabel} timed out and was stopped.");
        }

        if (result.ExitCode == 2)
        {
            var reason = FirstNonEmpty(result.Stderr, result.Stdout) ?? "(the hook gave no reason)";
            return new HookDecision(true, reason, null);
        }

        if (result.ExitCode != 0)
        {
            var detail = FirstNonEmpty(result.Stderr, result.Stdout);
            return new HookDecision(false, null,
                $"Hook {hookLabel} failed (exit {result.ExitCode}){(detail is null ? "." : $": {FirstLine(detail)}")}");
        }

        return InterpretJson(result.Stdout);
    }

    private static HookDecision InterpretJson(string stdout)
    {
        var text = stdout.Trim();
        if (!text.StartsWith('{'))
        {
            return None;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return None;
        }

        if (root is not JsonObject obj)
        {
            return None;
        }

        var specific = obj["hookSpecificOutput"] as JsonObject;
        var block = false;
        string? feedback = null;

        if (Str(obj["decision"]) == "block")
        {
            block = true;
            feedback = Str(obj["reason"]);
        }

        if (Str(specific?["permissionDecision"]) == "deny")
        {
            block = true;
            feedback ??= Str(specific?["permissionDecisionReason"]);
        }

        if (obj["continue"] is JsonValue cont && cont.TryGetValue<bool>(out var keepGoing) && !keepGoing)
        {
            block = true;
            feedback ??= Str(obj["stopReason"]);
        }

        var context = Str(specific?["additionalContext"]);
        if (context is not null)
        {
            feedback = feedback is null ? context : $"{feedback}\n{context}";
        }

        if (block && string.IsNullOrWhiteSpace(feedback))
        {
            feedback = "(the hook gave no reason)";
        }

        return new HookDecision(block, feedback, Str(obj["systemMessage"]));
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string? FirstNonEmpty(params string[] texts) =>
        texts.Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0);

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd('\r');
    }
}
