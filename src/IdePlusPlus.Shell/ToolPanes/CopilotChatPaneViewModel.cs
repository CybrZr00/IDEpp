using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
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
using IdePlusPlus.Shell;
using Serilog;

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

    /// <summary>
    /// Used whenever the user hasn't set a custom System Prompt in Settings - without this, no
    /// system-level instruction was ever sent at all, and a reasoning-heavy model (Claude Sonnet is
    /// the default - see DefaultModelFor) is prone to responding to a multi-file request with a
    /// prose plan ("1. Remove X... 2. Update Y... Let me implement this:") and then stopping without
    /// ever calling propose_edit, since nothing told it that describing a plan isn't the same as
    /// doing the work. Each tool's own Description (e.g. ProposeEditTool's "always call this... never
    /// paste the new code as plain chat text instead") is a per-tool nudge, not a substitute for this.
    /// </summary>
    private const string DefaultSystemPrompt =
        "You are the AI coding assistant built into the IDE++ IDE, working on the user's open solution. " +
        "You can read files, list directories, search the indexed codebase, and edit files directly in " +
        "the user's editor, plus use any MCP or plugin tools currently connected.\n\n" +
        "How to work:\n" +
        "- Investigate with read_file/list_files/search_codebase, then act in the same turn. Never finish " +
        "a turn having only described an edit: a plan written as chat text changes nothing, and the turn " +
        "does not resume itself.\n" +
        "- Make changes by calling propose_edit - once per file, and several times in one turn when " +
        "several files change. Prefer mode replace_text, quoting the exact snippet to replace: it does " +
        "not depend on line numbers, so it cannot be thrown off by an earlier edit. Use replace_range " +
        "only with line numbers you know are current, and whole_file only for a file you are creating.\n" +
        "- Never abbreviate a file with a placeholder such as \"... rest of file unchanged ...\". " +
        "Whatever you send is written to disk exactly as given.\n" +
        "- Don't re-read a file you already read in this conversation unless you've changed it since - " +
        "and if you have changed it, do re-read it, because every line number below the edit has moved.\n" +
        "- Keep narration short. One line on what you're doing beats a paragraph on what you're about to do.";

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
    private readonly PluginChatToolRegistry _pluginChatToolRegistry;
    private readonly WorkspaceFileReader _workspaceFileReader = new();
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
        McpServerManager mcpServerManager,
        PluginChatToolRegistry pluginChatToolRegistry)
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
        _pluginChatToolRegistry = pluginChatToolRegistry;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        selectedProvider = _appSettingsStore.Get(ProviderSettingKey) is ProviderLmStudio ? ProviderLmStudio : ProviderOpenRouter;
        selectedModel = ModelSettingFor(selectedProvider) ?? DefaultModelFor(selectedProvider);
        selectedEffort = Enum.TryParse<ChatEffort>(_appSettingsStore.Get(EffortSettingKey), out var savedEffort)
            ? savedEffort
            : ChatEffort.None;
        isAutoMode = bool.TryParse(_appSettingsStore.Get(AutoModeSettingKey), out var savedAutoMode) && savedAutoMode;
        isPlanMode = bool.TryParse(_appSettingsStore.Get(PlanModeSettingKey), out var savedPlanMode) && savedPlanMode;

        AvailableModels = [];

        // Fire-and-forget for both providers: the picker fills in a moment later, and the current
        // SelectedModel (restored from settings) stays usable meanwhile since the combo is editable.
        _ = RefreshModelsAsync();
    }

    public ObservableCollection<ObservableObject> Messages { get; }  = [];

    /// <summary>The model picker's dropdown contents - whatever the current provider's /models
    /// endpoint last reported. Previously a hardcoded four-model list for OpenRouter, which went
    /// stale as models shipped and were retired (by the time it was replaced, one of the four no
    /// longer existed on OpenRouter at all, so picking it could only fail).</summary>
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

    /// <summary>What the assistant is doing right now (e.g. "Thinking...", "Calling read_file...") -
    /// shown next to an activity indicator so a long-running turn doesn't look identical to a stuck
    /// one. Null whenever IsStreaming is false.</summary>
    [ObservableProperty]
    private string? currentActivity;

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

    /// <summary>
    /// Persists the plain chat transcript (not edit/plan proposals or tool-call cards, matching what
    /// SendAsync itself replays back into the model on the next turn).
    ///
    /// Called eagerly - as soon as the user's message is added, and again after each tool-loop
    /// iteration - rather than only once the whole turn finishes. A long agentic turn can run for
    /// minutes, and saving only at the end meant a crash, a kill, or closing the app mid-turn lost
    /// the entire conversation, which is precisely what this is supposed to prevent. Writes are
    /// per-message/per-iteration, never per-token, so this stays cheap.
    ///
    /// Blank messages are skipped so the empty assistant placeholder that exists while a response is
    /// still streaming doesn't come back as a blank bubble on reload.
    /// </summary>
    public void PersistChatHistory()
    {
        if (_solutionDirectory is null)
        {
            return;
        }

        var stored = Messages
            .OfType<ChatMessageViewModel>()
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
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

        // Before the (potentially very long) network call, so the question itself survives even if
        // the response never arrives.
        PersistChatHistory();

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
        var systemPrompt = _appSettingsStore.Get(SystemPromptSettingKey);
        history.Insert(0, new ChatMessage(
            ChatRole.System,
            string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt));

        if (IsPlanMode)
        {
            history.Insert(0, new ChatMessage(
                ChatRole.System,
                "Plan Mode is active. Investigate the code first using read_file, list_files and search_codebase, " +
                "then call propose_plan exactly once with your implementation plan. Investigate the solution with " +
                "those tools only - do not use browser automation or documentation tools to explore the user's own " +
                "code. You cannot edit files in Plan Mode; propose_edit becomes available only once the user " +
                "approves your plan."));
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
    /// Streams the assistant's response, automatically executing any tool calls (workspace
    /// read-only tools and MCP tools alike) and feeding their results back for the model to
    /// continue, looping until a turn produces no further tool calls.
    ///
    /// A propose_plan call always ends the loop - a plan needs an explicit human Approve click no
    /// matter what. A propose_edit call ends the loop too, UNLESS Auto Mode is on and every edit
    /// proposed in that turn actually applied, in which case a synthetic note is appended to
    /// history (nothing else records that the edit happened - see below) and the loop continues, so
    /// an auto-accepted edit doesn't just silently end the conversation. In manual mode a proposal
    /// remains terminal until the user decides it, same as always.
    /// </summary>
    private async Task RunAssistantTurnsAsync(List<ChatMessage> history, ChatMessageViewModel assistantMessage)
    {
        IsStreaming = true;
        CurrentActivity = "Thinking...";
        _streamCancellation = new CancellationTokenSource();
        try
        {
            var maxIterations = IsAutoMode ? AutoModeMaxToolLoopIterations : MaxToolLoopIterations;
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var turn = await StreamOneTurnAsync(history, assistantMessage, _streamCancellation.Token);

                if (turn.Calls.Count > 0)
                {
                    history.Add(new ChatMessage(
                        ChatRole.Assistant,
                        assistantMessage.Text,
                        ToolCalls: turn.Calls.Select(c => new ChatToolCall(c.EffectiveId, c.Name!, c.Arguments.ToString())).ToList()));

                    foreach (var call in turn.Calls)
                    {
                        var callViewModel = new McpToolCallViewModel(call.Name!);
                        _dispatcher.Invoke(() =>
                        {
                            CurrentActivity = $"Calling {call.Name}...";
                            Messages.Add(callViewModel);
                        });

                        var (isError, text) = await ExecuteToolAsync(call, _streamCancellation.Token);
                        _dispatcher.Invoke(() => callViewModel.Complete(isError, text));

                        history.Add(new ChatMessage(ChatRole.Tool, text, ToolCallId: call.EffectiveId));
                    }

                    _dispatcher.Invoke(() => CurrentActivity = "Thinking...");
                }

                var action = AssistantTurnPolicy.Decide(
                    toolCallCount: turn.Calls.Count,
                    editProposalCount: turn.EditProposals.Count,
                    allEditProposalsApplied: turn.EditProposals.Count > 0 && turn.EditProposals.All(p => p.WasApplied),
                    producedPlanProposal: turn.ProducedPlanProposal,
                    isAutoMode: IsAutoMode);

                if (action == AssistantTurnAction.Stop)
                {
                    break;
                }

                if (action == AssistantTurnAction.ContinueWithAppliedEditsNote)
                {
                    // Nothing in `history` records the propose_edit call itself (it's rendered to
                    // Messages, never replayed as a tool call/tool result) - without this, the model
                    // has no way to know its edit actually landed, and no prompt to keep going.
                    var summary = string.Join("\n", turn.EditProposals.Select(p => $"- Applied edit to {p.FilePath}."));
                    history.Add(new ChatMessage(
                        ChatRole.User,
                        $"{summary}\n\nContinue with the rest of the task if there's more to do, or let the user know you're finished."));
                }

                // The assistant text and tool results from this iteration are now final - checkpoint
                // them before starting the next one, which may run for a long time.
                _dispatcher.Invoke(PersistChatHistory);

                assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
                _dispatcher.Invoke(() => Messages.Add(assistantMessage));
            }
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Invoke(() => assistantMessage.Append("\n\n[cancelled]"));
        }
        catch (Exception ex)
        {
            // Uncaught here, this would otherwise propagate out of a fire-and-forget
            // AsyncRelayCommand invocation (WPF's ICommand.Execute never awaits it) and become an
            // unobserved task exception - logged only once the faulted Task happens to be garbage
            // collected, which can be arbitrarily delayed relative to when the conversation actually
            // stopped. Logging and surfacing it here immediately is what makes a real failure look
            // different from the assistant simply finishing.
            Log.Error(ex, "Assistant turn failed unexpectedly");
            _dispatcher.Invoke(() =>
            {
                assistantMessage.Append($"\n\n[error: the assistant stopped unexpectedly - {ex.Message}]");
                StatusMessage = "The assistant stopped due to an unexpected error - see the message above.";
            });
        }
        finally
        {
            IsStreaming = false;
            CurrentActivity = null;
            _streamCancellation = null;
            PersistChatHistory();
        }
    }

    /// <summary>Routes a tool call to a plugin's chat tool, the local read-only workspace tools, or
    /// out to an MCP server.</summary>
    private async Task<(bool IsError, string Text)> ExecuteToolAsync(PendingMcpCall call, CancellationToken cancellationToken)
    {
        // Checked first, before the MCP fallback below - a plugin tool's qualified name
        // ("plugin__{PluginName}__{Name}") is never a workspace tool name and _mcpServerManager has
        // never heard of it either, so without this it would reach CallToolAsync and come back as
        // an unknown-tool error instead of actually running.
        if (_pluginChatToolRegistry.IsPluginTool(call.Name!))
        {
            if (!TryParseToolArguments(call.Arguments.ToString(), out var pluginArguments))
            {
                return (true, $"Malformed arguments JSON for '{call.Name}'.");
            }

            var pluginResult = await _pluginChatToolRegistry.ExecuteAsync(call.Name!, pluginArguments, cancellationToken);
            return (pluginResult.IsError, pluginResult.Text);
        }

        if (!WorkspaceTools.IsWorkspaceTool(call.Name!))
        {
            var mcpResult = await _mcpServerManager.CallToolAsync(call.Name!, call.Arguments.ToString(), cancellationToken);
            return (mcpResult.IsError, mcpResult.Text);
        }

        if (!TryParseToolArguments(call.Arguments.ToString(), out var arguments))
        {
            return (true, $"Malformed arguments JSON for '{call.Name}'.");
        }

        switch (call.Name)
        {
            case WorkspaceTools.ReadFileToolName:
                var file = arguments?["file"]?.GetValue<string>();
                return file is null
                    ? (true, "read_file requires a 'file' argument.")
                    : (false, _workspaceFileReader.ReadFile(_solutionDirectory, file));

            case WorkspaceTools.ListFilesToolName:
                var directory = arguments?["directory"]?.GetValue<string>();
                var recursive = arguments?["recursive"]?.GetValue<bool>() ?? false;
                return (false, _workspaceFileReader.ListFiles(_solutionDirectory, directory, recursive));

            case WorkspaceTools.SearchCodebaseToolName:
                var query = arguments?["query"]?.GetValue<string>();
                if (query is null)
                {
                    return (true, "search_codebase requires a 'query' argument.");
                }

                if (!_codebaseIndexService.IsIndexed)
                {
                    return (true, "The codebase isn't indexed yet - use read_file/list_files instead, " +
                                  "or ask the user to click 'Index Codebase' in the Copilot Chat pane.");
                }

                var hits = await _codebaseIndexService.SearchAsync(query, CodebaseSearchTopK, cancellationToken);
                return hits.Count == 0
                    ? (false, "No matches found.")
                    : (false, string.Join("\n\n", hits.Select(c => $"// {c.FilePath}:{c.StartLine}-{c.EndLine}\n{c.Text}")));

            default:
                return (true, $"Unknown workspace tool '{call.Name}'.");
        }
    }

    /// <summary>Shared by both the plugin-tool and workspace-tool branches of <see cref="ExecuteToolAsync"/> -
    /// empty/whitespace arguments (a tool with no parameters) parse to null rather than failing.</summary>
    private static bool TryParseToolArguments(string argumentsJson, out JsonNode? arguments)
    {
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson) ? null : JsonNode.Parse(argumentsJson);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            arguments = null;
            return false;
        }
    }

    private sealed record TurnResult(List<PendingMcpCall> Calls, List<EditProposalViewModel> EditProposals, bool ProducedPlanProposal);

    private async Task<TurnResult> StreamOneTurnAsync(
        List<ChatMessage> history, ChatMessageViewModel assistantMessage, CancellationToken cancellationToken)
    {
        var proposalParser = new EditProposalParser();
        var proposalsByToolCallId = new Dictionary<string, EditProposalViewModel>();
        var planParser = new PlanProposalParser();
        var planProposalsByToolCallId = new Dictionary<string, PlanProposalViewModel>();
        var mcpCallsByIndex = new Dictionary<int, PendingMcpCall>();
        var routeByIndex = new Dictionary<int, ToolCallRoute>();

        var tools = new List<ToolDefinition> { IsPlanMode ? ProposePlanTool.Definition : ProposeEditTool.Definition };
        tools.AddRange(WorkspaceTools.All);
        tools.AddRange(_mcpServerManager.GetToolDefinitions());
        tools.AddRange(_pluginChatToolRegistry.GetToolDefinitions());

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

                    case ReasoningDelta reasoning:
                        assistantMessage.AppendReasoning(reasoning.Text);
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

                    // "length" means the model was cut off by the output-token cap, not that it
                    // chose to stop - without flagging this, a truncated turn (e.g. one that got cut
                    // off partway through a verbose plan, before ever reaching a tool call) looks
                    // identical to a normal, complete reply.
                    case FinishReasonReceived { Reason: "length" }:
                        assistantMessage.Append("\n\n[response was cut off - it hit the model's output length limit before finishing]");
                        break;

                    case UsageReceived usage:
                        TotalPromptTokens += usage.PromptTokens;
                        TotalCompletionTokens += usage.CompletionTokens;
                        break;
                }
            });
        }

        return new TurnResult(
            mcpCallsByIndex.Values.Where(c => c.Name is not null).ToList(),
            proposalsByToolCallId.Values.Where(p => p.IsComplete).ToList(),
            planProposalsByToolCallId.Count > 0);
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

    /// <summary>
    /// Repopulates the model picker from the current provider's /models endpoint. OpenRouter's list
    /// is filtered to tool-capable models: every edit this IDE makes goes through a tool call, so a
    /// model that can't call tools can only ever narrate, which is indistinguishable from an IDE bug
    /// unless the picker rules those models out up front.
    ///
    /// A failed fetch leaves the existing list alone rather than blanking it - the combo box is
    /// editable, so a model id that's already selected (or typed by hand) keeps working with no
    /// network round-trip at all.
    /// </summary>
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        var provider = SelectedProvider;
        IsRefreshingModels = true;
        try
        {
            var models = provider == ProviderLmStudio
                ? await _lmStudioClient.GetAvailableModelsAsync()
                : await _openRouterClient.GetAvailableModelsAsync();

            // The user can switch providers while a fetch is in flight; dropping the stale result
            // avoids briefly showing (or auto-selecting) the other provider's models.
            if (provider != SelectedProvider)
            {
                return;
            }

            if (models.Count == 0)
            {
                StatusMessage = provider == ProviderLmStudio
                    ? "Couldn't reach LM Studio - check the base URL in Tools > Settings and that it's running."
                    : "Couldn't fetch the model list from OpenRouter - you can still type a model id directly.";
                return;
            }

            AvailableModels.Clear();
            foreach (var model in models)
            {
                AvailableModels.Add(model);
            }

            // Only pick for the user when what's selected isn't actually offered - otherwise a
            // refresh would silently move them off the model they deliberately chose.
            if (string.IsNullOrWhiteSpace(SelectedModel) || !models.Contains(SelectedModel))
            {
                SelectedModel = models.Contains(DefaultModelFor(provider)) ? DefaultModelFor(provider) : models[0];
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

        AvailableModels.Clear();
        _ = RefreshModelsAsync();
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
