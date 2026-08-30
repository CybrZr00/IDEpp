namespace IdePlusPlus.Templates;

/// <summary>
/// The installed templates, ready to list.
///
/// <para>Locate the SDK's cache, read it, and - when there isn't one - make the SDK build one by
/// running <c>dotnet new list</c> once and reading again. That priming step is why this can work on
/// a machine where <c>dotnet new</c> has never been run: verified against an empty
/// <c>DOTNET_CLI_HOME</c>, a single <c>new list</c> creates the cache as a side effect. It costs a
/// second or so; every read afterwards is a JSON parse, which is the entire reason for not shelling
/// out per listing.</para>
///
/// <para>Every failure ends as an empty list plus one line on <see cref="OnLog"/>. A missing SDK, a
/// cache in a format this doesn't know, a locked file - none of them are worth an exception out of a
/// path that runs when someone opens a menu.</para>
/// </summary>
public sealed class TemplateCatalog
{
    private static readonly TimeSpan PrimeTimeout = TimeSpan.FromMinutes(2);

    private readonly TemplateCacheLocator _locator = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);

    private IReadOnlyList<TemplateInfo> _templates = [];
    private string? _cachedPath;
    private DateTime _cachedWriteTimeUtc;
    private bool _primed;

    /// <summary>Progress and failure messages, for the Output pane. Same shape as
    /// JavaScriptTaskDiscoveryService.OnLog.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// Every installed template. Memoized against the cache file's last-write time, so installing a
    /// template package is picked up without a restart while an ordinary listing costs nothing.
    /// </summary>
    public async Task<IReadOnlyList<TemplateInfo>> GetTemplatesAsync(
        string? contextDirectory = null, CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            var path = await _locator.LocateAsync(contextDirectory, cancellationToken);

            if (path is null && !_primed)
            {
                path = await PrimeAsync(contextDirectory, cancellationToken);
            }

            if (path is null)
            {
                OnLog?.Invoke("No .NET template cache found - is the .NET SDK installed and on PATH?");
                return _templates = [];
            }

            var writeTimeUtc = SafeLastWriteTimeUtc(path);
            if (path == _cachedPath && writeTimeUtc == _cachedWriteTimeUtc)
            {
                return _templates;
            }

            var templates = TemplateCacheReader.ReadFile(path);
            if (templates is null)
            {
                OnLog?.Invoke($"Couldn't read the .NET template cache at {path} - templates won't be listed.");
                return _templates = [];
            }

            _cachedPath = path;
            _cachedWriteTimeUtc = writeTimeUtc;
            return _templates = templates;
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>The rows to show, deduplicated and ordered - see <see cref="TemplateGrouping"/> for
    /// why that is not a formatting concern.</summary>
    public async Task<IReadOnlyList<TemplateGroup>> GetGroupsAsync(
        TemplateKind? kind = null, string? contextDirectory = null, CancellationToken cancellationToken = default) =>
        TemplateGrouping.Group(await GetTemplatesAsync(contextDirectory, cancellationToken), kind);

    /// <summary>Forces the next read to go back to disk. For after installing a template package,
    /// where the cache is rewritten but its path doesn't change.</summary>
    public void Invalidate()
    {
        _cachedPath = null;
        _cachedWriteTimeUtc = default;
        _primed = false;
    }

    /// <summary>
    /// Makes the SDK write its cache by asking it to list templates, then looks again. Attempted once
    /// per catalog: if it didn't work the first time, it is not going to start working on the next
    /// menu open, and re-running a two-minute command every time would be worse than the missing
    /// list.
    /// </summary>
    private async Task<string?> PrimeAsync(string? contextDirectory, CancellationToken cancellationToken)
    {
        _primed = true;
        OnLog?.Invoke("Finding .NET templates for the first time...");

        var result = await DotnetCliRunner.RunAsync(
            ["new", "list"],
            contextDirectory ?? Environment.CurrentDirectory,
            PrimeTimeout,
            cancellationToken);

        if (result is null)
        {
            OnLog?.Invoke("Couldn't run dotnet - is the .NET SDK installed and on PATH?");
            return null;
        }

        return await _locator.LocateAsync(contextDirectory, cancellationToken);
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Forces a re-read rather than trusting a stale memo.
            return DateTime.MinValue;
        }
    }
}
