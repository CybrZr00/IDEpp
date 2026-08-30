using IdePlusPlus.Build;

namespace IdePlusPlus.Publish;

/// <summary>
/// Runs `dotnet publish` for a profile.
///
/// Everything goes through BuildService.RunDotnetAsync rather than spawning a process directly, so a
/// publish and a build can't run at once over the same obj/bin - and so <see cref="IsRunning"/>,
/// BuildService.IsRunning and the IDE's "a build is already running" refusal are all the same fact
/// rather than three that have to be kept in sync.
/// </summary>
public sealed class PublishService(BuildService buildService)
{
    private readonly BuildService _buildService = buildService;

    public bool IsRunning => _buildService.IsRunning;

    /// <exception cref="InvalidOperationException">The profile uses a protocol IDE++ can't run, or a
    /// build/publish is already in flight.</exception>
    public Task<int> PublishAsync(
        string projectPath,
        PublishProfile profile,
        Action<string> onOutputLine,
        string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        if (!profile.IsRunnable)
        {
            throw new InvalidOperationException(
                $"Publish profile '{profile.Name}' uses a protocol IDE++ can't run. " +
                "Only folder and container profiles can be published from here.");
        }

        return _buildService.RunDotnetAsync(
            PublishArguments.Build(projectPath, profile, targetFramework),
            Path.GetDirectoryName(projectPath) ?? string.Empty,
            onOutputLine,
            cancellationToken);
    }
}
