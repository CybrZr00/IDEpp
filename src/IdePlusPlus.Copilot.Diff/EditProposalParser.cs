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
    /// <summary>
    /// How much of one call's arguments is kept in memory. Past this the call is spilled to a temp
    /// file and refused: a single proposal this size cannot be reviewed anyway, and holding it cost far
    /// more than its length - the buffer, a string copy on every fragment, the parsed JSON, the diff -
    /// which is how one runaway whole-file write grew the app to tens of gigabytes.
    /// </summary>
    public const int DefaultMaxArgumentChars = 2_000_000;

    /// <summary>The most written to the spill file; the rest of a runaway stream is only counted.</summary>
    private const long MaxSpillChars = 256_000_000;

    /// <summary>Below this, every fragment is parsed and previewed. Above it, scanning the whole
    /// accumulated text per fragment is quadratic, so previews are throttled and a parse is only
    /// attempted when the fragment could have closed the object.</summary>
    private const int ThrottleAboveChars = 65_536;

    private sealed class CallState
    {
        public StringBuilder Buffer { get; set; } = new();
        public string? SpillPath { get; set; }
        public long TotalChars { get; set; }
        public int LastPreviewLength { get; set; }
    }

    private readonly Dictionary<int, CallState> _calls = [];
    private readonly Dictionary<int, string> _toolCallIdByIndex = [];
    private readonly int _maxArgumentChars;
    private readonly string _spillDirectory;

    public EditProposalParser(int maxArgumentChars = DefaultMaxArgumentChars, string? spillDirectory = null)
    {
        _maxArgumentChars = maxArgumentChars;
        _spillDirectory = spillDirectory
            ?? Path.Combine(Path.GetTempPath(), "IdePlusPlus", "oversized-proposals");
    }

    public EditProposalEvent OnToolCallDelta(ToolCallDelta delta)
    {
        if (delta.Id is not null)
        {
            _toolCallIdByIndex[delta.Index] = delta.Id;
        }

        if (!_calls.TryGetValue(delta.Index, out var call))
        {
            call = new CallState();
            _calls[delta.Index] = call;
        }

        var toolCallId = _toolCallIdByIndex.GetValueOrDefault(delta.Index, $"index-{delta.Index}");
        var fragment = delta.ArgumentsFragment;

        if (call.SpillPath is not null)
        {
            AppendToSpill(call, fragment);
            return OversizedEvent(toolCallId, call);
        }

        if (fragment is not null)
        {
            call.Buffer.Append(fragment);
            call.TotalChars += fragment.Length;
        }

        if (call.Buffer.Length > _maxArgumentChars)
        {
            SpillToTempFile(call, toolCallId);
            return OversizedEvent(toolCallId, call);
        }

        var length = call.Buffer.Length;
        var large = length > ThrottleAboveChars;

        // A complete object ends with its closing brace, so a fragment without one cannot have
        // finished it - for a large buffer that is the difference between one parse and thousands.
        if (!large || fragment is null || fragment.Contains('}'))
        {
            var accumulated = call.Buffer.ToString();
            var operation = TryParseComplete(accumulated);
            if (operation is not null)
            {
                return new EditProposalEvent
                {
                    ToolCallId = toolCallId,
                    Status = EditProposalStatus.Complete,
                    Operation = operation,
                    ArgumentsJson = accumulated,
                };
            }

            call.LastPreviewLength = length;
            return StreamingEvent(toolCallId, accumulated);
        }

        // Large and not finished: refresh the live preview only every so often.
        if (length - call.LastPreviewLength < ThrottleAboveChars)
        {
            return new EditProposalEvent { ToolCallId = toolCallId, Status = EditProposalStatus.Streaming };
        }

        call.LastPreviewLength = length;
        return StreamingEvent(toolCallId, call.Buffer.ToString());
    }

    private static EditProposalEvent StreamingEvent(string toolCallId, string accumulated) => new()
    {
        ToolCallId = toolCallId,
        Status = EditProposalStatus.Streaming,
        FilePathPreview = TryExtractStringField(accumulated, "file"),
        NewTextPreview = TryExtractStringField(accumulated, "new_text"),
    };

    private static EditProposalEvent OversizedEvent(string toolCallId, CallState call) => new()
    {
        ToolCallId = toolCallId,
        Status = EditProposalStatus.Oversized,
        SpillPath = call.SpillPath,
        TotalChars = call.TotalChars,
    };

    private void SpillToTempFile(CallState call, string toolCallId)
    {
        var safeId = new string(toolCallId.Where(char.IsLetterOrDigit).Take(40).ToArray());
        Directory.CreateDirectory(_spillDirectory);
        call.SpillPath = Path.Combine(
            _spillDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeId}-{Guid.NewGuid():N}.json");
        File.WriteAllText(call.SpillPath, call.Buffer.ToString());

        // Release the buffer's storage outright, not just its contents.
        call.Buffer = new StringBuilder();
    }

    private static void AppendToSpill(CallState call, string? fragment)
    {
        if (fragment is null)
        {
            return;
        }

        var previous = call.TotalChars;
        call.TotalChars += fragment.Length;
        if (previous < MaxSpillChars)
        {
            File.AppendAllText(call.SpillPath!, fragment);
        }
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
