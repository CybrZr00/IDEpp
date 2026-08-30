namespace IdePlusPlus.Tasks;

/// <summary>What the user is being asked to approve, so the prompt can name the actual files.</summary>
public sealed record JavaScriptTaskTrustRequest(string SolutionDirectory, IReadOnlyList<string> ManifestPaths);

/// <summary>
/// Turns found gulpfiles and Gruntfiles into listed tasks, but only for a solution the user has
/// approved - and caches the result so a Refresh does not re-execute a gulpfile that has not
/// changed.
///
/// The approval gate is the reason this is a service rather than another static class like the rest
/// of discovery: it holds the trust store, the prompt, and the cache, and it is the single place
/// that decides whether repository code is allowed to run.
/// </summary>
public sealed class JavaScriptTaskDiscoveryService
{
    private readonly JavaScriptTaskTrustStore _trustStore;

    /// <summary>Keyed by manifest path; the value pins the file state the listing came from, so an
    /// edited gulpfile is re-listed and an untouched one is not.</summary>
    private readonly Dictionary<string, (DateTime WriteTimeUtc, long Length, JavaScriptTaskListResult Result)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public JavaScriptTaskDiscoveryService(JavaScriptTaskTrustStore trustStore) => _trustStore = trustStore;

    /// <summary>
    /// Asks the user whether this solution's JavaScript manifests may be executed to list their
    /// tasks. Set by the composition root, exactly as McpServerManager.ProjectTrustPrompt is. Left
    /// null - in a headless context, say - nothing is ever approved, which fails closed.
    /// </summary>
    public Func<JavaScriptTaskTrustRequest, Task<bool>>? TrustPrompt { get; set; }

    /// <summary>Log sink for what was skipped and why; the pane routes it to the Output pane.</summary>
    public Action<string>? OnLog { get; set; }

    public bool IsTrusted(string solutionDirectory, IReadOnlyList<string> manifestPaths) =>
        ReadFingerprint(manifestPaths) is { } fingerprint && _trustStore.IsTrusted(solutionDirectory, fingerprint);

    /// <summary>
    /// Prompts for approval and records it. Returns whether discovery may now run. Called from an
    /// explicit user action, never automatically on solution load: an unprompted dialog every time a
    /// repo with a gulpfile is opened would train people to click through it, which is the opposite
    /// of consent.
    /// </summary>
    public async Task<bool> RequestTrustAsync(string solutionDirectory, IReadOnlyList<string> manifestPaths)
    {
        if (ReadFingerprint(manifestPaths) is not { } fingerprint)
        {
            OnLog?.Invoke("[Tasks] Could not read this solution's gulpfile/Gruntfile to check it - not scanning.");
            return false;
        }

        if (_trustStore.IsTrusted(solutionDirectory, fingerprint))
        {
            return true;
        }

        if (TrustPrompt is not { } prompt)
        {
            OnLog?.Invoke("[Tasks] Skipped gulp/grunt discovery - it has not been approved for this solution.");
            return false;
        }

        if (!await prompt(new JavaScriptTaskTrustRequest(solutionDirectory, manifestPaths)))
        {
            OnLog?.Invoke("[Tasks] Declined - gulp/grunt tasks will not be listed for this solution.");
            return false;
        }

        _trustStore.Trust(solutionDirectory, fingerprint);
        return true;
    }

    /// <summary>
    /// Lists each manifest's tasks, executing it only if the solution is currently approved. Returns
    /// a manifest per input, with an empty task list and a reported error where listing failed - a
    /// gulpfile that throws should say so rather than looking like a gulpfile with no tasks.
    /// </summary>
    public async Task<IReadOnlyList<TaskManifest>> ListTasksAsync(
        string solutionDirectory,
        IReadOnlyList<(string Path, TaskManifestKind Kind)> manifests,
        CancellationToken cancellationToken = default)
    {
        if (manifests.Count == 0 || !IsTrusted(solutionDirectory, manifests.Select(m => m.Path).ToList()))
        {
            return [];
        }

        var results = new List<TaskManifest>();
        foreach (var (path, kind) in manifests)
        {
            var listing = await ListOneAsync(path, kind, cancellationToken);
            if (listing.Error is { } error)
            {
                OnLog?.Invoke($"[Tasks] {Path.GetFileName(path)}: {error}");
            }

            results.Add(new TaskManifest(
                kind,
                path,
                PackageManagerDetector.Detect(path, packageManagerField: null),
                listing.TaskNames.Select(name => new TaskDefinition(kind, path, name, Command: null)).ToList()));
        }

        return results;
    }

    private async Task<JavaScriptTaskListResult> ListOneAsync(
        string path, TaskManifestKind kind, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return JavaScriptTaskListResult.Failed("the file is no longer there.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return JavaScriptTaskListResult.Failed($"could not be read: {ex.Message}");
        }

        if (_cache.TryGetValue(path, out var cached)
            && cached.WriteTimeUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached.Result;
        }

        var result = kind == TaskManifestKind.Grunt
            ? await GruntTaskDiscovery.ListTasksAsync(path, cancellationToken)
            : await GulpTaskDiscovery.ListTasksAsync(path, cancellationToken);

        _cache[path] = (info.LastWriteTimeUtc, info.Length, result);
        return result;
    }

    /// <summary>Null when any manifest cannot be read - an unreadable file must not hash to the same
    /// thing as an empty one, or a fingerprint could match across a change.</summary>
    private static string? ReadFingerprint(IReadOnlyList<string> manifestPaths)
    {
        var contents = new List<(string Path, string Text)>();
        foreach (var path in manifestPaths)
        {
            try
            {
                contents.Add((path, File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        return JavaScriptTaskTrustStore.Fingerprint(contents);
    }

    /// <summary>
    /// The bindings a manifest's own Visual Studio comment asks for, mapped onto IDE++'s events.
    /// Import only: IDE++ never writes this comment back, so a repo configured in VS works here on
    /// first open without IDE++ editing the user's source file.
    /// </summary>
    public IReadOnlyList<TaskBinding> ImportVisualStudioBindings(string solutionDirectory, string manifestPath)
    {
        if (VsBindingCommentReader.Read(manifestPath) is not { } comment)
        {
            return [];
        }

        if (comment.UnsupportedProjectOpenTasks.Count > 0)
        {
            OnLog?.Invoke(
                $"[Tasks] {Path.GetFileName(manifestPath)}: Visual Studio binds " +
                $"{string.Join(", ", comment.UnsupportedProjectOpenTasks)} to Project Open, which IDE++ has no " +
                "equivalent of - not imported.");
        }

        var relative = TaskBindingStore.ToRelativePath(solutionDirectory, manifestPath);
        return comment.Bindings.Select(b => new TaskBinding(relative, b.TaskName, b.Kind)).ToList();
    }
}
