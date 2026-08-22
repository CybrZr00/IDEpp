using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Stores small per-user secrets (API keys) DPAPI-protected at rest, by default under
/// %LOCALAPPDATA%\IdePlusPlus\secrets.json. Never holds plaintext on disk.
/// </summary>
public sealed class SecureSettingsStore
{
    private static readonly string DefaultSecretsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "secrets.json");

    private readonly string _secretsPath;
    private readonly Dictionary<string, string> _protectedValuesByKey;

    public SecureSettingsStore(string? secretsPath = null)
    {
        _secretsPath = secretsPath ?? DefaultSecretsPath;
        _protectedValuesByKey = Load(_secretsPath);
    }

    public string? GetSecret(string key)
    {
        if (!_protectedValuesByKey.TryGetValue(key, out var protectedBase64))
        {
            return null;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var plaintextBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted under a different user profile (e.g. a migrated/re-imaged machine), or the
            // entry (or its base64 encoding) is corrupt - it can never be decrypted from here, so
            // drop it rather than leaving a permanently-dead row in secrets.json. Re-entering the
            // key will recreate it.
            _protectedValuesByKey.Remove(key);
            Save();
            return null;
        }
    }

    public void SetSecret(string key, string plaintextValue)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextValue);
        var protectedBytes = ProtectedData.Protect(plaintextBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        _protectedValuesByKey[key] = Convert.ToBase64String(protectedBytes);
        Save();
    }

    private static Dictionary<string, string> Load(string secretsPath)
    {
        try
        {
            if (!File.Exists(secretsPath))
            {
                return [];
            }

            var json = File.ReadAllText(secretsPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_secretsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(_protectedValuesByKey));
    }
}
