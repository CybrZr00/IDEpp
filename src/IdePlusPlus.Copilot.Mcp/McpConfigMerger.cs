namespace IdePlusPlus.Copilot.Mcp;

public static class McpConfigMerger
{
    /// <summary>Project-local entries override a global one of the same name, and add any new ones -
    /// global first, project second, so a later same-key assignment wins. Also tolerates a duplicate
    /// name already present within <paramref name="global"/> itself (e.g. a hand-edited or otherwise
    /// corrupted config file) rather than throwing - last occurrence wins there too.</summary>
    public static IReadOnlyList<McpServerConfig> Merge(IReadOnlyList<McpServerConfig> global, IReadOnlyList<McpServerConfig> projectLocal)
    {
        var byName = new Dictionary<string, McpServerConfig>();
        foreach (var config in global.Concat(projectLocal))
        {
            byName[config.Name] = config;
        }

        return byName.Values.ToList();
    }
}
