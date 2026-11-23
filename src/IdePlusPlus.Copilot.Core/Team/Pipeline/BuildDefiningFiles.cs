namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// Which files an owner may not simply claim. A claim is for a new source or test file the design forgot; a file
/// that defines how the solution builds (a project, a solution, MSBuild props, a package list) changes what every
/// other owner builds against, so it is the design's to list - or the user's - and never an owner's to take.
/// </summary>
public static class BuildDefiningFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".sln", ".slnx", ".slnf", ".props", ".targets", ".proj", ".nuspec",
    };

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "nuget.config", "packages.lock.json", "packages.config",
    };

    /// <summary>
    /// Why <paramref name="normalizedPath"/> cannot be claimed, or null when it can. Expects a path that already passed
    /// <see cref="PipelineArtifactStore.NormalizeRelativePath"/>.
    /// </summary>
    public static string? Refusal(string normalizedPath)
    {
        var segments = normalizedPath.Split('/');
        var name = segments[^1];
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{normalizedPath} is inside .git, which belongs to git.";
        }

        if (Extensions.Contains(Path.GetExtension(name)) || Names.Contains(name))
        {
            return $"{normalizedPath} defines how the solution builds, so it is not yours to claim: it changes what every owner "
                   + "builds against. If the work needs it changed, say so in your report.";
        }

        return null;
    }
}
