using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// Remembers which solutions' project-local MCP configs the user has agreed to run.
///
/// A project-local `.ideplusplus/copilot/mcp-servers.json` is meant to be checked in, and every
/// entry in it is launched as `cmd.exe /c &lt;command&gt;` (see McpClient). Opening a solution used to
/// start those immediately, so cloning a repository and opening it ran whatever that repository
/// asked for, with no prompt and nothing on screen. Trust is therefore per solution AND per config
/// content: approving a repo's config once must not silently approve whatever it changes to on the
/// next pull.
///
/// Kept beside the global server list in %LOCALAPPDATA%, deliberately outside any solution - a
/// trust record stored under the solution would be checked in and would approve itself.
/// </summary>
public sealed class McpProjectTrustStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "mcp-project-trust.json");

    private readonly string _filePath;

    public McpProjectTrustStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    /// <summary>Content hash of a project-local config file. Line endings are normalized first so
    /// that a checkout under a different autocrlf setting isn't treated as a different config.</summary>
    public static string Fingerprint(string configFileText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configFileText.Replace("\r\n", "\n"))));

    public bool IsTrusted(string solutionDirectory, string configFingerprint) =>
        Load().TryGetValue(Key(solutionDirectory), out var trusted)
        && string.Equals(trusted, configFingerprint, StringComparison.Ordinal);

    public void Trust(string solutionDirectory, string configFingerprint)
    {
        var entries = Load();
        entries[Key(solutionDirectory)] = configFingerprint;
        Save(entries);
    }

    public void Revoke(string solutionDirectory)
    {
        var entries = Load();
        if (entries.Remove(Key(solutionDirectory)))
        {
            Save(entries);
        }
    }

    /// <summary>Full path, lowercased - the same solution reached via a different spelling of its
    /// path is the same solution on Windows.</summary>
    private static string Key(string solutionDirectory)
    {
        try
        {
            return Path.GetFullPath(solutionDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return solutionDirectory.ToLowerInvariant();
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable trust file means nothing is trusted, which fails closed.
            return [];
        }
    }

    private void Save(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(entries, SerializerOptions));
    }
}
