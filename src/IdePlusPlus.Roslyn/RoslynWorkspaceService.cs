using System.Collections.Concurrent;
using IdePlusPlus.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace IdePlusPlus.Roslyn;

/// <summary>
/// Owns a background <see cref="MSBuildWorkspace"/> for one solution and implements
/// <see cref="ILiveDiagnosticsProvider"/> on top of it. Loaded independently of, and in parallel
/// with, IdePlusPlus.ProjectSystem.SolutionLoader's own MSBuild pass - the two don't share any
/// state (SolutionLoader immediately unloads its MSBuild Project evaluations after reading file
/// lists, so there is nothing for this workspace to reuse).
///
/// <para><b>Never calls <see cref="Workspace.TryApplyChanges"/>.</b> This was empirically verified
/// (not assumed) while building this class: <see cref="MSBuildWorkspace"/>'s implementation of
/// ApplyChanges writes changed document text straight back to the file on disk - it is designed for
/// command-line tools that want e.g. "format this solution and save the result", not for holding an
/// editor's unsaved buffer. Feeding an in-progress (possibly syntactically broken, definitely
/// unsaved) keystroke-by-keystroke buffer through TryApplyChanges silently overwrites the real file
/// on every push, racing the user's own unsaved edits and destroying anything not yet saved. Instead,
/// in-editor text is tracked purely in memory via <see cref="_currentSolution"/>, a private
/// <see cref="Solution"/> snapshot derived from the workspace's loaded solution via
/// <c>WithDocumentText</c> and never hooked back into the <see cref="Microsoft.CodeAnalysis.Workspace"/>
/// itself - Solution is an immutable value-like snapshot, so this is a safe, disk-free way to layer
/// unsaved edits on top of what MSBuildWorkspace loaded from disk.</para>
///
/// <para>Threading invariant: a Roslyn <see cref="Microsoft.CodeAnalysis.Workspace"/> (and, by the
/// same rule applied here, this class's own <see cref="_currentSolution"/> snapshot) is not safe to
/// mutate from more than one thread at a time. Every code path that reassigns
/// <see cref="_currentSolution"/> or the <c>_workspace</c> field itself (on reload) goes through
/// <see cref="_workspaceLock"/> - callers may call <see cref="NotifyDocumentOpened"/>/
/// <see cref="NotifyDocumentChanged"/> from the UI thread and this class serializes the resulting
/// updates onto its own background work.</para>
/// </summary>
public sealed class RoslynWorkspaceService : ILiveDiagnosticsProvider, IDisposable
{
    // Chosen to sit comfortably above a single keystroke's inter-arrival time (TextEditorHost's own
    // ghost-text debounce is 350ms) without feeling laggy - recomputing a semantic model on every
    // keystroke would be wasteful for a multi-thousand-line file.
    private const int DiagnosticsDebounceMilliseconds = 300;

    private readonly SemaphoreSlim _workspaceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DocumentId> _documentIdsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    private MSBuildWorkspace _workspace;
    private WorkspaceEventRegistration _workspaceFailedRegistration = null!;

    // In-memory-only overlay of unsaved editor text on top of whatever MSBuildWorkspace last loaded
    // from disk - see the "Never calls TryApplyChanges" remark above for why this exists instead of
    // just using _workspace.CurrentSolution directly.
    private Solution _currentSolution;
    private bool _disposed;

    public RoslynWorkspaceService()
    {
        _workspace = CreateWorkspace();
        _currentSolution = _workspace.CurrentSolution;
    }

    /// <inheritdoc />
    public event Action<string, IReadOnlyList<LiveDiagnostic>>? DiagnosticsChanged;

    /// <summary>
    /// Raised (never thrown) whenever MSBuildWorkspace fails to load or bind an individual project
    /// or document - a bad/unrestored project should degrade that one project's IntelliSense, not
    /// take the whole workspace, or the app, down.
    /// </summary>
    public event Action<string>? WorkspaceFailed;

    private MSBuildWorkspace CreateWorkspace()
    {
        var workspace = MSBuildWorkspace.Create();
        _workspaceFailedRegistration = workspace.RegisterWorkspaceFailedHandler(OnWorkspaceFailed);
        return workspace;
    }

    private void OnWorkspaceFailed(WorkspaceDiagnosticEventArgs e) =>
        WorkspaceFailed?.Invoke(e.Diagnostic.Message);

    /// <summary>
    /// Loads (or reloads) a solution. Safe to call again for a solution reload: MSBuildWorkspace is
    /// explicitly documented as not supporting being pointed at a second, unrelated solution on the
    /// same instance, so a reload disposes the old workspace and creates a fresh one before opening.
    /// Genuinely cancellable via <paramref name="cancellationToken"/> - OpenSolutionAsync itself
    /// respects it, and this method never swallows the resulting OperationCanceledException.
    /// </summary>
    public async Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _workspaceFailedRegistration.Dispose();
            _workspace.Dispose();
            _workspace = CreateWorkspace();

