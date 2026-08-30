namespace IdePlusPlus.Publish;

/// <summary>
/// Assembles the `dotnet publish` command line for a profile, and works out where its output lands.
/// Pure and separated from the process spawn for the same reason RunService.BuildArguments is - the
/// argument list is the part worth asserting on, and it's untestable once it's inside a Process.
/// </summary>
public static class PublishArguments
{
    /// <summary>
    /// Everything except the profile reference is deliberately left off the command line.
    ///
    /// `-p:PublishProfile=x` makes MSBuild import the .pubxml, which then supplies the RID,
    /// deployment mode, output folder, single-file/trim/R2R switches and container image settings.
    /// Passing those a second time as CLI arguments would mean two sources of truth that can
    /// disagree - and worse, would make IDE++ publish something subtly different from what a plain
    /// `dotnet publish -p:PublishProfile=x` produces, which is the whole reason for storing profiles
    /// in the standard format. The profile is always saved before publishing so there is always a
    /// file to import.
    ///
    /// Configuration is the exception, passed explicitly because it also selects which build the
    /// publish consumes. Note it comes from the profile, not the IDE's Debug/Release picker:
    /// publishing Release while debugging Debug is the normal case.
    ///
    /// TargetFramework is passed for multi-targeted projects because the publish profile is imported
    /// per-TFM, so `dotnet publish` needs to be told which one before the import happens.
    /// </summary>
    public static List<string> Build(string projectPath, PublishProfile profile, string? targetFramework = null)
    {
        var arguments = new List<string> { "publish", projectPath };

        if (profile.Kind == PublishKind.Container)
        {
            // -t: rather than the documented /t: - identical to MSBuild, and a leading slash is a
            // path on non-Windows.
            arguments.Add("-t:PublishContainer");
        }

        arguments.Add($"-p:PublishProfile={profile.Name}");
        arguments.Add("--configuration");
        arguments.Add(profile.Configuration);

        if ((profile.TargetFramework ?? targetFramework) is { Length: > 0 } framework)
        {
            arguments.Add("--framework");
            arguments.Add(framework);
        }

        return arguments;
    }

    /// <summary>
    /// Best-effort guess at the folder the publish wrote to, for the "open the output folder" link.
    ///
    /// Not authoritative - MSBuild is, and a project can move PublishDir from anywhere. Callers only
    /// offer the link when the directory turned out to exist, so a wrong guess costs nothing.
    /// Returns null when there isn't enough information to guess at all.
    /// </summary>
    public static string? ResolvePublishDirectory(
        string projectPath, PublishProfile profile, string? targetFramework = null)
    {
        if (Path.GetDirectoryName(projectPath) is not { } projectDirectory)
        {
            return null;
        }

        // PublishDir conventionally ends with a separator; the default layout below doesn't produce
        // one. Trimming keeps both branches returning the same shape of path.
        if (profile.PublishDir is { Length: > 0 } publishDir)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(publishDir, projectDirectory));
        }

        var framework = profile.TargetFramework ?? targetFramework;
        if (string.IsNullOrEmpty(framework))
        {
            return null;
        }

        // bin/<Config>/<tfm>/[<rid>/]publish - the SDK's layout, confirmed by what `dotnet publish`
        // reports for a project with and without a RuntimeIdentifier.
        var segments = new List<string> { projectDirectory, "bin", profile.Configuration, framework };
        if (profile.RuntimeIdentifier is { Length: > 0 } runtimeIdentifier)
        {
            segments.Add(runtimeIdentifier);
        }

        segments.Add("publish");
        return Path.GetFullPath(Path.Combine([.. segments]));
    }
}
