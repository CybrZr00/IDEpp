using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class CopilotChatPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "CopilotChat";

    private const int CodebaseSearchTopK = 5;

    private readonly IChatClient _chatClient;
    private readonly SecureSettingsStore _settingsStore;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly EditApplicationService _editApplicationService;
    private readonly ProjectMemoryStore _projectMemoryStore;
    private readonly CodebaseIndexService _codebaseIndexService;
    private readonly Dispatcher _dispatcher;
    private DocumentViewModel? _currentDocument;
    private string? _solutionDirectory;
    private IReadOnlyList<string> _indexableFiles = [];
    private CancellationTokenSource? _streamCancellation;

    public CopilotChatPaneViewModel(
        IChatClient chatClient,
        SecureSettingsStore settingsStore,
        AppSettingsStore appSettingsStore,
        EditApplicationService editApplicationService,
        ProjectMemoryStore projectMemoryStore,
        CodebaseIndexService codebaseIndexService)
        : base(PaneContentId, "Copilot Chat")
    {
        _chatClient = chatClient;
        _settingsStore = settingsStore;
        _appSettingsStore = appSettingsStore;
        _editApplicationService = editApplicationService;
        _projectMemoryStore = projectMemoryStore;
        _codebaseIndexService = codebaseIndexService;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        HasApiKey = !string.IsNullOrWhiteSpace(_settingsStore.GetSecret(OpenRouterChatClient.ApiKeySettingName));
        HasDeepSeekApiKey = !string.IsNullOrWhiteSpace(_settingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName));

        LmStudioBaseUrl = _appSettingsStore.Get(LmStudioEmbeddingClient.BaseUrlSettingName) ?? LmStudioEmbeddingClient.DefaultBaseUrl;
        LmStudioModel = _appSettingsStore.Get(LmStudioEmbeddingClient.ModelSettingName) ?? "";
    }

    public ObservableCollection<ObservableObject> Messages { get; } = [];

    public ObservableCollection<string> AvailableModels { get; } =
    [
        "anthropic/claude-sonnet-4.5",
        "openai/gpt-5",
        "deepseek/deepseek-v4-pro",
        "google/gemini-3-pro",
    ];

    [ObservableProperty]
    private string selectedModel = "anthropic/claude-sonnet-4.5";

    [ObservableProperty]
    private string inputText = "";

    [ObservableProperty]
    private bool includeCurrentFileContext = true;

    [ObservableProperty]
    private bool useCodebaseSearch;

    [ObservableProperty]
    private bool hasApiKey;

    [ObservableProperty]
    private bool hasDeepSeekApiKey;

    [ObservableProperty]
    private string lmStudioBaseUrl;

    [ObservableProperty]
    private string lmStudioModel;

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

    public void SaveApiKey(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            return;
        }

        _settingsStore.SetSecret(OpenRouterChatClient.ApiKeySettingName, plaintextKey.Trim());
        HasApiKey = true;
        StatusMessage = "API key saved.";
    }

    public void SaveDeepSeekApiKey(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            return;
        }

        _settingsStore.SetSecret(DeepSeekFimClient.ApiKeySettingName, plaintextKey.Trim());
        HasDeepSeekApiKey = true;
        StatusMessage = "DeepSeek API key saved.";
    }

    [RelayCommand]
    private void SaveLmStudioSettings()
    {
        _appSettingsStore.Set(LmStudioEmbeddingClient.BaseUrlSettingName, LmStudioBaseUrl.Trim());
        _appSettingsStore.Set(LmStudioEmbeddingClient.ModelSettingName, LmStudioModel.Trim());
        StatusMessage = "LM Studio settings saved.";
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

        await StreamAssistantResponseAsync(history, assistantMessage);
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

    private async Task StreamAssistantResponseAsync(List<ChatMessage> history, ChatMessageViewModel assistantMessage)
    {
        var proposalParser = new EditProposalParser();
        var proposalsByToolCallId = new Dictionary<string, EditProposalViewModel>();

        IsStreaming = true;
        _streamCancellation = new CancellationTokenSource();
        try
        {
            await foreach (var streamEvent in _chatClient.StreamChatAsync(
                               history, SelectedModel, [ProposeEditTool.Definition], _streamCancellation.Token))
            {
                _dispatcher.Invoke(() =>
                {
                    switch (streamEvent)
                    {
                        case TextDelta delta:
                            assistantMessage.Append(delta.Text);
                            break;

                        case ToolCallDelta toolCallDelta:
                            HandleToolCallDelta(toolCallDelta, proposalParser, proposalsByToolCallId);
                            break;

                        case StreamError error:
                            assistantMessage.Append($"\n\n[error: {error.Message}]");
                            break;
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Append("\n\n[cancelled]");
        }
        finally
        {
            IsStreaming = false;
            _streamCancellation = null;
        }
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

        await StreamAssistantResponseAsync(history, assistantMessage);
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

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SaveMemoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsIndexingChanged(bool value) => IndexCodebaseCommand.NotifyCanExecuteChanged();

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
}
