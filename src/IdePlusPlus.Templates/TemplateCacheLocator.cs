namespace IdePlusPlus.Templates;

/// <summary>
/// Finds the <c>templatecache.json</c> that belongs to the SDK which will actually run.
///
/// <para>There is one cache per SDK version, side by side - a stock machine here has 10.0.103,
/// 10.0.201 and 10.0.301 - so picking the wrong one lists templates a different SDK installed. The
/// version comes from <c>dotnet --version</c> run in the solution's own directory, which is what
/// makes a <c>global.json</c> pin come out right.</para>
/// </summary>
public sealed class TemplateCacheLocator
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where the template engine keeps its per-user state. <c>DOTNET_CLI_HOME</c> wins when set -
    /// verified by pointing it at an empty directory and watching the engine build a fresh cache
    /// there - which is also how this is testable without touching the real profile.
    /// </summary>
    public static string CliHome =>
        Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string CachePathFor(string cliHome, string sdkVersion) =>
        Path.Combine(cliHome, ".templateengine", "dotnetcli", sdkVersion, "templatecache.json");

    private static string CacheRoot(string cliHome) =>
        Path.Combine(cliHome, ".templateengine", "dotnetcli");

    /// <summary>
    /// The cache path for the SDK that <paramref name="contextDirectory"/> resolves to, or - when the
    /// SDK can't be asked - the newest cache already on disk. Null when there is no cache directory
    /// at all, which is the case this class cannot fix and <see cref="TemplateCatalog"/> handles by
    /// priming.
    /// </summary>
    public async Task<string?> LocateAsync(string? contextDirectory, CancellationToken cancellationToken = default)
    {
        var cliHome = CliHome;

        if (await ResolveSdkVersionAsync(contextDirectory, cancellationToken) is { } sdkVersion)
        {
            var path = CachePathFor(cliHome, sdkVersion);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Either dotnet isn't on PATH, or that SDK has a cache directory but no cache yet. Falling
        // back to the newest cache present beats showing no templates at all: the template packages
        // are shared between SDK versions, so a neighbouring cache is close to right.
        return NewestExistingCache(cliHome);
    }

    /// <summary>The SDK version string, honoring global.json by asking from the solution's own
    /// directory. Null when dotnet isn't installed or didn't answer.</summary>
    public async Task<string?> ResolveSdkVersionAsync(
        string? contextDirectory, CancellationToken cancellationToken = default)
    {
        var result = await DotnetCliRunner.RunAsync(
            ["--version"],
            contextDirectory ?? Environment.CurrentDirectory,
            VersionTimeout,
            cancellationToken);

        if (result is not { ExitCode: 0 })
        {
            return null;
        }

        var version = result.Output.Trim();

        // A global.json pinning an absent SDK makes `dotnet --version` print an error and still exit
        // 0 in some SDK builds; anything with whitespace in it is prose, not a version.
        return version.Length > 0 && !version.Any(char.IsWhiteSpace) ? version : null;
    }

    private static string? NewestExistingCache(string cliHome)
    {
        var root = CacheRoot(cliHome);
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var newest = OrderByVersionDescending(
                    Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>())
                .Select(name => CachePathFor(cliHome, name))
                .FirstOrDefault(File.Exists);

            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Newest SDK first. Version-aware rather than ordinal, because "10.0.103" sorts after
    /// "10.0.99" as text and before it as a version. Pre-release suffixes are dropped for the
    /// comparison and only break ties, which is the right way round: 10.0.300 beats
    /// 10.0.300-preview.
    /// </summary>
    public static IReadOnlyList<string> OrderByVersionDescending(IEnumerable<string> directoryNames) =>
        directoryNames
            .OrderByDescending(name => Version.TryParse(name.Split('-')[0], out var version) ? version : new Version(0, 0))
            .ThenByDescending(name => !name.Contains('-'))
            .ThenByDescending(name => name, StringComparer.Ordinal)
            .ToList();
}
