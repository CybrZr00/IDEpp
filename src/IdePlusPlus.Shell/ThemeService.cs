using System.Windows;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Shell;

/// <summary>
/// Swaps the app's chrome resource dictionary (Themes/*.xaml) to match the selected AppTheme, and
/// pushes the same choice to EditorTheme (for AvalonEdit-internal rendering that can't use
/// DynamicResource). MainWindow separately reacts to Changed to swap AvalonDock's own theme, since
/// that's specific to the DockingManager control - AvalonDock only ships light/dark built-ins, so it
/// follows EditorTheme.IsDark rather than getting one of its own per AppTheme.
/// </summary>
public sealed class ThemeService
{
    private const string SettingKey = "Theme";

    private static readonly Dictionary<AppTheme, string> DictionaryUriByTheme = new()
    {
        [AppTheme.Light] = "/IdePlusPlus.Shell;component/Themes/LightTheme.xaml",
        [AppTheme.Dark] = "/IdePlusPlus.Shell;component/Themes/DarkTheme.xaml",
        [AppTheme.SoftDark] = "/IdePlusPlus.Shell;component/Themes/SoftDarkTheme.xaml",
        [AppTheme.DyslexiaFriendly] = "/IdePlusPlus.Shell;component/Themes/DyslexiaFriendlyTheme.xaml",
    };

    private readonly AppSettingsStore _settingsStore;

    public ThemeService(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>Convenience for the small handful of call sites that only care "is this dark-ish" -
    /// see EditorTheme.IsDark for exactly which themes that covers.</summary>
    public bool IsDark => EditorTheme.IsDark;

    public event Action? Changed;

    /// <summary>Applies the saved theme (defaulting to Light) without persisting it again. Call once at startup.</summary>
    public void Initialize() => Apply(ParseSavedTheme(), persist: false);

    public void SetTheme(AppTheme theme) => Apply(theme, persist: true);

    private AppTheme ParseSavedTheme() =>
        Enum.TryParse<AppTheme>(_settingsStore.Get(SettingKey), out var saved) ? saved : AppTheme.Light;

    private void Apply(AppTheme theme, bool persist)
    {
        Current = theme;

        var dictionary = new ResourceDictionary { Source = new Uri(DictionaryUriByTheme[theme], UriKind.Relative) };

        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(dictionary);

        EditorTheme.Apply(theme);

        if (persist)
        {
            _settingsStore.Set(SettingKey, theme.ToString());
        }

        Changed?.Invoke();
    }
}