            var solution = await _workspace
                .OpenSolutionAsync(solutionPath, progress: null, cancellationToken)
                .ConfigureAwait(false);
            _currentSolution = solution;

            var documentIdsByPath = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is { } path)
                    {
                        // First project to claim a path wins. Relevant for multi-targeted projects
                        // and linked/shared files, where the same source file can produce more than
                        // one DocumentId - MVP only needs *a* usable document for diagnostics/
                        // completion, not one per TFM (see design doc's deferred-items list).
                        documentIdsByPath.TryAdd(path, document.Id);
                    }
                }
            }

            _documentIdsByPath.Clear();
            foreach (var (path, id) in documentIdsByPath)
            {
                _documentIdsByPath[path] = id;
            }
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <inheritdoc />
    public void NotifyDocumentOpened(string filePath, string text) => ScheduleDiagnostics(filePath, text);

    /// <inheritdoc />
    public void NotifyDocumentChanged(string filePath, string text) => ScheduleDiagnostics(filePath, text);

    /// <inheritdoc />
    public void NotifyDocumentClosed(string filePath)
    {
        // MVP no-op: the workspace keeps whatever text was last pushed for this file rather than
        // reverting to the on-disk copy on close. Nothing currently re-reads a closed document's
        // diagnostics, so this is harmless for now (see design doc's deferred-items list).
    }

    /// <summary>
    /// Applies <paramref name="text"/> to the document immediately (no debounce - a completion
    /// request needs the just-typed character reflected right away) and returns the resulting
    /// Roslyn <see cref="Document"/>, or null if <paramref name="filePath"/> isn't a document this
    /// workspace loaded. Internal: only RoslynCompletionService (same assembly) calls this - it is
    /// the one place a Roslyn-typed value crosses out of this class, and it never crosses the
    /// IdePlusPlus.Roslyn/IdePlusPlus.Editor boundary.
    ///
    /// Deliberately updates only the in-memory <see cref="_currentSolution"/> snapshot, never
    /// <c>_workspace.TryApplyChanges</c> - see this class's type-level remarks for why calling
    /// TryApplyChanges here would silently overwrite the real file on disk with an unsaved buffer.
    /// </summary>
    internal async Task<Document?> GetDocumentWithTextAsync(string filePath, string text, CancellationToken cancellationToken)
    {
        if (!_documentIdsByPath.TryGetValue(filePath, out var documentId))
        {
            return null;
        }

        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _currentSolution = _currentSolution.WithDocumentText(documentId, SourceText.From(text), PreservationMode.PreserveIdentity);
            return _currentSolution.GetDocument(documentId);
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    /// <summary>
    /// Diagnostics for one file, computed now rather than announced later.
    ///
    /// Everything else here is push-shaped: an edit arrives, a debounce elapses, DiagnosticsChanged
    /// fires, and the Error List pane redraws. That works for a pane watching the screen and not at
    /// all for a caller that wants an answer - which is what the copilot's get_diagnostics tool
    /// needs. Same semantic model and the same LiveDiagnosticMapper as the push path, so the two
    /// cannot disagree about what the compiler thinks.
    ///
    /// <paramref name="unsavedText"/> layers the editor's current buffer over what MSBuildWorkspace
    /// loaded from disk, so a file with unsaved changes is analysed as the user sees it. Returns an
    /// empty list for a path this workspace never loaded - a file outside the solution, or one added
    /// since it was opened.
    /// </summary>
    public async Task<IReadOnlyList<LiveDiagnostic>> GetDiagnosticsAsync(
        string filePath, string? unsavedText = null, CancellationToken cancellationToken = default)
    {
        if (!_documentIdsByPath.TryGetValue(filePath, out var documentId))
        {
            return [];
        }

        Document? document;
        if (unsavedText is not null)
        {
            document = await GetDocumentWithTextAsync(filePath, unsavedText, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                document = _currentSolution.GetDocument(documentId);
            }
            finally
            {
                _workspaceLock.Release();
            }
        }

        if (document is null)
        {
            return [];
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return [];
        }

        return semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Select(d => LiveDiagnosticMapper.ToLiveDiagnostic(d, filePath))
            .ToList();
    }

    /// <summary>
    /// Declarations whose name matches <paramref name="name"/>, anywhere in the loaded solution.
    ///
    /// Matches the symbol's own name, not its fully-qualified one - "GetStatusAsync", not
    /// "IdePlusPlus.SourceControl.GitService.GetStatusAsync" - because a caller looking for a symbol
    /// usually knows what it is called and not where it lives. That is the whole reason to ask.
    /// </summary>
    public async Task<IReadOnlyList<SymbolLocation>> FindSymbolsAsync(
        string name, int maxResults, CancellationToken cancellationToken = default)
    {
        var solution = await GetSolutionSnapshotAsync(cancellationToken).ConfigureAwait(false);

        var symbols = await SymbolFinder
            .FindSourceDeclarationsAsync(solution, name, ignoreCase: false, cancellationToken)
            .ConfigureAwait(false);

        return symbols
            .SelectMany(symbol => symbol.Locations
                .Where(l => l.IsInSource)
                .Select(location => ToSymbolLocation(symbol.Kind.ToString(), Describe(symbol), location)))
            .Where(l => l is not null)
            .Select(l => l!)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Every reference to the named symbol.
    ///
    /// Returns the matching declarations too, and leaves it to the caller what to do when there is
    /// more than one: overload sets and same-named members on unrelated types are both common, and
    /// silently picking the first would answer a question nobody asked. Both lists are capped by
    /// <paramref name="maxResults"/> - "used in 400 places" is a fact worth having, but 400 file
    /// positions are not.
    /// </summary>
    public async Task<(IReadOnlyList<SymbolLocation> Declarations, IReadOnlyList<SymbolLocation> References, int TotalReferences)>
        FindReferencesAsync(string name, int maxResults, CancellationToken cancellationToken = default)
    {
        var solution = await GetSolutionSnapshotAsync(cancellationToken).ConfigureAwait(false);

        var symbols = (await SymbolFinder
            .FindSourceDeclarationsAsync(solution, name, ignoreCase: false, cancellationToken)
            .ConfigureAwait(false)).ToList();

        var declarations = symbols
            .SelectMany(symbol => symbol.Locations
                .Where(l => l.IsInSource)
                .Select(location => ToSymbolLocation(symbol.Kind.ToString(), Describe(symbol), location)))
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

        if (symbols.Count != 1)
        {
            return (declarations, [], 0);
        }

        var references = new List<SymbolLocation>();
        foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbols[0], solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (var location in referenced.Locations)
            {
                if (ToSymbolLocation("Reference", Describe(symbols[0]), location.Location) is { } mapped)
                {
                    references.Add(mapped);
                }
            }
        }

        return (declarations, references.Take(maxResults).ToList(), references.Count);
    }

    private async Task<Solution> GetSolutionSnapshotAsync(CancellationToken cancellationToken)
    {
        // Solution is an immutable snapshot, so the lock only has to cover reading the field - the
        // search itself runs outside it and cannot be disturbed by a concurrent edit push.
        await _workspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _currentSolution;
        }
        finally
        {
            _workspaceLock.Release();
        }
    }

    private static string Describe(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    /// <summary>Null when the location has no source file or its line cannot be read - a symbol
    /// from metadata, or a generated tree with no path.</summary>
    private static SymbolLocation? ToSymbolLocation(string kind, string displayName, Location location)
    {
        if (location.SourceTree?.FilePath is not { Length: > 0 } filePath)
        {
            return null;
        }

        var span = location.GetLineSpan();
        var lineIndex = span.StartLinePosition.Line;

        var lineText = "";
        var text = location.SourceTree.GetText();
        if (lineIndex >= 0 && lineIndex < text.Lines.Count)
        {
            lineText = text.Lines[lineIndex].ToString().Trim();
        }

        return new SymbolLocation(
            kind, displayName, filePath, lineIndex + 1, span.StartLinePosition.Character + 1, lineText);
    }

    /// <summary>Whether a solution has been loaded and this file is part of it - lets a caller say
    /// "not analysed" rather than reporting a clean bill of health for a file nobody looked at.</summary>
    public bool IsAnalysed(string filePath) => _documentIdsByPath.ContainsKey(filePath);

    private void ScheduleDiagnostics(string filePath, string text)
    {
        if (!_documentIdsByPath.ContainsKey(filePath))
        {
            return;
        }

        // Cancel whatever debounce/recompute is already in flight for this file - only the most
        // recent edit's diagnostics are worth computing.
        if (_pendingDiagnostics.TryRemove(filePath, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pendingDiagnostics[filePath] = cts;
        _ = RecomputeDiagnosticsAfterDebounceAsync(filePath, text, cts);
    }

    private async Task RecomputeDiagnosticsAfterDebounceAsync(string filePath, string text, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DiagnosticsDebounceMilliseconds, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Document? document;
        try
        {
            document = await GetDocumentWithTextAsync(filePath, text, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (document is null || cts.IsCancellationRequested)
        {
            return;
        }

        List<LiveDiagnostic> diagnostics;
        try
        {
            var semanticModel = await document.GetSemanticModelAsync(cts.Token).ConfigureAwait(false);
            if (semanticModel is null)
            {
                return;
            }

            diagnostics = semanticModel.GetDiagnostics(cancellationToken: cts.Token)
                .Select(d => LiveDiagnosticMapper.ToLiveDiagnostic(d, filePath))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        // Only clear the in-flight marker for this exact debounce - if a newer edit already
        // replaced it in the dictionary, leave that newer one alone.
        _pendingDiagnostics.TryRemove(new KeyValuePair<string, CancellationTokenSource>(filePath, cts));

        DiagnosticsChanged?.Invoke(filePath, diagnostics);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var cts in _pendingDiagnostics.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _workspaceFailedRegistration.Dispose();
        _workspace.Dispose();
        _workspaceLock.Dispose();
    }
}
