using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Reconstructs propose_plan tool-call arguments from streamed fragments - the same
/// accumulate-until-valid-JSON approach as EditProposalParser, simplified since a plan has no
/// diff to preview mid-stream (just the summary).
/// </summary>
public sealed partial class PlanProposalParser
{
    private readonly Dictionary<int, StringBuilder> _argumentsByIndex = [];
    private readonly Dictionary<int, string> _toolCallIdByIndex = [];

    public PlanProposalEvent OnToolCallDelta(ToolCallDelta delta)
    {
        if (delta.Id is not null)
        {
            _toolCallIdByIndex[delta.Index] = delta.Id;
        }

        if (!_argumentsByIndex.TryGetValue(delta.Index, out var buffer))
        {
            buffer = new StringBuilder();
            _argumentsByIndex[delta.Index] = buffer;
        }

        if (delta.ArgumentsFragment is not null)
        {
            buffer.Append(delta.ArgumentsFragment);
        }

        var toolCallId = ToolCallIdFor(delta.Index);
        var accumulated = buffer.ToString();

        // Not parsing yet is the normal case mid-stream - keep accumulating.
        var plan = TryParse(accumulated, out _);
        if (plan is not null)
        {
            return new PlanProposalEvent { ToolCallId = toolCallId, Status = PlanProposalStatus.Complete, Plan = plan };
        }

        return new PlanProposalEvent
        {
            ToolCallId = toolCallId,
            Status = PlanProposalStatus.Streaming,
            SummaryPreview = TryExtractSummary(accumulated),
        };
    }

    /// <summary>
    /// Once the stream has ended: every call whose arguments never parsed into a plan, why not, and
    /// what was actually sent. Without this a plan that never parses just stays "streaming" - its
    /// card shows the summary preview and nothing can be clicked, with no sign anything went wrong.
    /// </summary>
    public IReadOnlyList<PlanProposalFailure> Finish()
    {
        var failures = new List<PlanProposalFailure>();
        foreach (var (index, buffer) in _argumentsByIndex)
        {
            var accumulated = buffer.ToString();
            if (TryParse(accumulated, out var reason) is null)
            {
                failures.Add(new PlanProposalFailure(ToolCallIdFor(index), reason!, accumulated));
            }
        }

        return failures;
    }

    private string ToolCallIdFor(int index) => _toolCallIdByIndex.GetValueOrDefault(index, $"index-{index}");

    private static PlanOperation? TryParse(string json, out string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            failureReason = "the model sent no plan at all";
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            failureReason = $"the plan isn't valid JSON - most likely cut off or malformed ({ex.Message})";
            return null;
        }

        // Some models send the arguments object as a JSON string that holds the JSON.
        if (node is JsonValue wrapped && wrapped.TryGetValue<string>(out var inner) && inner.TrimStart().StartsWith('{'))
        {
            return TryParse(inner, out failureReason);
        }

        if (node is not JsonObject fields)
        {
            failureReason = "the plan isn't a JSON object";
            return null;
        }

        if (fields["summary"] is not JsonValue summaryValue || !summaryValue.TryGetValue<string>(out var summary))
        {
            failureReason = "the plan has no \"summary\" text";
            return null;
        }

        var steps = fields["steps"] switch
        {
            JsonArray items => items.Select(StepText).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList(),
            JsonValue value when value.TryGetValue<string>(out var text) => StepsFromString(text),
            _ => null,
        };
        if (steps is null)
        {
            failureReason = "the plan has no \"steps\" list";
            return null;
        }

        failureReason = null;
        return new PlanOperation(summary, steps);
    }

    /// <summary>
    /// The whole list as one string. Usually a step per line, but a model also sends its JSON list as the
    /// string - sometimes wrapped in the markup of a tool call (<c>&lt;parameter name="steps"&gt;[...]</c>) -
    /// and splitting that by line made every quoted, comma-ended line a step of its own, plus one for the
    /// markup and one for each bracket. So a string that reads as a JSON list is read as one; anything else,
    /// including a checklist whose lines start with "[x]", is split by line as before.
    /// </summary>
    private static List<string> StepsFromString(string text)
    {
        var trimmed = ParameterMarkup().Replace(text.Trim(), "").Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                if (JsonNode.Parse(trimmed) is JsonArray items)
                {
                    return items.Select(StepText).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
                }
            }
            catch (JsonException)
            {
                // Not a JSON list after all, such as "[x] Read" - fall through to the lines.
            }
        }

        return text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    }

    // <parameter name="steps"> at the start and </parameter> at the end.
    [GeneratedRegex(@"^<parameter\s[^>]*>|</parameter>$", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterMarkup();

    private static readonly string[] StepHeadingKeys = ["title", "name", "step", "action"];
    private static readonly string[] StepDetailKeys = ["description", "detail", "details", "text", "summary"];

    /// <summary>
    /// One step as text. The schema asks for strings, but models also send numbers, objects such as
    /// {"title": ..., "detail": ...}, or nested lists - reading those beats refusing the whole plan.
    /// </summary>
    private static string? StepText(JsonNode? step) => step switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray items => string.Join("; ", items.Select(StepText).Where(s => !string.IsNullOrWhiteSpace(s))),
        JsonObject fields => ObjectStepText(fields),
        _ => step.ToJsonString(),
    };

    private static string ObjectStepText(JsonObject fields)
    {
        // Headings must be text: {"step": 1, "description": ...} is numbered, not titled "1".
        var heading = StepHeadingKeys
            .Select(key => fields[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        var detail = StepDetailKeys
            .Select(key => StepText(fields[key]))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        if (heading is not null || detail is not null)
        {
            return heading is not null && detail is not null ? $"{heading} - {detail}" : (heading ?? detail)!;
        }

        // No key it recognises: every field's text in order, or failing that the object as written.
        var texts = fields.Select(field => StepText(field.Value)).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        return texts.Count > 0 ? string.Join(" - ", texts) : fields.ToJsonString();
    }

    /// <summary>Best-effort extraction of the "summary" string for a live preview while streaming.</summary>
    private static string? TryExtractSummary(string partialJson)
    {
        const string marker = "\"summary\":";
        var markerIndex = partialJson.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var cursor = markerIndex + marker.Length;
        while (cursor < partialJson.Length && partialJson[cursor] is ' ')
        {
            cursor++;
        }

        if (cursor >= partialJson.Length || partialJson[cursor] != '"')
        {
            return null;
        }

        cursor++;
        var value = new StringBuilder();
        for (var i = cursor; i < partialJson.Length; i++)
        {
            var c = partialJson[i];
            if (c == '\\' && i + 1 < partialJson.Length)
            {
                value.Append(partialJson[i + 1] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    var other => other,
                });
                i++;
                continue;
            }

            if (c == '"')
            {
                break;
            }

            value.Append(c);
        }

        return value.ToString();
    }
}
