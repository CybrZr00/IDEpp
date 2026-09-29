namespace IdePlusPlus.Connections;

/// <summary>Reads a .env file's KEY=VALUE pairs, surfacing only keys that look connection-string-like
/// (containing "CONN", case-insensitively) - narrow on purpose, since a .env file typically holds
/// plenty of unrelated environment variables that would otherwise flood the pane. .env is usually
/// gitignored and not an MSBuild-visible item, so callers probe for it directly per project rather
/// than finding it via the solution's indexed file list (see ConnectionStringScanner).</summary>
public static class DotEnvConnectionReader
{
    public static IReadOnlyList<DiscoveredConnection> Read(string filePath, string? projectPath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var results = new List<DiscoveredConnection>();
        foreach (var rawLine in File.ReadAllLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim().Trim('"');

            if (value.Length > 0 && key.Contains("CONN", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new DiscoveredConnection(key, value, filePath, projectPath, ConnectionSourceKind.DotEnv));
            }
        }

        return results;
    }
}
