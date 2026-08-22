using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Infrastructure;

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

    public SettingsViewModel(
        SecureSettingsStore secureSettingsStore,
        AppSettingsStore appSettingsStore,
        McpServerManager mcpServerManager,
        McpServerConfigStore mcpServerConfigStore)
    {
        _secureSettingsStore = secureSettingsStore;
        _appSettingsStore = appSettingsStore;
        _mcpServerManager = mcpServerManager;
        _mcpServerConfigStore = mcpServerConfigStore;

        hasApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(OpenRouterChatClient.ApiKeySettingName));
        hasDeepSeekApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName));
        lmStudioBaseUrl = _appSettingsStore.Get(LmStudioEmbeddingClient.BaseUrlSettingName) ?? LmStudioEmbeddingClient.DefaultBaseUrl;
        lmStudioModel = _appSettingsStore.Get(LmStudioEmbeddingClient.ModelSettingName) ?? "";

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
    private string lmStudioBaseUrl;

    [ObservableProperty]
    private string lmStudioModel;

    [ObservableProperty]
    private string? statusMessage;

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

    [RelayCommand]
    private void SaveLmStudioSettings()
    {
        _appSettingsStore.Set(LmStudioEmbeddingClient.BaseUrlSettingName, LmStudioBaseUrl.Trim());
        _appSettingsStore.Set(LmStudioEmbeddingClient.ModelSettingName, LmStudioModel.Trim());
        StatusMessage = "LM Studio settings saved.";
    }

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
        var statuses = _mcpServerManager.ServerStatuses.ToDictionary(s => s.Name);
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
