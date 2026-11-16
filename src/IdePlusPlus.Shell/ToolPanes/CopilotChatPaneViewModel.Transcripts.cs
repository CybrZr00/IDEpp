using System.IO;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.Memory;
using Serilog;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// The conversation transcript: every message and every thought, kept for good and searchable.
///
/// chat-history.json is the live conversation and New Chat empties it; project-memory.md is a short
/// summary and drops detail by design; sessions/*.md leaves out the model's reasoning. None of them
/// can answer "what did we decide about X last week, and why" once the conversation they came from
/// is gone. This is written as each message completes, not when a conversation ends, so there is no
/// moment at which clearing, summarising or crashing can lose something that has already been said.
///
/// What is recorded is what the model actually streamed (see StreamOneTurnAsync), not the text of the
/// message bubble, which also carries notes the IDE adds such as "[cancelled]".
///
/// A failure to record is logged once and otherwise ignored: the transcript is a convenience, and a
/// full disk must not stop the conversation it is a copy of.
/// </summary>
public sealed partial class CopilotChatPaneViewModel
{
    private readonly TranscriptMemoryService? _transcripts;

    /// <summary>The conversation new entries are appended to. Null until something is said in a new
    /// conversation, so opening the app and closing it again leaves no empty transcript behind.</summary>
    private string? _transcriptConversationId;

    private bool _transcriptFailureLogged;

    /// <summary>Appends one entry to the current conversation's transcript, starting the
    /// conversation if this is its first. Does nothing without a transcript service or a solution.</summary>
    private void RecordTranscript(string kind, string text, string? source = null)
    {
        if (_transcripts is null || _solutionDirectory is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _transcriptConversationId ??= _transcripts.StartConversation();
        if (_transcriptConversationId is null || !_transcripts.Record(_transcriptConversationId, kind, text, source))
        {
            if (!_transcriptFailureLogged)
            {
                _transcriptFailureLogged = true;
                Log.Warning("Couldn't write to this solution's conversation transcript; later messages will not be searchable");
            }
        }
    }

    /// <summary>Points the transcript at the solution that was just opened, and carries on with the
    /// conversation that was open when it was last closed, if one came back from chat-history.json.
    /// Called once that history has been loaded.</summary>
    private void OpenTranscriptsFor(string? directory)
    {
        if (_transcripts is null)
        {
            return;
        }

        try
        {
            _transcripts.SetSolution(directory);
            _transcriptConversationId = Messages.Count > 0 ? _transcripts.LatestConversationId() : null;
            _transcriptFailureLogged = false;

            if (_transcripts.LoadError is { } loadError)
            {
                Log.Warning("Couldn't read all of this solution's conversation transcripts: {Error}", loadError);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Couldn't open this solution's conversation transcripts");
        }
    }

    private async Task<(bool IsError, string Text)> RunSearchTranscriptsAsync(
        JsonNode? arguments, CancellationToken cancellationToken)
    {
        if (_transcripts is null || _solutionDirectory is null)
        {
            return (true, "No solution is open, so there are no saved transcripts to search.");
        }

        var query = StringArgument(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return (true, "search_transcripts requires a 'query' argument.");
        }

        if (_transcripts.EntryCount == 0)
        {
            return (false, "There are no saved transcripts for this project yet.");
        }

        var kinds = StringArgument(arguments, "kind") switch
        {
            TranscriptTools.KindMessage => TranscriptKindFilter.Messages,
            TranscriptTools.KindThought => TranscriptKindFilter.Thoughts,
            _ => TranscriptKindFilter.Any,
        };

        var limit = IntArgument(arguments, "limit") ?? TranscriptTools.DefaultLimit;
        var result = await _transcripts.SearchAsync(query, kinds, limit, cancellationToken);
        return (false, TranscriptSearchFormatter.Format(query, result, _transcriptConversationId));
    }

    /// <summary>Arguments come from a model, which sends a number as a string now and then; reading one
    /// the wrong way must not end the turn.</summary>
    private static string? StringArgument(JsonNode? arguments, string name) =>
        arguments?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? IntArgument(JsonNode? arguments, string name)
    {
        if (arguments?[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }
}
