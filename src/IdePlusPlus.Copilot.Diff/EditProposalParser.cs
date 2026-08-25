using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Copilot.Diff;

/// <summary>
/// Reconstructs propose_edit tool-call arguments from streamed fragments. Validated against a
/// real OpenRouter/Claude streaming response before this was written: arguments arrive as true
/// incremental fragments (must be concatenated), and id/name are only present on a tool call's
/// first delta - see the M5 spike notes. A full EditOperation only becomes available once the
/// accumulated text is valid JSON (normally just the final fragment); until then, a lenient
/// substring scan gives a live preview of the file/new_text fields for streaming UI feedback.
/// </summary>
public sealed class EditProposalParser
{
    private readonly Dictionary<int, StringBuilder> _argumentsByIndex = [];
    private readonly Dictionary<int, string> _toolCallIdByIndex = [];

    public EditProposalEvent OnToolCallDelta(ToolCallDelta delta)
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

        var operation = TryParseComplete(accumulated);
        if (operation is not null)
        {
            return new EditProposalEvent { ToolCallId = toolCallId, Status = EditProposalStatus.Complete, Operation = operation };
        }

        return new EditProposalEvent
        {
            ToolCallId = toolCallId,
            Status = EditProposalStatus.Streaming,
            FilePathPreview = TryExtractStringField(accumulated, "file"),
            NewTextPreview = TryExtractStringField(accumulated, "new_text"),
        };
    }

    private static EditOperation? TryParseComplete(string json)
    {
        if (json.Length == 0)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(json);
            var file = node?["file"]?.GetValue<string>();
            var mode = node?["mode"]?.GetValue<string>();
            var newText = node?["new_text"]?.GetValue<string>();
            if (file is null || mode is null || newText is null)
            {
                return null;
            }

            var startLine = node!["start_line"]?.GetValue<int>();
            var endLine = node["end_line"]?.GetValue<int>();
            var oldText = node["old_text"]?.GetValue<string>();
            return new EditOperation(file, mode, startLine, endLine, newText, oldText);
        }
        catch (Exception)
        {
            // Not syntactically complete yet (the normal case mid-stream) - keep accumulating.
            return null;
        }
    }

    /// <summary>
    /// Best-effort extraction of a string field's value from a possibly-incomplete JSON
    /// fragment, for live preview only - TryParseComplete is what produces the authoritative
    /// EditOperation. Handles the common JSON escapes; anything unrecognized passes through.
    /// </summary>
    private static string? TryExtractStringField(string partialJson, string fieldName)
    {
        var marker = $"\"{fieldName}\":";
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
