using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.LmStudio;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Roslyn;
using IdePlusPlus.Shell;
using IdePlusPlus.SourceControl;
using IdePlusPlus.TestRunner;
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
    private const string ShellCommandsSettingKey = "ChatShellCommands";
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
        "You can read files, list directories, search the codebase, look up C# symbols and their " +
        "references, edit files directly in the user's editor, check a file for compiler errors, build " +
        "the solution, run its tests, and inspect git - plus use any MCP or plugin tools currently " +
        "connected.\n\n" +
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
        "- Before changing anything shared, find_references shows every caller. A signature change " +
        "that compiles where you edited it can still break code you never read. find_symbol beats " +
        "search_codebase when you know a name: it is a compiler-accurate answer, not a similarity guess.\n" +
        "- After changing code, check it. get_diagnostics is fast and covers one file, so use it as you " +
        "go; build_solution catches what your change broke elsewhere; run_tests says whether behaviour " +
        "still holds; git_diff shows what your edits actually did. An edit result telling you it applied " +
        "is not the same as the change being correct.\n" +
        "- Finish on a green build. If it fails, fix it and build again rather than handing back " +
        "something you know is broken - and if you cannot, say plainly what is still wrong.\n" +
        "- Keep narration short. One line on what you're doing beats a paragraph on what you're about to do.";

    /// <summary>Long enough for a cold build of a large solution, short enough that a wedged one
    /// doesn't hold the conversation open indefinitely.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Longer than the build timeout: a whole-solution run legitimately takes minutes, and
    /// killing a nearly-finished suite wastes more than it saves.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Shorter than the build and test timeouts on purpose: those are known-slow
    /// operations, an ad-hoc command that runs for two minutes is usually one that is stuck.</summary>
    private static readonly TimeSpan ShellCommandTimeout = TimeSpan.FromMinutes(2);

    private const int CodebaseSearchTopK = 5;

    // Raised from 8 once iterations started doing verification work. Eight was sized for a loop
    // whose only tools read files; a single "build, read the errors, fix, build again" cycle spends
    // four or five on its own, and running out mid-fix leaves the code worse than not having tried.
    // An edit proposal still ends the turn in manual mode, so this only bounds read/build/git calls.
    private const int MaxToolLoopIterations = 20;

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
    private readonly GitService _gitService;
    private readonly BuildService _buildService;
    private readonly TestRunService _testRunService;
    private readonly RoslynWorkspaceService _roslynWorkspaceService;
    private readonly ShellCommandRunner _shellCommandRunner = new();

    /// <summary>Commands the user has already approved this session, so an agentic loop that runs
    /// the same command twice asks once. Keyed on the exact command text - a different command is a
    /// different decision.</summary>
    private readonly HashSet<string> _approvedCommands = new(StringComparer.Ordinal);
    private readonly WorkspaceFileReader _workspaceFileReader;
    private readonly Dispatcher _dispatcher;
    private DocumentViewModel? _currentDocument;
    private string? _solutionDirectory;
    private string? _solutionPath;
    private Func<string>? _currentConfiguration;
    private IReadOnlyList<string> _indexableFiles = [];
    private IReadOnlyList<string> _testProjectPaths = [];
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
        PluginChatToolRegistry pluginChatToolRegistry,
        GitService gitService,
        BuildService buildService,
        TestRunService testRunService,
        RoslynWorkspaceService roslynWorkspaceService)
        : base(PaneContentId, "Copilot Chat")
    {
        _openRouterClient = openRouterClient;
        _lmStudioClient = lmStudioClient;
        _appSettingsStore = appSettingsStore;
        _editApplicationService = editApplicationService;
        _workspaceFileReader = new WorkspaceFileReader(_editApplicationService.TryGetUnsavedText);
        _projectMemoryStore = projectMemoryStore;
        _chatHistoryStore = chatHistoryStore;
        _codebaseIndexService = codebaseIndexService;
        _mcpServerManager = mcpServerManager;
        _pluginChatToolRegistry = pluginChatToolRegistry;
        _gitService = gitService;
        _buildService = buildService;
        _testRunService = testRunService;
        _roslynWorkspaceService = roslynWorkspaceService;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        selectedProvider = _appSettingsStore.Get(ProviderSettingKey) is ProviderLmStudio ? ProviderLmStudio : ProviderOpenRouter;
        selectedModel = ModelSettingFor(selectedProvider) ?? DefaultModelFor(selectedProvider);
        selectedEffort = Enum.TryParse<ChatEffort>(_appSettingsStore.Get(EffortSettingKey), out var savedEffort)
            ? savedEffort
            : ChatEffort.None;
        isAutoMode = bool.TryParse(_appSettingsStore.Get(AutoModeSettingKey), out var savedAutoMode) && savedAutoMode;
        areShellCommandsEnabled =
            bool.TryParse(_appSettingsStore.Get(ShellCommandsSettingKey), out var savedShell) && savedShell;
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

    /// <summary>Off unless the user turns it on, and reset is their call - this is the only tool
    /// that can run arbitrary code, so it should never be on because it was on last time by
    /// accident. Persisted like the other toggles, but see the warning banner beside it.</summary>
    [ObservableProperty]
    private bool areShellCommandsEnabled;

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

    /// <summary>
    /// Asked before each distinct run_command, with the exact command line and the model's stated
    /// reason. Unset, nothing runs - an unattended process must not approve arbitrary code on the
    /// user's behalf, the same rule McpServerManager's project-trust prompt follows.
    /// </summary>
    public Func<string, string, Task<bool>>? ShellCommandApprovalPrompt { get; set; }

    public void SetActiveDocument(DocumentViewModel? document) => _currentDocument = document;

    /// <summary>
    /// The open solution, as the copilot's tools need to see it.
    ///
    /// A directory alone was enough while every tool only read files. Building needs the solution
    /// file itself, and the configuration the user has selected - which they can change at any
    /// time, hence a callback rather than a snapshot taken at solution load.
    /// </summary>
    public void SetSolutionContext(string? solutionPath, string? directory, Func<string>? currentConfiguration)
    {
        _solutionPath = solutionPath;
        _currentConfiguration = currentConfiguration;
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

    /// <summary>The solution's test projects, so run_tests can default to all of them. Detected
    /// during solution load (see TestProjectDetector); the chat pane never sees the solution tree
    /// itself.</summary>
    public void SetTestProjects(IReadOnlyList<string> projectPaths) => _testProjectPaths = projectPaths;

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
    /// proposed in that turn actually applied, in which case the loop continues so an auto-accepted
    /// edit doesn't just silently end the conversation. In manual mode a proposal remains terminal
    /// until the user decides it, same as always.
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

                // propose_edit is replayed here alongside the read-only and MCP calls, so the
                // model sees its own edits as tool calls with tool results. Before this, an
                // edit-only iteration recorded nothing at all - not even the assistant's own
                // narration, since this whole block was gated on turn.Calls.Count - and the model
                // was told only "- Applied edit to {path}." by a synthetic user message. It could
                // not tell an edit that landed from one that was refused, nor notice that it had
                // just removed most of a file.
                if (turn.Calls.Count > 0 || turn.EditProposals.Count > 0)
                {
                    var toolCalls = turn.Calls
                        .Select(c => new ChatToolCall(c.EffectiveId, c.Name!, c.Arguments.ToString()))
                        .Concat(turn.EditProposals.Select(p => new ChatToolCall(
                            p.ToolCallId, ProposeEditTool.ToolName, p.ArgumentsJson ?? "{}")))
                        .ToList();

                    history.Add(new ChatMessage(ChatRole.Assistant, assistantMessage.Text, ToolCalls: toolCalls));

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

                    // Every replayed call needs an answer or the next request is malformed, so this
                    // runs for refused and undecided proposals too - ToolResultText always says
                    // something true about where the edit got to.
                    foreach (var proposal in turn.EditProposals)
                    {
                        history.Add(new ChatMessage(
                            ChatRole.Tool, proposal.ToolResultText, ToolCallId: proposal.ToolCallId));
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

        // Routed off the same list that was sent to the model, so a built-in tool can never be
        // advertised and then quietly forwarded to MCP as an unknown tool.
        if (!BuiltInTools.IsBuiltIn(call.Name!))
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
                    : (false, _workspaceFileReader.ReadFile(
                        _solutionDirectory,
                        file,
                        arguments?["offset"]?.GetValue<int>(),
                        arguments?["limit"]?.GetValue<int>()));

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

            case GitTools.DiffToolName:
                return await RunGitDiffAsync(
                    arguments?["path"]?.GetValue<string>(),
                    arguments?["staged"]?.GetValue<bool>() ?? false,
                    cancellationToken);

            case GitTools.StatusToolName:
                return await RunGitStatusAsync(cancellationToken);

            case BuildTools.BuildToolName:
                return await RunBuildAsync(arguments?["project"]?.GetValue<string>(), cancellationToken);

            case ShellTools.RunCommandToolName:
                return await RunShellCommandAsync(
                    arguments?["command"]?.GetValue<string>(),
                    arguments?["reason"]?.GetValue<string>(),
                    cancellationToken);

            case SymbolTools.FindSymbolToolName:
                var symbolName = arguments?["name"]?.GetValue<string>();
                return symbolName is null
                    ? (true, "find_symbol requires a 'name' argument.")
                    : await RunFindSymbolAsync(symbolName, cancellationToken);

            case SymbolTools.FindReferencesToolName:
                var referenceName = arguments?["name"]?.GetValue<string>();
                return referenceName is null
                    ? (true, "find_references requires a 'name' argument.")
                    : await RunFindReferencesAsync(referenceName, cancellationToken);

            case DiagnosticTools.GetDiagnosticsToolName:
                var diagnosticFile = arguments?["file"]?.GetValue<string>();
                return diagnosticFile is null
                    ? (true, "get_diagnostics requires a 'file' argument.")
                    : await RunGetDiagnosticsAsync(diagnosticFile, cancellationToken);

            case TestTools.RunTestsToolName:
                return await RunTestsAsync(
                    arguments?["project"]?.GetValue<string>(),
                    arguments?["filter"]?.GetValue<string>(),
                    cancellationToken);

            default:
                return (true, $"Unknown built-in tool '{call.Name}'.");
        }
    }

    private async Task<(bool IsError, string Text)> RunGitDiffAsync(
        string? path, bool staged, CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there is no repository to diff.");
        }

        // The model chooses this path, so it gets the same containment treatment as a read.
        if (path is not null && WorkspaceFileReader.ResolveWithinRoot(_solutionDirectory, path) is null)
        {
            return (true, $"Refused: '{path}' resolves outside the open solution directory.");
        }

        var (success, output) = await _gitService.GetDiffAsync(
            _solutionDirectory, path, staged, cancellationToken);

        if (!success)
        {
            return (true, $"git diff failed: {output}");
        }

        if (output.Length == 0)
        {
            return (false, staged
                ? "No staged changes."
                : "No unstaged changes. Note that a diff never lists untracked files - use git_status for those.");
        }

        return (false, Cap(output, GitTools.MaxOutputChars, "diff"));
    }

    private async Task<(bool IsError, string Text)> RunGitStatusAsync(CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there is no repository to inspect.");
        }

        var (success, output) = await _gitService.GetShortStatusAsync(_solutionDirectory, cancellationToken);
        if (!success)
        {
            return (true, $"git status failed: {output}");
        }

        return (false, output.Length == 0 ? "Working tree clean." : Cap(output, GitTools.MaxOutputChars, "status"));
    }

    /// <summary>
    /// Builds and reports back what the compiler said.
    ///
    /// Unsaved documents are written out first. An applied edit to an open file only touches its
    /// buffer, and the compiler reads disk - so without saving, the build describes source the
    /// agent has already changed. That is worse than no build at all, because it looks authoritative.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunBuildAsync(
        string? project, CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null || _solutionPath is null)
        {
            return (true, "No solution is open, so there is nothing to build.");
        }

        var target = _solutionPath;
        if (!string.IsNullOrWhiteSpace(project))
        {
            if (WorkspaceFileReader.ResolveWithinRoot(_solutionDirectory, project) is not { } projectPath)
            {
                return (true, $"Refused: '{project}' resolves outside the open solution directory.");
            }

            if (!File.Exists(projectPath))
            {
                return (true, $"Project not found: {project}");
            }

            target = projectPath;
        }

        if (_buildService.IsRunning)
        {
            return (true, "A build is already running - wait for it to finish before starting another.");
        }

        var saved = _dispatcher.Invoke(_editApplicationService.SaveDirtyDocuments);

        var configuration = _currentConfiguration?.Invoke() ?? "Debug";
        var outputLines = new List<string>();

        // The tool loop's only cancellation is the Stop button, so a wedged build would otherwise
        // hang the conversation indefinitely. Safe to enforce now that cancelling actually kills
        // the process tree.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BuildTimeout);

        int exitCode;
        try
        {
            exitCode = await _buildService.BuildAsync(
                target, line => { lock (outputLines) { outputLines.Add(line); } },
                configuration: configuration, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (true, $"The build was still running after {BuildTimeout.TotalMinutes:0} minutes and was stopped.");
        }
        catch (InvalidOperationException ex)
        {
            return (true, ex.Message);
        }

        return (false, FormatBuildReport(exitCode, configuration, saved, outputLines));
    }

    /// <summary>
    /// Runs an arbitrary command, once the user has said so.
    ///
    /// The confirmation is the whole safety model, so it is per distinct command rather than a
    /// blanket "shell: on". A model that reads a hostile README can propose anything it likes; what
    /// it cannot do is run it without the exact command line being shown to a person first.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunShellCommandAsync(
        string? command, string? reason, CancellationToken cancellationToken)
    {
        if (!AreShellCommandsEnabled)
        {
            return (true, "Shell commands are turned off. The user can enable them in the Copilot Chat pane.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return (true, "run_command requires a 'command' argument.");
        }

        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there is no directory to run a command in.");
        }

        if (!_approvedCommands.Contains(command))
        {
            if (ShellCommandApprovalPrompt is not { } prompt)
            {
                return (true, "Shell commands cannot be approved in this session, so nothing was run.");
            }

            if (!await prompt(command, reason ?? "(no reason given)"))
            {
                return (true,
                    "The user declined to run that command. Do not try to run it again, or work around "
                    + "the refusal with a different command that does the same thing - explain what you "
                    + "wanted it for instead.");
            }

            _approvedCommands.Add(command);
        }

        ShellCommandResult result;
        try
        {
            result = await _shellCommandRunner.RunAsync(
                command, _solutionDirectory, ShellCommandTimeout, ShellTools.MaxOutputChars, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, $"Could not run the command: {ex.Message}");
        }

        if (result.TimedOut)
        {
            return (true,
                $"The command was still running after {ShellCommandTimeout.TotalMinutes:0} minute(s) and was "
                + $"stopped.{(result.Output.Length > 0 ? $" Output so far:\n{result.Output}" : "")}");
        }

        var header = result.ExitCode == 0
            ? "Command succeeded (exit code 0)."
            : $"Command FAILED with exit code {result.ExitCode}.";

        return (false, result.Output.Length > 0 ? $"{header}\n\n{result.Output}" : $"{header} (no output)");
    }

    private async Task<(bool IsError, string Text)> RunFindSymbolAsync(
        string name, CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there are no symbols to search.");
        }

        IReadOnlyList<SymbolLocation> symbols;
        try
        {
            symbols = await _roslynWorkspaceService.FindSymbolsAsync(name, SymbolTools.MaxResults, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, $"Symbol search failed: {ex.Message}");
        }

        if (symbols.Count == 0)
        {
            return (false,
                $"No C# symbol named '{name}' was found. The match is exact and case-sensitive on the "
                + "symbol's own name; try search_codebase if you are not sure what it is called.");
        }

        return (false, $"{symbols.Count} declaration(s) of '{name}':\n" + FormatSymbols(symbols));
    }

    private async Task<(bool IsError, string Text)> RunFindReferencesAsync(
        string name, CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there are no references to search.");
        }

        IReadOnlyList<SymbolLocation> declarations;
        IReadOnlyList<SymbolLocation> references;
        int totalReferences;
        try
        {
            (declarations, references, totalReferences) =
                await _roslynWorkspaceService.FindReferencesAsync(name, SymbolTools.MaxResults, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, $"Reference search failed: {ex.Message}");
        }

        if (declarations.Count == 0)
        {
            return (false, $"No C# symbol named '{name}' was found, so it has no references.");
        }

        // More than one declaration means more than one symbol shares this name - an overload set,
        // or unrelated types with a same-named member. Picking one would answer a question the
        // caller did not ask.
        if (references.Count == 0 && totalReferences == 0 && declarations.Count > 1)
        {
            return (false,
                $"'{name}' is ambiguous - {declarations.Count} declarations share that name, so references "
                + "were not resolved. Narrow it down from these:\n" + FormatSymbols(declarations));
        }

        if (totalReferences == 0)
        {
            return (false,
                $"'{name}' is declared but never referenced:\n" + FormatSymbols(declarations));
        }

        var report = new StringBuilder($"'{name}' has {totalReferences} reference(s).\n\nDeclared at:\n");
        report.Append(FormatSymbols(declarations));
        report.Append("\n\nReferenced at:\n");
        report.Append(FormatSymbols(references));

        if (totalReferences > references.Count)
        {
            report.Append($"\n\n[{totalReferences - references.Count} more reference(s) not shown]");
        }

        return (false, report.ToString());
    }

    /// <summary>Paths are made relative to the solution so they can be handed straight back to
    /// read_file, and the source line is included so a list of hits is readable without opening
    /// every one of them.</summary>
    private string FormatSymbols(IReadOnlyList<SymbolLocation> symbols) =>
        string.Join("\n", symbols.Select(s =>
        {
            var path = _solutionDirectory is null
                ? s.FilePath
                : Path.GetRelativePath(_solutionDirectory, s.FilePath).Replace('\\', '/');
            return $"  {path}:{s.Line}:{s.Column}  {s.Kind} {s.DisplayName}"
                   + (s.LineText.Length > 0 ? $"\n      {s.LineText}" : "");
        }));

    /// <summary>
    /// Compiler errors and warnings for one file, from the already-loaded Roslyn semantic model.
    ///
    /// Reads the editor's buffer rather than disk where the file is open, so a file edited but not
    /// saved is analysed as it stands - the same reason read_file does. Unlike build_solution this
    /// deliberately does not save anything: it is meant to be cheap enough to call after every edit,
    /// and writing to disk as a side effect of a read-only question would be a surprise.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunGetDiagnosticsAsync(
        string file, CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there is nothing to analyse.");
        }

        if (WorkspaceFileReader.ResolveWithinRoot(_solutionDirectory, file) is not { } fullPath)
        {
            return (true, $"Refused: '{file}' resolves outside the open solution directory.");
        }

        if (!File.Exists(fullPath))
        {
            return (true, $"File not found: {file}");
        }

        if (!_roslynWorkspaceService.IsAnalysed(fullPath))
        {
            return (true,
                $"{file} is not part of the loaded C# solution, so it has no compiler diagnostics. "
                + "This covers non-C# files, and C# files added since the solution was opened. "
                + "Use build_solution instead.");
        }

        var unsavedText = _editApplicationService.TryGetUnsavedText(fullPath);

        IReadOnlyList<LiveDiagnostic> diagnostics;
        try
        {
            diagnostics = await _roslynWorkspaceService.GetDiagnosticsAsync(fullPath, unsavedText, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (true, $"Could not analyse {file}: {ex.Message}");
        }

        var errors = diagnostics.Where(d => d.Severity == "Error").ToList();
        var warnings = diagnostics.Where(d => d.Severity == "Warning").ToList();

        if (errors.Count == 0 && warnings.Count == 0)
        {
            return (false, $"{file}: no errors or warnings."
                           + (unsavedText is null ? "" : " (Analysed with your unsaved changes.)"));
        }

        var report = new StringBuilder($"{file}: {errors.Count} error(s), {warnings.Count} warning(s).");
        if (unsavedText is not null)
        {
            report.Append(" Analysed with your unsaved changes.");
        }

        foreach (var diagnostic in errors.Concat(warnings).Take(DiagnosticTools.MaxReportedDiagnostics))
        {
            report.Append(
                $"\n({diagnostic.Line},{diagnostic.Column}): "
                + $"{diagnostic.Severity.ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}");
        }

        var total = errors.Count + warnings.Count;
        if (total > DiagnosticTools.MaxReportedDiagnostics)
        {
            report.Append($"\n\n[{total - DiagnosticTools.MaxReportedDiagnostics} more not shown]");
        }

        return (false, report.ToString());
    }

    /// <summary>
    /// Runs the tests and reports the failures.
    ///
    /// Documents are saved first for the same reason a build saves them - `dotnet test` builds from
    /// disk, so an unsaved edit would be tested in its pre-edit form and pass or fail for the wrong
    /// reasons.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunTestsAsync(
        string? project, string? filter, CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there are no tests to run.");
        }

        IReadOnlyList<string> projects;
        if (!string.IsNullOrWhiteSpace(project))
        {
            if (WorkspaceFileReader.ResolveWithinRoot(_solutionDirectory, project) is not { } projectPath)
            {
                return (true, $"Refused: '{project}' resolves outside the open solution directory.");
            }

            if (!File.Exists(projectPath))
            {
                return (true, $"Project not found: {project}");
            }

            projects = [projectPath];
        }
        else
        {
            if (_testProjectPaths.Count == 0)
            {
                return (true, "No test projects were found in this solution.");
            }

            projects = _testProjectPaths;
        }

        if (_testRunService.IsRunning)
        {
            return (true, "A test run is already in progress - wait for it to finish before starting another.");
        }

        _dispatcher.Invoke(_editApplicationService.SaveDirtyDocuments);

        var filters = string.IsNullOrWhiteSpace(filter) ? null : new[] { filter };
        var report = new StringBuilder();
        var totals = (Passed: 0, Failed: 0, Skipped: 0);
        var failures = new List<TestResult>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);

        foreach (var projectPath in projects)
        {
            IReadOnlyList<TestResult> results;
            try
            {
                results = await _testRunService.RunAsync(projectPath, filters, _ => { }, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (true,
                    $"The test run was still going after {TestTimeout.TotalMinutes:0} minutes and was stopped. "
                    + "Narrow it with 'project' or 'filter'.");
            }
            catch (InvalidOperationException ex)
            {
                return (true, ex.Message);
            }

            var name = Path.GetFileNameWithoutExtension(projectPath);
            if (results.Count == 0)
            {
                report.Append($"\n{name}: no tests ran (the project may have failed to build, or the filter matched nothing).");
                continue;
            }

            var passed = results.Count(r => r.Outcome == TestOutcome.Passed);
            var failed = results.Count(r => r.Outcome == TestOutcome.Failed);
            var skipped = results.Count(r => r.Outcome == TestOutcome.Skipped);
            totals = (totals.Passed + passed, totals.Failed + failed, totals.Skipped + skipped);
            failures.AddRange(results.Where(r => r.Outcome == TestOutcome.Failed));

            report.Append($"\n{name}: {passed} passed, {failed} failed, {skipped} skipped.");
        }

        var headline = totals.Failed == 0
            ? $"All {totals.Passed} test(s) passed."
            : $"{totals.Failed} test(s) FAILED, {totals.Passed} passed.";
        if (totals.Skipped > 0)
        {
            headline += $" {totals.Skipped} skipped.";
        }

        foreach (var failure in failures.Take(TestTools.MaxReportedFailures))
        {
            report.Append($"\n\nFAILED {failure.FullyQualifiedName}");
            if (failure.ErrorMessage is { Length: > 0 } message)
            {
                report.Append($"\n{Cap(message, TestTools.MaxFailureMessageChars, "message")}");
            }
        }

        if (failures.Count > TestTools.MaxReportedFailures)
        {
            report.Append($"\n\n[{failures.Count - TestTools.MaxReportedFailures} more failure(s) not shown]");
        }

        return (false, headline + report);
    }

    private static string FormatBuildReport(
        int exitCode, string configuration, IReadOnlyList<string> saved, List<string> outputLines)
    {
        var diagnostics = outputLines
            .Select(BuildDiagnosticParser.TryParse)
            .OfType<BuildDiagnostic>()
            .ToList();

        var errors = diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Error).ToList();
        var warnings = diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Warning).ToList();

        var report = new StringBuilder();
        report.Append(exitCode == 0 ? $"Build succeeded ({configuration})." : $"Build FAILED ({configuration}), exit code {exitCode}.");
        report.Append($" {errors.Count} error(s), {warnings.Count} warning(s).");

        if (saved.Count > 0)
        {
            report.Append($" Saved {saved.Count} file(s) with unsaved changes first.");
        }

        // Errors first and in full; warnings only fill whatever room is left, since a build that
        // failed is about its errors.
        var reported = errors.Concat(warnings).Take(BuildTools.MaxReportedDiagnostics).ToList();
        foreach (var diagnostic in reported)
        {
            var severity = diagnostic.Severity == BuildDiagnosticSeverity.Error ? "error" : "warning";
            report.Append(
                $"\n{diagnostic.FilePath}({diagnostic.Line},{diagnostic.Column}): {severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        if (diagnostics.Count > reported.Count)
        {
            report.Append($"\n\n[{diagnostics.Count - reported.Count} more diagnostic(s) not shown]");
        }

        // BuildDiagnosticParser only matches MSBuild's "path(line,col): severity CODE:" form, so a
        // failure from MSBuild itself (MSB####), the SDK (NETSDK####), or a missing project parses
        // to nothing at all. Falling back to the raw tail is what keeps those from being reported
        // as "0 errors" on a failed build.
        if (exitCode != 0 && errors.Count == 0)
        {
            var raw = string.Join("\n", outputLines.TakeLast(60));
            if (raw.Length > BuildTools.MaxRawOutputChars)
            {
                raw = raw[^BuildTools.MaxRawOutputChars..];
            }

            report.Append($"\n\nNo compiler diagnostics were parsed. Raw build output (tail):\n{raw}");
        }

        return report.ToString();
    }

    /// <summary>Length reported from the original text, not the cut copy, so the notice says how
    /// much there actually was.</summary>
    private static string Cap(string text, int maxChars, string what) =>
        text.Length <= maxChars
            ? text
            : text[..maxChars] + $"\n\n[truncated - the {what} is {text.Length} chars, showing the first {maxChars}. "
                                 + "Pass a 'path' to narrow it down.]";

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
        tools.AddRange(BuiltInTools.All);
        if (AreShellCommandsEnabled)
        {
            tools.AddRange(BuiltInTools.OptIn);
        }
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

            // Auto Mode applies edits with nobody looking, which is fine for the ordinary case and
            // not fine for an edit that removes most of a file - the exact shape silent truncation
            // takes. Held for a human instead: the proposal stays undecided with its Accept button
            // live, so confirming is one click rather than a re-run, and the model is told why.
            if (IsAutoMode && !proposal.IsLargeDeletion)
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

    partial void OnAreShellCommandsEnabledChanged(bool value)
    {
        _appSettingsStore.Set(ShellCommandsSettingKey, value.ToString());

        // Turning it off drops the approvals too, so re-enabling later starts from a clean slate
        // rather than silently inheriting decisions made in a different context.
        if (!value)
        {
            _approvedCommands.Clear();
        }
    }

    partial void OnIsPlanModeChanged(bool value) => _appSettingsStore.Set(PlanModeSettingKey, value.ToString());

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SaveMemoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsIndexingChanged(bool value) => IndexCodebaseCommand.NotifyCanExecuteChanged();

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
}
