using System.Text;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Reconstructs propose_plan tool-call arguments from streamed fragments - the same
/// accumulate-until-valid-JSON approach as EditProposalParser, simplified since a plan has no
/// diff to preview mid-stream (just the summary).
/// </summary>
public sealed class PlanProposalParser
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

        var toolCallId = _toolCallIdByIndex.GetValueOrDefault(delta.Index, $"index-{delta.Index}");
        var accumulated = buffer.ToString();

        var plan = TryParseComplete(accumulated);
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

    private static PlanOperation? TryParseComplete(string json)
    {
        if (json.Length == 0)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(json);
            var summary = node?["summary"]?.GetValue<string>();
            var stepsArray = node?["steps"]?.AsArray();
            if (summary is null || stepsArray is null)
            {
                return null;
            }

            var steps = stepsArray
                .Select(s => s?.GetValue<string>())
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            return new PlanOperation(summary, steps);
        }
        catch (Exception)
        {
            // Not syntactically complete yet (the normal case mid-stream) - keep accumulating.
            return null;
        }
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
