using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

/// <summary>
/// Backs the Settings dialog. API keys and LM Studio config used to live inline in the Copilot Chat
/// panel, but that panel is docked narrow and the extra rows didn't fit - see the M7 commit. Both
/// OpenRouterChatClient/DeepSeekFimClient/LmStudioEmbeddingClient re-read their settings from the
/// stores on every call rather than caching them, so changes made here take effect immediately with
/// no refresh/notification plumbing back to the chat panel needed.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SecureSettingsStore _secureSettingsStore;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly McpServerManager _mcpServerManager;
    private readonly McpServerConfigStore _mcpServerConfigStore;
    private readonly DeepSeekFimClient _deepSeekFimClient;

    public SettingsViewModel(
        SecureSettingsStore secureSettingsStore,
        AppSettingsStore appSettingsStore,
        McpServerManager mcpServerManager,
        McpServerConfigStore mcpServerConfigStore,
        DeepSeekFimClient deepSeekFimClient)
    {
        _secureSettingsStore = secureSettingsStore;
        _appSettingsStore = appSettingsStore;
        _mcpServerManager = mcpServerManager;
        _mcpServerConfigStore = mcpServerConfigStore;
        _deepSeekFimClient = deepSeekFimClient;

        hasApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(OpenRouterChatClient.ApiKeySettingName));
        hasDeepSeekApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName));
        hasLmStudioApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(LmStudioEmbeddingClient.ApiKeySettingName));
        lmStudioBaseUrl = _appSettingsStore.Get(LmStudioEmbeddingClient.BaseUrlSettingName) ?? LmStudioEmbeddingClient.DefaultBaseUrl;
        lmStudioModel = _appSettingsStore.Get(LmStudioEmbeddingClient.ModelSettingName) ?? "";
        useLmStudioEmbeddings = _appSettingsStore.Get(EmbeddingClientSelector.ProviderSettingName) == EmbeddingClientSelector.LmStudioProvider;
        systemPrompt = _appSettingsStore.Get(CopilotChatPaneViewModel.SystemPromptSettingKey) ?? "";
        restoreNuGetOnSolutionLoad = !bool.TryParse(_appSettingsStore.Get(MainWindowViewModel.RestoreOnSolutionLoadSettingKey), out var savedRestore) || savedRestore;

        foreach (var config in _mcpServerConfigStore.Load())
        {
            McpServers.Add(new McpServerRowViewModel
            {
                Name = config.Name,
                Command = config.Command,
                ArgsText = string.Join(' ', config.Args),
                EnvText = config.Env is null ? "" : string.Join(';', config.Env.Select(kv => $"{kv.Key}={kv.Value}")),
            });
        }

        RefreshMcpServerStatuses();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApiKeyStatusText))]
    private bool hasApiKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepSeekApiKeyStatusText))]
    private bool hasDeepSeekApiKey;

    public string ApiKeyStatusText => HasApiKey ? "Key configured." : "No key set.";

    public string DeepSeekApiKeyStatusText => HasDeepSeekApiKey ? "Key configured." : "No key set.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LmStudioApiKeyStatusText))]
    private bool hasLmStudioApiKey;

    public string LmStudioApiKeyStatusText => HasLmStudioApiKey ? "Key configured." : "No key set (not needed for a local LM Studio server).";

    [ObservableProperty]
    private string lmStudioBaseUrl;

    [ObservableProperty]
    private string lmStudioModel;

    /// <summary>Off by default: codebase search embeds with the built-in ONNX model. Saved as soon as
    /// it changes, like <see cref="RestoreNuGetOnSolutionLoad"/>. An index built by the other model is
    /// ignored until the codebase is re-indexed.</summary>
    [ObservableProperty]
    private bool useLmStudioEmbeddings;

    [ObservableProperty]
    private string systemPrompt;

    [ObservableProperty]
    private string? statusMessage;

    /// <summary>Defaults to on when unset - mirrors the inverted bool.TryParse idiom used wherever a
    /// setting should default true (see MainWindowViewModel.LoadSolutionAsync, which consumes this
    /// same key). Saved immediately on change, the same way CopilotChatPaneViewModel's checkbox-style
    /// bool settings (IsAutoMode/IsPlanMode) save themselves rather than needing a separate button.</summary>
    [ObservableProperty]
    private bool restoreNuGetOnSolutionLoad;

    public ObservableCollection<McpServerRowViewModel> McpServers { get; } = [];

    public void SaveApiKey(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            return;
        }

        _secureSettingsStore.SetSecret(OpenRouterChatClient.ApiKeySettingName, plaintextKey.Trim());
        HasApiKey = true;
        StatusMessage = "OpenRouter API key saved.";
    }

    public void SaveDeepSeekApiKey(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            return;
        }

        _secureSettingsStore.SetSecret(DeepSeekFimClient.ApiKeySettingName, plaintextKey.Trim());
        HasDeepSeekApiKey = true;
        StatusMessage = "DeepSeek API key saved.";
    }

    public void SaveLmStudioApiKey(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            return;
        }

        _secureSettingsStore.SetSecret(LmStudioEmbeddingClient.ApiKeySettingName, plaintextKey.Trim());
        HasLmStudioApiKey = true;
        StatusMessage = "LM Studio API key saved.";
    }

    /// <summary>The secret store has no delete, so clearing stores an empty value - which the LM Studio
    /// clients treat the same as no key at all (nothing is sent).</summary>
    [RelayCommand]
    private void ClearLmStudioApiKey()
    {
        _secureSettingsStore.SetSecret(LmStudioEmbeddingClient.ApiKeySettingName, "");
        HasLmStudioApiKey = false;
        StatusMessage = "LM Studio API key cleared.";
    }

    /// <summary>Tests whatever key is currently typed in the box, or the already-saved key if the
    /// box is empty - DeepSeek has no dedicated health-check endpoint, so this is a real (tiny)
    /// completion request.</summary>
    public async Task TestDeepSeekConnectionAsync(string typedKey)
    {
        var apiKey = string.IsNullOrWhiteSpace(typedKey) ? _secureSettingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName) : typedKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusMessage = "Enter or save a DeepSeek API key first.";
            return;
        }

        StatusMessage = "Testing DeepSeek connection...";
        var (success, message) = await _deepSeekFimClient.TestConnectionAsync(apiKey, CancellationToken.None);
        StatusMessage = success ? $"DeepSeek: {message}" : $"DeepSeek test failed: {message}";
    }

    [RelayCommand]
    private void SaveLmStudioSettings()
    {
        _appSettingsStore.Set(LmStudioEmbeddingClient.BaseUrlSettingName, LmStudioBaseUrl.Trim());
        _appSettingsStore.Set(LmStudioEmbeddingClient.ModelSettingName, LmStudioModel.Trim());
        StatusMessage = "LM Studio settings saved.";
    }

    [RelayCommand]
    private void SaveSystemPrompt()
    {
        _appSettingsStore.Set(CopilotChatPaneViewModel.SystemPromptSettingKey, SystemPrompt);
        StatusMessage = "System prompt saved.";
    }

    partial void OnUseLmStudioEmbeddingsChanged(bool value)
    {
        _appSettingsStore.Set(EmbeddingClientSelector.ProviderSettingName, value ? EmbeddingClientSelector.LmStudioProvider : "Local");
        StatusMessage = "Embedding source changed - re-index the codebase for search to use it.";
    }

    partial void OnRestoreNuGetOnSolutionLoadChanged(bool value) =>
        _appSettingsStore.Set(MainWindowViewModel.RestoreOnSolutionLoadSettingKey, value.ToString());

    [RelayCommand]
    private void AddMcpServer() => McpServers.Add(new McpServerRowViewModel());

    [RelayCommand]
    private void AddPlaywrightMcpServer() => McpServers.Add(new McpServerRowViewModel
    {
        Name = "playwright",
        Command = "npx",
        ArgsText = "-y @playwright/mcp@latest",
    });

    [RelayCommand]
    private void AddContext7McpServer() => McpServers.Add(new McpServerRowViewModel
    {
        Name = "context7",
        Command = "npx",
        ArgsText = "-y @upstash/context7-mcp",
    });

    [RelayCommand]
    private void RemoveMcpServer(McpServerRowViewModel row) => McpServers.Remove(row);

    [RelayCommand]
    private async Task SaveAndReconnectMcpServersAsync()
    {
        var configs = McpServers
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Command))
            .Select(r => new McpServerConfig(r.Name.Trim(), r.Command.Trim(), ParseArgs(r.ArgsText), ParseEnv(r.EnvText)))
            .ToList();

        _mcpServerConfigStore.Save(configs);
        StatusMessage = "Connecting to MCP servers...";

        await _mcpServerManager.ReconnectAllAsync();

        RefreshMcpServerStatuses();
        StatusMessage = "MCP servers reconnected.";
    }

    private void RefreshMcpServerStatuses()
    {
        // GroupBy+last rather than ToDictionary: a duplicate server name (e.g. two saved rows with
        // the same name, or - previously - a race in McpServerManager.ReconnectAllAsync) must not
        // crash the whole Settings dialog just to show connection status; last one wins, same
        // "later entry overrides" rule McpConfigMerger already documents.
        var statuses = _mcpServerManager.ServerStatuses.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Last());
        foreach (var row in McpServers)
        {
            row.StatusText = statuses.TryGetValue(row.Name, out var status)
                ? status.IsConnected ? $"Connected - {status.ToolCount} tool(s)." : $"Failed: {status.Error}"
                : null;
        }
    }

    private static List<string> ParseArgs(string argsText) =>
        argsText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static Dictionary<string, string>? ParseEnv(string envText)
    {
        var pairs = envText
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

        return pairs.Count > 0 ? pairs : null;
    }
}
