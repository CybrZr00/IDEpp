namespace IdePlusPlus.Connections;

/// <summary>Finds every connection string across the open solution's config files: appsettings*.json
/// and web.config/app.config (from the solution's already-indexed file list, the same one
/// CopilotChat.SetIndexableFiles uses), plus a direct per-project probe for .env - see
/// DotEnvConnectionReader for why that one isn't found via the indexed list.</summary>
public static class ConnectionStringScanner
{
    public static IReadOnlyList<DiscoveredConnection> Scan(
        IReadOnlyList<string> solutionFilePaths,
        IReadOnlyList<(string ProjectPath, string ProjectDirectory)> projects)
    {
        var results = new List<DiscoveredConnection>();

        foreach (var filePath in solutionFilePaths)
        {
            var fileName = Path.GetFileName(filePath);
            var projectPath = ProjectFor(filePath, projects);

            if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(AppSettingsConnectionReader.Read(filePath, projectPath));
            }
            else if (fileName.Equals("web.config", StringComparison.OrdinalIgnoreCase)
                     || fileName.Equals("app.config", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(WebConfigConnectionReader.Read(filePath, projectPath));
            }
        }

        foreach (var (projectPath, projectDirectory) in projects)
        {
            var dotEnvPath = Path.Combine(projectDirectory, ".env");
            results.AddRange(DotEnvConnectionReader.Read(dotEnvPath, projectPath));
        }

        return results;
    }

    /// <summary>The deepest (most specific) project directory the file lives under, so a file in a
    /// nested project isn't mis-attributed to a shallower sibling.</summary>
    private static string? ProjectFor(
        string filePath, IReadOnlyList<(string ProjectPath, string ProjectDirectory)> projects) =>
        projects
            .Where(p => filePath.StartsWith(p.ProjectDirectory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.ProjectDirectory.Length)
            .Select(p => (string?)p.ProjectPath)
            .FirstOrDefault();
}
