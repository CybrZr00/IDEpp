using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class CopilotChatPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "CopilotChat";

    private readonly IChatClient _chatClient;
    private readonly SecureSettingsStore _settingsStore;
    private readonly EditApplicationService _editApplicationService;
    private readonly Dispatcher _dispatcher;
    private DocumentViewModel? _currentDocument;
    private string? _solutionDirectory;
    private CancellationTokenSource? _streamCancellation;

    public CopilotChatPaneViewModel(IChatClient chatClient, SecureSettingsStore settingsStore, EditApplicationService editApplicationService)
        : base(PaneContentId, "Copilot Chat")
    {
        _chatClient = chatClient;
        _settingsStore = settingsStore;
        _editApplicationService = editApplicationService;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        var savedKey = _settingsStore.GetSecret(OpenRouterChatClient.ApiKeySettingName);
        HasApiKey = !string.IsNullOrWhiteSpace(savedKey);
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
    private bool hasApiKey;

    [ObservableProperty]
    private bool isStreaming;

    [ObservableProperty]
    private string? statusMessage;

    public void SetActiveDocument(DocumentViewModel? document) => _currentDocument = document;

    public void SetSolutionDirectory(string? directory) => _solutionDirectory = directory;

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

        if (IncludeCurrentFileContext && _currentDocument is { FilePath: { } path })
        {
            history.Insert(0, new ChatMessage(
                ChatRole.System,
                $"The user currently has this file open ({path}):\n\n{_currentDocument.TextDocument.Text}"));
        }

        history.Add(new ChatMessage(ChatRole.User, userText));

        var assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistantMessage);

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

    partial void OnIsStreamingChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
}
