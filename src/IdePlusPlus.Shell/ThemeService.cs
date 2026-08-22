using System.Windows;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Shell;

/// <summary>
/// Swaps the app's Light/Dark resource dictionary and pushes the same choice to EditorTheme (for
/// AvalonEdit-internal rendering that can't use DynamicResource). MainWindow separately reacts to
/// Changed to swap AvalonDock's own theme, since that's specific to the DockingManager control.
/// </summary>
public sealed class ThemeService
{
    private const string SettingKey = "Theme";

    private readonly AppSettingsStore _settingsStore;

    public ThemeService(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public bool IsDark { get; private set; }

    public event Action? Changed;

    /// <summary>Applies the saved theme (defaulting to light) without persisting it again. Call once at startup.</summary>
    public void Initialize() => Apply(_settingsStore.Get(SettingKey) == "Dark", persist: false);

    public void Toggle() => Apply(!IsDark, persist: true);

    private void Apply(bool dark, bool persist)
    {
        IsDark = dark;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                dark
                    ? "/IdePlusPlus.Shell;component/Themes/DarkTheme.xaml"
                    : "/IdePlusPlus.Shell;component/Themes/LightTheme.xaml",
                UriKind.Relative),
        };

        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(dictionary);

        EditorTheme.Apply(dark);

        if (persist)
        {
            _settingsStore.Set(SettingKey, dark ? "Dark" : "Light");
        }

        Changed?.Invoke();
    }
}
