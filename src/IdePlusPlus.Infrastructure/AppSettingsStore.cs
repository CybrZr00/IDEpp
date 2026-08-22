using System.Text.Json;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Plain (unencrypted) per-user key/value settings under %LOCALAPPDATA%\IdePlusPlus\settings.json -
/// for non-secret configuration like a local server URL, as opposed to SecureSettingsStore's API keys.
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "settings.json");

    private readonly string _settingsPath;
    private readonly Dictionary<string, string> _values;

    public AppSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? DefaultSettingsPath;
        _values = Load(_settingsPath);
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value)
    {
        _values[key] = value;
        Save();
    }

    private static Dictionary<string, string> Load(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(settingsPath)) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_values));
    }
}
