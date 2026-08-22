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
    private const string EffortSettingKey = "ChatEffort";
    private const string AutoModeSettingKey = "ChatAutoMode";
    private const string PlanModeSettingKey = "ChatPlanMode";

    /// <summary>Public so SettingsViewModel can read/write the same key - re-read fresh on every
    /// turn (see PrependContextAsync) rather than cached, the same "no refresh plumbing needed"
    /// pattern SettingsViewModel's own doc comment describes for the OpenRouter/DeepSeek/LM Studio
    /// settings it also owns.</summary>
    public const string SystemPromptSettingKey = "ChatSystemPrompt";

    private const int CodebaseSearchTopK = 5;
    private const int MaxToolLoopIterations = 8;

    // A rough, tokenizer-free heuristic (not an exact token count) purely to warn before a request
    // is likely to hit a provider's context limit - 4 chars/token is the commonly-cited rule of
    // thumb for English text, and erring toward warning too early costs nothing.
    private const int LongConversationCharThreshold = 4 * 100_000;

    // Manual mode's 8-iteration cap was implicitly backstopped by every propose_edit needing a
    // human click to proceed - Auto Mode removes that checkpoint, so a purely tool-call-driven
    // agentic task (e.g. against MCP tools) needs more room to run before hitting the ceiling.
    // CancelStreaming is always available as the actual safety valve.
    private const int AutoModeMaxToolLoopIterations = 30;

    private readonly OpenRouterChatClient _openRouterClient;
    private readonly LmStudioChatClient _lmStudioClient;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly EditApplicationService _editApplicationService;
    private readonly ProjectMemoryStore _projectMemoryStore;
    private readonly ChatHistoryStore _chatHistoryStore;
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
        ChatHistoryStore chatHistoryStore,
        CodebaseIndexService codebaseIndexService,
        McpServerManager mcpServerManager)
        : base(PaneContentId, "Copilot Chat")
    {
        _openRouterClient = openRouterClient;
        _lmStudioClient = lmStudioClient;
        _appSettingsStore = appSettingsStore;
        _editApplicationService = editApplicationService;
        _projectMemoryStore = projectMemoryStore;
        _chatHistoryStore = chatHistoryStore;
        _codebaseIndexService = codebaseIndexService;
        _mcpServerManager = mcpServerManager;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        selectedProvider = _appSettingsStore.Get(ProviderSettingKey) is ProviderLmStudio ? ProviderLmStudio : ProviderOpenRouter;
        selectedModel = ModelSettingFor(selectedProvider) ?? DefaultModelFor(selectedProvider);
        selectedEffort = Enum.TryParse<ChatEffort>(_appSettingsStore.Get(EffortSettingKey), out var savedEffort)
            ? savedEffort
            : ChatEffort.None;
        isAutoMode = bool.TryParse(_appSettingsStore.Get(AutoModeSettingKey), out var savedAutoMode) && savedAutoMode;
        isPlanMode = bool.TryParse(_appSettingsStore.Get(PlanModeSettingKey), out var savedPlanMode) && savedPlanMode;

        AvailableModels = new ObservableCollection<string>(
            selectedProvider == ProviderLmStudio ? [] : OpenRouterModels);

        if (selectedProvider == ProviderLmStudio)
        {
            _ = RefreshLmStudioModelsAsync();
        }
    }

    public ObservableCollection<ObservableObject> Messages { get; } = [];

    private static readonly string[] OpenRouterModels =
    [
        "anthropic/claude-sonnet-4.5",
        "openai/gpt-5",
        "deepseek/deepseek-v4-pro",
        "google/gemini-3-pro",
    ];

    /// <summary>The model picker's dropdown contents - the fixed OpenRouter list, or whatever LM Studio's /v1/models last reported.</summary>
    public ObservableCollection<string> AvailableModels { get; }

    public IReadOnlyList<string> AvailableProviders { get; } = [ProviderOpenRouter, ProviderLmStudio];

    public IReadOnlyList<ChatEffort> AvailableEfforts { get; } = [ChatEffort.None, ChatEffort.Low, ChatEffort.Medium, ChatEffort.High];

    [ObservableProperty]
    private string selectedProvider;

    [ObservableProperty]
    private string selectedModel;

    [ObservableProperty]
    private ChatEffort selectedEffort;

    /// <summary>When on, edit proposals are accepted the instant they finish streaming instead of
    /// waiting for a manual Accept click - the diff still renders first for auditability, it's just
    /// not gated.</summary>
    [ObservableProperty]
    private bool isAutoMode;

    /// <summary>When on, the model gets propose_plan instead of propose_edit and is instructed to
    /// investigate and present a plan rather than touch any files - propose_edit only becomes
    /// available again once the user approves a plan (see ApprovePlanAsync).</summary>
    [ObservableProperty]
    private bool isPlanMode;

    [ObservableProperty]
    private bool isRefreshingModels;

    public bool IsLmStudioProvider => SelectedProvider == ProviderLmStudio;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenUsageText))]
    private int totalPromptTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenUsageText))]
    private int totalCompletionTokens;

    /// <summary>Cumulative for this pane's lifetime, summed across every request made (not just the
    /// latest turn) - since each request re-sends the full conversation, this reflects total tokens
    /// actually billed this session, which is the more useful number for a cost indicator.</summary>
    public string TokenUsageText => TotalPromptTokens + TotalCompletionTokens > 0
        ? $"Tokens: {TotalPromptTokens:N0} in / {TotalCompletionTokens:N0} out"
        : "";

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

            Messages.Clear();
            foreach (var stored in _chatHistoryStore.Load(directory))
            {
                if (Enum.TryParse<ChatRole>(stored.Role, out var role))
                {
                    Messages.Add(new ChatMessageViewModel(role) { Text = stored.Text });
                }
            }
        }

        SaveMemoryCommand.NotifyCanExecuteChanged();
        IndexCodebaseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Auto-saved after every assistant turn so the conversation survives an app restart -
    /// only the plain chat transcript, not edit/plan proposals or MCP tool-call cards, matching what
    /// SendAsync itself replays back into the model's history on the next turn.</summary>
    private void PersistChatHistory()
    {
        if (_solutionDirectory is null)
        {
            return;
        }

        var stored = Messages
            .OfType<ChatMessageViewModel>()
            .Select(m => new StoredChatMessage(m.Role.ToString(), m.Text))
            .ToList();

        _chatHistoryStore.Save(_solutionDirectory, stored);
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

        if (history.Sum(m => m.Content.Length) > LongConversationCharThreshold)
        {
            StatusMessage = "This conversation is getting long and may hit the model's context limit soon - " +
                             "consider using Save Memory and starting a fresh conversation.";
        }

        var assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistantMessage);

        await RunAssistantTurnsAsync(history, assistantMessage);
    }

    private async Task PrependContextAsync(List<ChatMessage> history, string userText)
    {
        if (_appSettingsStore.Get(SystemPromptSettingKey) is { Length: > 0 } systemPrompt)
        {
            history.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));
        }

        if (IsPlanMode)
        {
            history.Insert(0, new ChatMessage(
                ChatRole.System,
                "Plan Mode is active: investigate first (read files, search, use read-only tools), then call " +
                "propose_plan exactly once with your full implementation plan. Do not attempt any file edits - " +
                "propose_edit is unavailable until the user approves your plan."));
        }

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
            var maxIterations = IsAutoMode ? AutoModeMaxToolLoopIterations : MaxToolLoopIterations;
            for (var iteration = 0; iteration < maxIterations; iteration++)
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
            PersistChatHistory();
        }
    }

    private async Task<List<PendingMcpCall>> StreamOneTurnAsync(
        List<ChatMessage> history, ChatMessageViewModel assistantMessage, CancellationToken cancellationToken)
    {
        var proposalParser = new EditProposalParser();
        var proposalsByToolCallId = new Dictionary<string, EditProposalViewModel>();
        var planParser = new PlanProposalParser();
        var planProposalsByToolCallId = new Dictionary<string, PlanProposalViewModel>();
        var mcpCallsByIndex = new Dictionary<int, PendingMcpCall>();
        var routeByIndex = new Dictionary<int, ToolCallRoute>();

        var tools = new List<ToolDefinition> { IsPlanMode ? ProposePlanTool.Definition : ProposeEditTool.Definition };
        tools.AddRange(_mcpServerManager.GetToolDefinitions());

        IChatClient client = SelectedProvider == ProviderLmStudio ? _lmStudioClient : _openRouterClient;

        await foreach (var streamEvent in client.StreamChatAsync(history, SelectedModel, tools, SelectedEffort, cancellationToken))
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
                            routeByIndex[toolCallDelta.Index] = toolCallDelta.FunctionName switch
                            {
                                ProposeEditTool.ToolName => ToolCallRoute.ProposeEdit,
                                ProposePlanTool.ToolName => ToolCallRoute.ProposePlan,
                                _ => ToolCallRoute.Mcp,
                            };
                        }

                        switch (routeByIndex.GetValueOrDefault(toolCallDelta.Index, ToolCallRoute.Mcp))
                        {
                            case ToolCallRoute.ProposeEdit:
                                HandleToolCallDelta(toolCallDelta, proposalParser, proposalsByToolCallId);
                                break;
                            case ToolCallRoute.ProposePlan:
                                HandlePlanToolCallDelta(toolCallDelta, planParser, planProposalsByToolCallId);
                                break;
                            default:
                                AccumulateMcpCall(toolCallDelta, mcpCallsByIndex);
                                break;
                        }

                        break;

                    case StreamError error:
                        assistantMessage.Append($"\n\n[error: {error.Message}]");
                        break;

                    case UsageReceived usage:
                        TotalPromptTokens += usage.PromptTokens;
                        TotalCompletionTokens += usage.CompletionTokens;
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

    private enum ToolCallRoute
    {
        ProposeEdit,
        ProposePlan,
        Mcp,
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
            if (IsAutoMode)
            {
                proposal.AcceptCommand.Execute(null);
            }
        }
        else
        {
            proposal.UpdateStreaming(proposalEvent);
        }
    }

    private void HandlePlanToolCallDelta(
        ToolCallDelta delta,
        PlanProposalParser planParser,
        Dictionary<string, PlanProposalViewModel> proposalsByToolCallId)
    {
        var planEvent = planParser.OnToolCallDelta(delta);

        if (!proposalsByToolCallId.TryGetValue(planEvent.ToolCallId, out var proposal))
        {
            proposal = new PlanProposalViewModel(planEvent.ToolCallId, ApprovePlanAsync);
            proposalsByToolCallId[planEvent.ToolCallId] = proposal;
            Messages.Add(proposal);
        }

        if (planEvent.Status == PlanProposalStatus.Complete)
        {
            proposal.Complete(planEvent);
        }
        else
        {
            proposal.UpdateStreaming(planEvent);
        }
    }

    /// <summary>
    /// Turns Plan Mode off (so the next turn gets the real propose_edit tool) and re-prompts the
    /// model to carry out the approved plan, continuing the same conversation rather than waiting
    /// for the user to type something.
    /// </summary>
    private async Task ApprovePlanAsync(PlanProposalViewModel proposal)
    {
        IsPlanMode = false;

        var marker = new ChatMessageViewModel(ChatRole.User) { Text = "✅ Approve plan & implement" };
        Messages.Add(marker);

        var stepsList = string.Join("\n", proposal.Steps.Select((step, i) => $"{i + 1}. {step}"));
        var instruction = $"""
            The plan below was approved. Implement it now - use propose_edit for any file changes, and
            keep calling tools as needed. Don't propose another plan; just do the work.

            Summary: {proposal.Summary}

            Steps:
            {stepsList}
            """;

        var history = Messages
            .OfType<ChatMessageViewModel>()
            .Where(m => m != marker)
            .Select(m => new ChatMessage(m.Role, m.Text))
            .ToList();
        history.Add(new ChatMessage(ChatRole.User, instruction));

        var assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistantMessage);

        await RunAssistantTurnsAsync(history, assistantMessage);
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

    [RelayCommand]
    private async Task RefreshLmStudioModelsAsync()
    {
        IsRefreshingModels = true;
        try
        {
            var models = await _lmStudioClient.GetAvailableModelsAsync();

            AvailableModels.Clear();
            foreach (var model in models)
            {
                AvailableModels.Add(model);
            }

            if (models.Count == 0)
            {
                StatusMessage = "Couldn't reach LM Studio - check the base URL in Tools > Settings and that it's running.";
            }
            else if (string.IsNullOrWhiteSpace(SelectedModel) || !models.Contains(SelectedModel))
            {
                SelectedModel = models[0];
            }
        }
        finally
        {
            IsRefreshingModels = false;
        }
    }

    partial void OnSelectedProviderChanged(string value)
    {
        _appSettingsStore.Set(ProviderSettingKey, value);
        SelectedModel = ModelSettingFor(value) ?? DefaultModelFor(value);
        OnPropertyChanged(nameof(IsLmStudioProvider));

        if (value == ProviderLmStudio)
        {
            _ = RefreshLmStudioModelsAsync();
        }
        else
        {
            AvailableModels.Clear();
            foreach (var model in OpenRouterModels)
            {
                AvailableModels.Add(model);
            }
        }
    }

    partial void OnSelectedModelChanged(string value) =>
        _appSettingsStore.Set(SelectedProvider == ProviderLmStudio ? LmStudioModelSettingKey : OpenRouterModelSettingKey, value);

    partial void OnSelectedEffortChanged(ChatEffort value) => _appSettingsStore.Set(EffortSettingKey, value.ToString());

    partial void OnIsAutoModeChanged(bool value) => _appSettingsStore.Set(AutoModeSettingKey, value.ToString());

    partial void OnIsPlanModeChanged(bool value) => _appSettingsStore.Set(PlanModeSettingKey, value.ToString());

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SaveMemoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsIndexingChanged(bool value) => IndexCodebaseCommand.NotifyCanExecuteChanged();

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
}
