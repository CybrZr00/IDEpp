using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.LmStudio;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class CopilotChatPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "CopilotChat";

    private const string ProviderOpenRouter = "OpenRouter";
    private const string ProviderLmStudio = "LM Studio";

    private const string ProviderSettingKey = "ChatProvider";
    private const string OpenRouterModelSettingKey = "OpenRouterChatModel";
    private const string LmStudioModelSettingKey = "LmStudioChatModel";

    private const int CodebaseSearchTopK = 5;
    private const int MaxToolLoopIterations = 8;

    private readonly OpenRouterChatClient _openRouterClient;
    private readonly LmStudioChatClient _lmStudioClient;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly EditApplicationService _editApplicationService;
    private readonly ProjectMemoryStore _projectMemoryStore;
    private readonly CodebaseIndexService _codebaseIndexService;
    private readonly McpServerManager _mcpServerManager;
    private readonly Dispatcher _dispatcher;
    private DocumentViewModel? _currentDocument;
    private string? _solutionDirectory;
    private IReadOnlyList<string> _indexableFiles = [];
    private CancellationTokenSource? _streamCancellation;

    public CopilotChatPaneViewModel(
        OpenRouterChatClient openRouterClient,
        LmStudioChatClient lmStudioClient,
        AppSettingsStore appSettingsStore,
        EditApplicationService editApplicationService,
        ProjectMemoryStore projectMemoryStore,
        CodebaseIndexService codebaseIndexService,
        McpServerManager mcpServerManager)
        : base(PaneContentId, "Copilot Chat")
    {
        _openRouterClient = openRouterClient;
        _lmStudioClient = lmStudioClient;
        _appSettingsStore = appSettingsStore;
        _editApplicationService = editApplicationService;
        _projectMemoryStore = projectMemoryStore;
        _codebaseIndexService = codebaseIndexService;
        _mcpServerManager = mcpServerManager;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        selectedProvider = _appSettingsStore.Get(ProviderSettingKey) is ProviderLmStudio ? ProviderLmStudio : ProviderOpenRouter;
        selectedModel = ModelSettingFor(selectedProvider) ?? DefaultModelFor(selectedProvider);
    }

    public ObservableCollection<ObservableObject> Messages { get; } = [];

    public ObservableCollection<string> AvailableModels { get; } =
    [
        "anthropic/claude-sonnet-4.5",
        "openai/gpt-5",
        "deepseek/deepseek-v4-pro",
        "google/gemini-3-pro",
    ];

    public IReadOnlyList<string> AvailableProviders { get; } = [ProviderOpenRouter, ProviderLmStudio];

    [ObservableProperty]
    private string selectedProvider;

    [ObservableProperty]
    private string selectedModel;

    [ObservableProperty]
    private string inputText = "";

    [ObservableProperty]
    private bool includeCurrentFileContext = true;

    [ObservableProperty]
    private bool useCodebaseSearch;

    [ObservableProperty]
    private string? indexStatus;

    [ObservableProperty]
    private bool isStreaming;

    [ObservableProperty]
    private bool isIndexing;

    [ObservableProperty]
    private string? statusMessage;

    public void SetActiveDocument(DocumentViewModel? document) => _currentDocument = document;

    public void SetSolutionDirectory(string? directory)
    {
        _solutionDirectory = directory;
        if (directory is not null)
        {
            _codebaseIndexService.LoadFromDisk(ProjectMemoryStore.IndexFilePath(directory));
            IndexStatus = _codebaseIndexService.IsIndexed
                ? $"Loaded index: {_codebaseIndexService.IndexedChunkCount} chunks."
                : null;
        }

        SaveMemoryCommand.NotifyCanExecuteChanged();
        IndexCodebaseCommand.NotifyCanExecuteChanged();
    }

    public void SetIndexableFiles(IReadOnlyList<string> filePaths)
    {
        _indexableFiles = filePaths;
        IndexCodebaseCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend() => !IsStreaming && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var userText = InputText.Trim();
        InputText = "";
        StatusMessage = null;

        var userMessage = new ChatMessageViewModel(ChatRole.User) { Text = userText };
        Messages.Add(userMessage);

        var history = Messages
            .OfType<ChatMessageViewModel>()
            .Where(m => m != userMessage)
            .Select(m => new ChatMessage(m.Role, m.Text))
            .ToList();

        await PrependContextAsync(history, userText);
        history.Add(new ChatMessage(ChatRole.User, userText));

        var assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistantMessage);

        await RunAssistantTurnsAsync(history, assistantMessage);
    }

    private async Task PrependContextAsync(List<ChatMessage> history, string userText)
    {
        if (_solutionDirectory is not null)
        {
            var memory = _projectMemoryStore.LoadMemory(_solutionDirectory);
            if (!string.IsNullOrWhiteSpace(memory))
            {
                history.Insert(0, new ChatMessage(
                    ChatRole.System, $"Project memory (persisted context from previous sessions):\n\n{memory}"));
            }
        }

        if (IncludeCurrentFileContext && _currentDocument is { FilePath: { } path })
        {
            history.Insert(0, new ChatMessage(
                ChatRole.System, $"The user currently has this file open ({path}):\n\n{_currentDocument.TextDocument.Text}"));
        }

        if (UseCodebaseSearch && _codebaseIndexService.IsIndexed)
        {
            var relevantChunks = await _codebaseIndexService.SearchAsync(userText, CodebaseSearchTopK, CancellationToken.None);
            if (relevantChunks.Count > 0)
            {
                var contextText = string.Join(
                    "\n\n", relevantChunks.Select(c => $"// {c.FilePath}:{c.StartLine}-{c.EndLine}\n{c.Text}"));
                history.Insert(0, new ChatMessage(
                    ChatRole.System, $"Potentially relevant code found by searching the project:\n\n{contextText}"));
            }
        }
    }

    /// <summary>
    /// Streams the assistant's response, and - unlike propose_edit, which is terminal until the
    /// user accepts/rejects it - automatically executes any MCP tool calls and feeds their results
    /// back for the model to continue, looping (bounded by MaxToolLoopIterations) until a turn
    /// produces no further MCP tool calls.
    /// </summary>
    private async Task RunAssistantTurnsAsync(List<ChatMessage> history, ChatMessageViewModel assistantMessage)
    {
        IsStreaming = true;
        _streamCancellation = new CancellationTokenSource();
        try
        {
            for (var iteration = 0; iteration < MaxToolLoopIterations; iteration++)
            {
                var pendingMcpCalls = await StreamOneTurnAsync(history, assistantMessage, _streamCancellation.Token);
                if (pendingMcpCalls.Count == 0)
                {
                    break;
                }

                history.Add(new ChatMessage(
                    ChatRole.Assistant,
                    assistantMessage.Text,
                    ToolCalls: pendingMcpCalls.Select(c => new ChatToolCall(c.EffectiveId, c.Name!, c.Arguments.ToString())).ToList()));

                foreach (var call in pendingMcpCalls)
                {
                    var callViewModel = new McpToolCallViewModel(call.Name!);
                    _dispatcher.Invoke(() => Messages.Add(callViewModel));

                    var result = await _mcpServerManager.CallToolAsync(call.Name!, call.Arguments.ToString(), _streamCancellation.Token);
                    _dispatcher.Invoke(() => callViewModel.Complete(result.IsError, result.Text));

                    history.Add(new ChatMessage(ChatRole.Tool, result.Text, ToolCallId: call.EffectiveId));
                }

                assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
                _dispatcher.Invoke(() => Messages.Add(assistantMessage));
            }
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Invoke(() => assistantMessage.Append("\n\n[cancelled]"));
        }
        finally
        {
            IsStreaming = false;
            _streamCancellation = null;
        }
    }

    private async Task<List<PendingMcpCall>> StreamOneTurnAsync(
        List<ChatMessage> history, ChatMessageViewModel assistantMessage, CancellationToken cancellationToken)
    {
        var proposalParser = new EditProposalParser();
        var proposalsByToolCallId = new Dictionary<string, EditProposalViewModel>();
        var mcpCallsByIndex = new Dictionary<int, PendingMcpCall>();
        var routeIsProposeEdit = new Dictionary<int, bool>();

        var tools = new List<ToolDefinition> { ProposeEditTool.Definition };
        tools.AddRange(_mcpServerManager.GetToolDefinitions());

        IChatClient client = SelectedProvider == ProviderLmStudio ? _lmStudioClient : _openRouterClient;

        await foreach (var streamEvent in client.StreamChatAsync(history, SelectedModel, tools, cancellationToken))
        {
            _dispatcher.Invoke(() =>
            {
                switch (streamEvent)
                {
                    case TextDelta delta:
                        assistantMessage.Append(delta.Text);
                        break;

                    case ToolCallDelta toolCallDelta:
                        if (toolCallDelta.FunctionName is not null)
                        {
                            routeIsProposeEdit[toolCallDelta.Index] = toolCallDelta.FunctionName == ProposeEditTool.ToolName;
                        }

                        if (routeIsProposeEdit.GetValueOrDefault(toolCallDelta.Index))
                        {
                            HandleToolCallDelta(toolCallDelta, proposalParser, proposalsByToolCallId);
                        }
                        else
                        {
                            AccumulateMcpCall(toolCallDelta, mcpCallsByIndex);
                        }

                        break;

                    case StreamError error:
                        assistantMessage.Append($"\n\n[error: {error.Message}]");
                        break;
                }
            });
        }

        return mcpCallsByIndex.Values.Where(c => c.Name is not null).ToList();
    }

    private static void AccumulateMcpCall(ToolCallDelta delta, Dictionary<int, PendingMcpCall> calls)
    {
        if (!calls.TryGetValue(delta.Index, out var call))
        {
            call = new PendingMcpCall(delta.Index);
            calls[delta.Index] = call;
        }

        if (delta.Id is not null)
        {
            call.Id = delta.Id;
        }

        if (delta.FunctionName is not null)
        {
            call.Name = delta.FunctionName;
        }

        if (delta.ArgumentsFragment is not null)
        {
            call.Arguments.Append(delta.ArgumentsFragment);
        }
    }

    private sealed class PendingMcpCall(int index)
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Arguments = new();

        public string EffectiveId => Id ?? $"call-{index}";
    }

    private void HandleToolCallDelta(
        ToolCallDelta delta,
        EditProposalParser proposalParser,
        Dictionary<string, EditProposalViewModel> proposalsByToolCallId)
    {
        var proposalEvent = proposalParser.OnToolCallDelta(delta);

        if (!proposalsByToolCallId.TryGetValue(proposalEvent.ToolCallId, out var proposal))
        {
            proposal = new EditProposalViewModel(proposalEvent.ToolCallId, _editApplicationService);
            proposalsByToolCallId[proposalEvent.ToolCallId] = proposal;
            Messages.Add(proposal);
        }

        if (proposalEvent.Status == EditProposalStatus.Complete)
        {
            proposal.Complete(proposalEvent, _solutionDirectory);
        }
        else
        {
            proposal.UpdateStreaming(proposalEvent);
        }
    }

    [RelayCommand]
    private void CancelStreaming() => _streamCancellation?.Cancel();

    private bool CanSaveMemory() => _solutionDirectory is not null && !IsStreaming;

    [RelayCommand(CanExecute = nameof(CanSaveMemory))]
    private async Task SaveMemoryAsync()
    {
        var solutionDirectory = _solutionDirectory!;
        var existingMemory = _projectMemoryStore.LoadMemory(solutionDirectory);
        var transcript = string.Join(
            "\n\n",
            Messages.OfType<ChatMessageViewModel>().Select(m => $"**{m.Role}:** {m.Text}"));

        if (string.IsNullOrWhiteSpace(transcript))
        {
            StatusMessage = "Nothing to save yet - have a conversation first.";
            return;
        }

        _projectMemoryStore.SaveSessionLog(solutionDirectory, transcript);

        var marker = new ChatMessageViewModel(ChatRole.User) { Text = "🧠 Save memory" };
        Messages.Add(marker);

        var memoryPath = ProjectMemoryStore.MemoryFilePath(solutionDirectory);
        var prompt = $"""
            You maintain a persistent project memory file for this codebase, used to give an AI assistant
            context across sessions. Propose an updated version of "{memoryPath}" via propose_edit
            (mode: whole_file), incorporating anything durably useful from the conversation below into the
            existing memory. Keep it concise (a few hundred words), organized under headings like Overview,
            Architecture, Conventions, Open Threads. Preserve existing content that's still accurate; remove
            anything stale. Do not respond with prose, only call propose_edit.

            --- EXISTING project-memory.md ---
            {(string.IsNullOrWhiteSpace(existingMemory) ? "(empty - this is the first save)" : existingMemory)}

            --- RECENT CONVERSATION ---
            {transcript}
            """;

        var history = new List<ChatMessage> { new(ChatRole.User, prompt) };
        var assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistantMessage);

        await RunAssistantTurnsAsync(history, assistantMessage);
    }

    private bool CanIndexCodebase() => _solutionDirectory is not null && !IsIndexing && _indexableFiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanIndexCodebase))]
    private async Task IndexCodebaseAsync()
    {
        var solutionDirectory = _solutionDirectory!;
        IsIndexing = true;
        IndexStatus = "Indexing...";
        try
        {
            await _codebaseIndexService.IndexFilesAsync(
                _indexableFiles,
                progress => _dispatcher.Invoke(() => IndexStatus = progress),
                CancellationToken.None);

            _codebaseIndexService.SaveToDisk(ProjectMemoryStore.IndexFilePath(solutionDirectory));
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private string? ModelSettingFor(string provider) =>
        _appSettingsStore.Get(provider == ProviderLmStudio ? LmStudioModelSettingKey : OpenRouterModelSettingKey);

    private static string DefaultModelFor(string provider) => provider == ProviderLmStudio ? "" : "anthropic/claude-sonnet-4.5";

    partial void OnSelectedProviderChanged(string value)
    {
        _appSettingsStore.Set(ProviderSettingKey, value);
        SelectedModel = ModelSettingFor(value) ?? DefaultModelFor(value);
    }

    partial void OnSelectedModelChanged(string value) =>
        _appSettingsStore.Set(SelectedProvider == ProviderLmStudio ? LmStudioModelSettingKey : OpenRouterModelSettingKey, value);

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SaveMemoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsIndexingChanged(bool value) => IndexCodebaseCommand.NotifyCanExecuteChanged();

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
}
