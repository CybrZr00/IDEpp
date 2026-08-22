namespace IdePlusPlus.Copilot.Mcp;

public static class McpConfigMerger
{
    /// <summary>Project-local entries override a global one of the same name, and add any new ones -
    /// global first, project second, so a later same-key assignment wins.</summary>
    public static IReadOnlyList<McpServerConfig> Merge(IReadOnlyList<McpServerConfig> global, IReadOnlyList<McpServerConfig> projectLocal)
    {
        var byName = global.ToDictionary(c => c.Name);
        foreach (var config in projectLocal)
        {
            byName[config.Name] = config;
        }

        return byName.Values.ToList();
    }
}
