using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.Core;
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
    private readonly ExternalToolchainStore _externalToolchainStore;
    private readonly AnthropicChatClient? _anthropicClient;
    private readonly Func<string?, Task<IReadOnlyList<string>>>? _fetchModels;

    /// <param name="fetchModels">A provider's model ids (null: the chat's provider), for the lead's
    /// and sub-agents' model pickers. Without it they are plain text boxes.</param>
    public SettingsViewModel(
        SecureSettingsStore secureSettingsStore,
        AppSettingsStore appSettingsStore,
        McpServerManager mcpServerManager,
        McpServerConfigStore mcpServerConfigStore,
        DeepSeekFimClient deepSeekFimClient,
        ExternalToolchainStore externalToolchainStore,
        AnthropicChatClient? anthropicClient = null,
        Func<string?, Task<IReadOnlyList<string>>>? fetchModels = null)
    {
        _anthropicClient = anthropicClient;
        _fetchModels = fetchModels;
        _secureSettingsStore = secureSettingsStore;
        _appSettingsStore = appSettingsStore;
        _mcpServerManager = mcpServerManager;
        _mcpServerConfigStore = mcpServerConfigStore;
        _deepSeekFimClient = deepSeekFimClient;
        _externalToolchainStore = externalToolchainStore;

        hasApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(OpenRouterChatClient.ApiKeySettingName));
        hasDeepSeekApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName));
        hasAnthropicApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(AnthropicChatClient.ApiKeySettingName));
        hasLmStudioApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(LmStudioEmbeddingClient.ApiKeySettingName));
        lmStudioBaseUrl = _appSettingsStore.Get(LmStudioEmbeddingClient.BaseUrlSettingName) ?? LmStudioEmbeddingClient.DefaultBaseUrl;
        lmStudioModel = _appSettingsStore.Get(LmStudioEmbeddingClient.ModelSettingName) ?? "";
        useLmStudioEmbeddings = _appSettingsStore.Get(EmbeddingClientSelector.ProviderSettingName) == EmbeddingClientSelector.LmStudioProvider;
        systemPrompt = _appSettingsStore.Get(CopilotChatPaneViewModel.SystemPromptSettingKey) ?? "";
        restoreNuGetOnSolutionLoad = !bool.TryParse(_appSettingsStore.Get(MainWindowViewModel.RestoreOnSolutionLoadSettingKey), out var savedRestore) || savedRestore;
        ultraWorkerCount = CopilotChatPaneViewModel.ReadUltraWorkerCount(_appSettingsStore);
        ultraTimeBudgetMinutes = CopilotChatPaneViewModel.ReadUltraTimeBudgetMinutes(_appSettingsStore);
        ultraConcurrentRequests = CopilotChatPaneViewModel.ReadUltraConcurrentRequests(_appSettingsStore);
        ultraMaxRunMinutes = CopilotChatPaneViewModel.ReadUltraMaxRunMinutes(_appSettingsStore);
        ultraMaxTokensMillions = CopilotChatPaneViewModel.ReadUltraMaxTokensMillions(_appSettingsStore);
        ultraAutoScale = CopilotChatPaneViewModel.ReadUltraAutoScale(_appSettingsStore);
        ultraStartWorkers = CopilotChatPaneViewModel.ReadUltraStartWorkers(_appSettingsStore);
        ultraOrchestrated = CopilotChatPaneViewModel.ReadUltraOrchestrated(_appSettingsStore);
        ultraOrchestratorProvider = CopilotChatPaneViewModel.ReadUltraOrchestratorProvider(_appSettingsStore) ?? SameAsChatChoice;
        ultraOrchestratorModel = CopilotChatPaneViewModel.ReadUltraOrchestratorModel(_appSettingsStore) ?? "";
        ultraOrchestratorEffort = EffortChoiceFor(_appSettingsStore.Get(CopilotChatPaneViewModel.UltraOrchestratorEffortSettingKey));
        subAgentProvider = CopilotChatPaneViewModel.ReadSubAgentProvider(_appSettingsStore) ?? SameAsChatChoice;
        subAgentModel = CopilotChatPaneViewModel.ReadSubAgentModel(_appSettingsStore) ?? "";
        subAgentEffort = EffortChoiceFor(_appSettingsStore.Get(CopilotChatPaneViewModel.SubAgentEffortSettingKey));

        foreach (var config in _mcpServerConfigStore.Load())
        {
            McpServers.Add(new McpServerRowViewModel
            {
                Name = config.Name,
                Command = config.Command,
                ArgsText = FormatArgs(config.Args),
                EnvText = config.Env is null ? "" : string.Join(';', config.Env.Select(kv => $"{kv.Key}={kv.Value}")),
            });
        }

        RefreshMcpServerStatuses();

        foreach (var tool in _externalToolchainStore.Tools)
        {
            Toolchains.Add(new ToolRowViewModel(tool));
        }

        RefreshToolchains();
    }

    /// <summary>The reverse-engineering toolchains (Ghidra, JDK) shown in the Tools section.</summary>
    public ObservableCollection<ToolRowViewModel> Toolchains { get; } = [];

    /// <summary>Exposed so the dialog's code-behind can run an install (which needs a window owner for
    /// the consent dialog) against the same store.</summary>
    public ExternalToolchainStore ToolchainStore => _externalToolchainStore;

    public void RefreshToolchains()
    {
        foreach (var row in Toolchains)
        {
            row.State = _externalToolchainStore.State(row.Id);
        }
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
    [NotifyPropertyChangedFor(nameof(AnthropicApiKeyStatusText))]
    private bool hasAnthropicApiKey;

    public string AnthropicApiKeyStatusText => HasAnthropicApiKey ? "Key configured." : "No key set.";

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

    /// <summary>How many workers an Ultra message starts, and how long they get. Read with the same
    /// clamping the chat pane applies, so what this shows is what a run will use.</summary>
    [ObservableProperty]
    private int ultraWorkerCount;

    [ObservableProperty]
    private int ultraTimeBudgetMinutes;

    [ObservableProperty]
    private int ultraConcurrentRequests;

    /// <summary>The caps on a run left to go until done: total minutes, and total tokens in
    /// millions (0 for no token limit).</summary>
    [ObservableProperty]
    private int ultraMaxRunMinutes;

    [ObservableProperty]
    private int ultraMaxTokensMillions;

    /// <summary>Auto mode: start with UltraStartWorkers and let the workers ask for more, up to
    /// UltraWorkerCount.</summary>
    [ObservableProperty]
    private bool ultraAutoScale;

    [ObservableProperty]
    private int ultraStartWorkers;

    /// <summary>Run Ultra with a lead that plans the work, checks in on the team and reviews each merge
    /// before it lands. Costs the lead's own model calls on top of the workers'.</summary>
    [ObservableProperty]
    private bool ultraOrchestrated;

    /// <summary>The lead's provider; <see cref="SameAsChatChoice"/> means the chat's own.</summary>
    [ObservableProperty]
    private string ultraOrchestratorProvider;

    /// <summary>The lead's model id; blank means the chat's own, or - on another provider - that
    /// provider's last-used model in the chat, else its default.</summary>
    [ObservableProperty]
    private string ultraOrchestratorModel;

    /// <summary>What the lead's model picker offers, once fetched from its provider.</summary>
    public ObservableCollection<string> UltraOrchestratorModelOptions { get; } = [];

    /// <summary>The lead's effort; <see cref="DefaultEffortChoice"/> means High.</summary>
    [ObservableProperty]
    private string ultraOrchestratorEffort;

    /// <summary>The provider for sub-agents; <see cref="SameAsChatChoice"/> means the chat's own.</summary>
    [ObservableProperty]
    private string subAgentProvider;

    /// <summary>The model id for sub-agents - investigate_parallel's investigators and Ultra's workers;
    /// blank means the chat's own, or - on another provider - that provider's last-used model in the
    /// chat, else its default.</summary>
    [ObservableProperty]
    private string subAgentModel;

    /// <summary>What the sub-agents' model picker offers, once fetched from their provider.</summary>
    public ObservableCollection<string> SubAgentModelOptions { get; } = [];

    public const string SameAsChatChoice = "Same as chat";

    /// <summary>What the lead's and sub-agents' provider boxes offer.</summary>
    public IReadOnlyList<string> ProviderChoices { get; } = [SameAsChatChoice, .. CopilotChatPaneViewModel.AllProviders];

    public bool CanFetchModels => _fetchModels is not null;

    private static string StoredProvider(string choice) => choice == SameAsChatChoice ? "" : choice;

    private static string? ProviderFor(string choice) => choice == SameAsChatChoice ? null : choice;

    // A model list belongs to one provider - switching leaves the typed id alone but drops the list.
    partial void OnUltraOrchestratorProviderChanged(string value) => UltraOrchestratorModelOptions.Clear();

    partial void OnSubAgentProviderChanged(string value) => SubAgentModelOptions.Clear();

    [RelayCommand]
    private Task FetchLeadModelsAsync() => FetchModelsIntoAsync(UltraOrchestratorProvider, UltraOrchestratorModelOptions, "lead");

    [RelayCommand]
    private Task FetchSubAgentModelsAsync() => FetchModelsIntoAsync(SubAgentProvider, SubAgentModelOptions, "sub-agent");

    private async Task FetchModelsIntoAsync(string providerChoice, ObservableCollection<string> into, string role)
    {
        if (_fetchModels is null)
        {
            return;
        }

        var label = providerChoice == SameAsChatChoice ? "the chat's provider" : providerChoice;
        StatusMessage = $"Fetching {label} models...";
        IReadOnlyList<string> models;
        try
        {
            models = await _fetchModels(ProviderFor(providerChoice));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            models = [];
        }

        into.Clear();
        foreach (var model in models)
        {
            into.Add(model);
        }

        StatusMessage = models.Count == 0
            ? $"Couldn't fetch models from {label} - check its key or URL. You can still type a model id."
            : $"{models.Count} {label} models available for the {role} model.";
    }

    /// <summary>The effort for sub-agents; <see cref="DefaultEffortChoice"/> means the chat's own.</summary>
    [ObservableProperty]
    private string subAgentEffort;

    public const string DefaultEffortChoice = "Default";

    /// <summary>What the effort boxes offer. Ultra is left out: it is a mode, not an effort one request can run at.</summary>
    public IReadOnlyList<string> EffortChoices { get; } =
        [DefaultEffortChoice, nameof(ChatEffort.None), nameof(ChatEffort.Low), nameof(ChatEffort.Medium), nameof(ChatEffort.High)];

    private static string EffortChoiceFor(string? stored) =>
        Enum.TryParse<ChatEffort>(stored, ignoreCase: true, out var effort) && effort != ChatEffort.Ultra
            ? effort.ToString()
            : DefaultEffortChoice;

    private static string StoredEffort(string choice) => choice == DefaultEffortChoice ? "" : choice;

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

    public void SaveAnthropicApiKey(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
        {
            return;
        }

        _secureSettingsStore.SetSecret(AnthropicChatClient.ApiKeySettingName, plaintextKey.Trim());
        HasAnthropicApiKey = true;
        StatusMessage = "Anthropic API key saved.";
    }

    /// <summary>Tests whatever key is typed in the box, or the saved key if the box is empty, by
    /// listing models - free, unlike a completion.</summary>
    public async Task TestAnthropicConnectionAsync(string typedKey)
    {
        var apiKey = string.IsNullOrWhiteSpace(typedKey) ? _secureSettingsStore.GetSecret(AnthropicChatClient.ApiKeySettingName) : typedKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusMessage = "Enter or save an Anthropic API key first.";
            return;
        }

        if (_anthropicClient is null)
        {
            return;
        }

        StatusMessage = "Testing Anthropic connection...";
        var error = await _anthropicClient.TestConnectionAsync(apiKey, CancellationToken.None);
        StatusMessage = error is null ? "Anthropic: connected." : $"Anthropic test failed: {error}";
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
    private void SaveUltraSettings()
    {
        UltraWorkerCount = Math.Clamp(UltraWorkerCount, 1, CopilotChatPaneViewModel.MaxUltraWorkerCount);
        UltraTimeBudgetMinutes = Math.Clamp(
            UltraTimeBudgetMinutes,
            CopilotChatPaneViewModel.MinUltraTimeBudgetMinutes,
            CopilotChatPaneViewModel.MaxUltraTimeBudgetMinutes);
        UltraConcurrentRequests = Math.Clamp(UltraConcurrentRequests, 1, CopilotChatPaneViewModel.MaxUltraWorkerCount);
        UltraMaxRunMinutes = Math.Clamp(
            UltraMaxRunMinutes, CopilotChatPaneViewModel.MinUltraTimeBudgetMinutes, CopilotChatPaneViewModel.MaxUltraMaxRunMinutes);
        UltraMaxTokensMillions = Math.Clamp(UltraMaxTokensMillions, 0, CopilotChatPaneViewModel.MaxUltraMaxTokensMillions);
        UltraStartWorkers = Math.Clamp(UltraStartWorkers, 1, UltraWorkerCount);
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraAutoScaleSettingKey, UltraAutoScale.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraStartWorkersSettingKey, UltraStartWorkers.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraWorkerCountSettingKey, UltraWorkerCount.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraTimeBudgetSettingKey, UltraTimeBudgetMinutes.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraConcurrentRequestsSettingKey, UltraConcurrentRequests.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraMaxRunMinutesSettingKey, UltraMaxRunMinutes.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraMaxTokensMillionsSettingKey, UltraMaxTokensMillions.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraOrchestratedSettingKey, UltraOrchestrated.ToString());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraOrchestratorProviderSettingKey, StoredProvider(UltraOrchestratorProvider));
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraOrchestratorModelSettingKey, UltraOrchestratorModel.Trim());
        _appSettingsStore.Set(CopilotChatPaneViewModel.UltraOrchestratorEffortSettingKey, StoredEffort(UltraOrchestratorEffort));
        StatusMessage = $"Ultra settings saved - "
                        + (UltraAutoScale ? $"auto mode, {UltraStartWorkers} workers growing to {UltraWorkerCount}" : $"{UltraWorkerCount} workers")
                        + (UltraOrchestrated ? " with a lead" : "")
                        + $", up to {UltraTimeBudgetMinutes} minutes, "
                        + $"{UltraConcurrentRequests} model requests at a time; run until done stops after "
                        + $"{UltraMaxRunMinutes} minutes"
                        + (UltraMaxTokensMillions > 0 ? $" or {UltraMaxTokensMillions}M tokens." : ".");
    }

    [RelayCommand]
    private void SaveSubAgentSettings()
    {
        _appSettingsStore.Set(CopilotChatPaneViewModel.SubAgentProviderSettingKey, StoredProvider(SubAgentProvider));
        _appSettingsStore.Set(CopilotChatPaneViewModel.SubAgentModelSettingKey, SubAgentModel.Trim());
        _appSettingsStore.Set(CopilotChatPaneViewModel.SubAgentEffortSettingKey, StoredEffort(SubAgentEffort));
        StatusMessage = "Sub-agent settings saved.";
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
    private void AddReaMcpServer() => McpServers.Add(new McpServerRowViewModel
    {
        Name = "rea",
        Command = @"C:\Program Files\nodejs\node.exe",
        ArgsText = FormatArgs([@"C:\Users\markp\AppData\Roaming\npm\node_modules\rea-agents\scripts\rea.mjs", "mcp"]),
        EnvText = @"GHIDRA_INSTALL_DIR=C:\ghidra\ghidra_12.1.4_PUBLIC;JAVA_HOME=C:\Program Files\Eclipse Adoptium\jdk-21.0.8.9-hotspot",
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

    /// <summary>Splits on spaces, but a "double quoted" run stays one argument (for paths containing spaces).</summary>
    internal static List<string> ParseArgs(string argsText)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var ch in argsText)
        {
            if (ch == '"') { inQuotes = !inQuotes; hasToken = true; }
            else if (ch == ' ' && !inQuotes)
            {
                if (hasToken) args.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else { current.Append(ch); hasToken = true; }
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }

    /// <summary>Inverse of <see cref="ParseArgs"/>: quotes any argument containing a space.</summary>
    internal static string FormatArgs(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

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
