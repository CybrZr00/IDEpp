using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Embeddings;
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

    public SettingsViewModel(SecureSettingsStore secureSettingsStore, AppSettingsStore appSettingsStore)
    {
        _secureSettingsStore = secureSettingsStore;
        _appSettingsStore = appSettingsStore;

        hasApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(OpenRouterChatClient.ApiKeySettingName));
        hasDeepSeekApiKey = !string.IsNullOrWhiteSpace(_secureSettingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName));
        lmStudioBaseUrl = _appSettingsStore.Get(LmStudioEmbeddingClient.BaseUrlSettingName) ?? LmStudioEmbeddingClient.DefaultBaseUrl;
        lmStudioModel = _appSettingsStore.Get(LmStudioEmbeddingClient.ModelSettingName) ?? "";
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
}
