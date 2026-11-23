using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Writes one agent's side of a conversation to the transcript: a sub-agent, a scheduled run, an
/// Ultra worker or the Ultra lead. It is fixed to a solution, a conversation and a source label when
/// it is made, because these agents run on background threads and can outlive both the conversation
/// that started them (New Chat) and the solution (a switch). A late entry then goes to the
/// conversation it belongs to, or nowhere, and never into whatever is open by then.
///
/// Every method is safe to call from any thread and never throws.
/// </summary>
public sealed class TranscriptRecorder
{
    private readonly TranscriptMemoryService _service;
    private readonly string _solutionDirectory;
    private readonly string _conversationId;
    private readonly string _source;

    public TranscriptRecorder(
        TranscriptMemoryService service, string solutionDirectory, string conversationId, string source)
    {
        _service = service;
        _solutionDirectory = solutionDirectory;
        _conversationId = conversationId;
        _source = source;
    }

    /// <summary>The same conversation under another label - "ultra worker alice" for each worker, say.</summary>
    public TranscriptRecorder WithSource(string source) =>
        new(_service, _solutionDirectory, _conversationId, source);

    /// <summary>The instruction the agent was given, which is what its thoughts and replies answer.</summary>
    public void RecordTask(string text) => Write(TranscriptKinds.User, text);

    /// <summary>What the model streamed for one request: its reasoning, if it sent any, then its text.</summary>
    public void RecordModelTurn(string? reasoning, string text)
    {
        Write(TranscriptKinds.Thought, reasoning);
        Write(TranscriptKinds.Assistant, text);
    }

    private void Write(string kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _service.Record(_conversationId, kind, text, _source, onlyIfSolution: _solutionDirectory);
    }
}
