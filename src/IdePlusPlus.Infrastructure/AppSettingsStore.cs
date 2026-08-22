using System.Text.Json;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Plain (unencrypted) per-user key/value settings under %LOCALAPPDATA%\IdePlusPlus\settings.json -
/// for non-secret configuration like a local server URL, as opposed to SecureSettingsStore's API keys.
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "settings.json");

    private readonly Dictionary<string, string> _values;

    public AppSettingsStore()
    {
        _values = Load();
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value)
    {
        _values[key] = value;
        Save();
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath)) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_values));
    }
}
