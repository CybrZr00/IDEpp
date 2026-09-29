using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdePlusPlus.Tasks;

/// <summary>
/// Remembers which solutions the user has agreed to run gulp/grunt task discovery for.
///
/// Listing a gulpfile's tasks means executing it - that is how gulp builds its task graph - so
/// opening a cloned repository would otherwise run whatever that repository put at module scope,
/// with no prompt. Approval is therefore per solution AND per manifest content: approving a repo
/// once must not silently approve whatever its gulpfile becomes on the next pull.
///
/// Kept in %LOCALAPPDATA%, deliberately outside any solution, for the same reason
/// McpProjectTrustStore is - and this store exists because that one already made the argument. The
/// design note for this feature originally said to keep the flag in the solution's own
/// .ideplusplus/tasks.json; that would have been a file a repository ships, approving itself.
/// </summary>
public sealed class JavaScriptTaskTrustStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "js-task-trust.json");

    private readonly string _filePath;

    public JavaScriptTaskTrustStore(string? filePath = null) => _filePath = filePath ?? DefaultFilePath;

    /// <summary>
    /// Content hash covering every JavaScript manifest in the solution, each keyed by its own path
    /// so that adding a second gulpfile is a change too, not just editing the first. Line endings
    /// are normalized so a checkout under a different autocrlf setting is not a different manifest.
    /// </summary>
    public static string Fingerprint(IEnumerable<(string Path, string Text)> manifests)
    {
        var canonical = new StringBuilder();
        foreach (var (path, text) in manifests.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
        {
            canonical.Append(path.Replace('\\', '/').ToLowerInvariant()).Append('\n');
            canonical.Append(text.Replace("\r\n", "\n")).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public bool IsTrusted(string solutionDirectory, string fingerprint) =>
        Load().TryGetValue(Key(solutionDirectory), out var trusted)
        && string.Equals(trusted, fingerprint, StringComparison.Ordinal);

    public void Trust(string solutionDirectory, string fingerprint)
    {
        var entries = Load();
        entries[Key(solutionDirectory)] = fingerprint;
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable trust file means nothing is trusted, which fails closed.
            return [];
        }
    }

    private void Save(Dictionary<string, string> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(entries, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to record the approval means being asked again next time, which is the safe
            // direction to fail in.
        }
    }
}
