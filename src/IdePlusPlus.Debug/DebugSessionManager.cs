using System.Collections.ObjectModel;
using IdePlusPlus.Editor;

namespace IdePlusPlus.Debug;

/// <summary>
/// Owns zero-or-more concurrently running <see cref="DebugSession"/> instances (each with its own
/// <see cref="DapClient"/>/netcoredbg process - already safe to run concurrently with no changes to
/// DapClient itself). Replaces the single always-on DebugSession singleton: MainWindowViewModel
/// subscribes to this manager's events once instead of to a single session's.
/// </summary>
public sealed class DebugSessionManager
{
    private readonly BreakpointManager _breakpointManager;
    private readonly DebugLocationIndicator _debugLocation;

    public DebugSessionManager(BreakpointManager breakpointManager, DebugLocationIndicator debugLocation)
    {
        _breakpointManager = breakpointManager;
        _debugLocation = debugLocation;
    }

    public ObservableCollection<DebugSession> Sessions { get; } = [];

    public DebugSession? ActiveSession { get; private set; }

    /// <summary>Raised (on a background thread) whenever the active session changes.</summary>
    public event Action? ActiveSessionChanged;

    /// <summary>Raised (on a background thread) whenever a session is added to or removed from <see cref="Sessions"/>.</summary>
    public event Action? SessionsChanged;

    /// <summary>Re-raised (on a background thread) whenever any session's own StateChanged fires.</summary>
    public event Action? StateChanged;

    /// <summary>Re-raised (on a background thread) whenever any session's OutputReceived fires, with the originating session.</summary>
    public event Action<DebugSession, string>? OutputReceived;

    public bool IsAnyDebugging => Sessions.Count > 0;

    public bool IsActiveSessionStopped => ActiveSession?.IsStopped ?? false;

    public void SetActiveSession(DebugSession? session)
    {
        if (!ReferenceEquals(ActiveSession, session))
        {
            ActiveSession = session;
            ActiveSessionChanged?.Invoke();
        }
    }

    public async Task<DebugSession> StartAsync(
        string debugAdapterExecutablePath,
        string programPath,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var session = CreateSession();
        await session.StartAsync(debugAdapterExecutablePath, programPath, workingDirectory, environmentVariables);
        AddSession(session);
        return session;
    }

    public async Task<DebugSession> AttachAsync(string debugAdapterExecutablePath, int processId)
    {
        var session = CreateSession();
        await session.AttachAsync(debugAdapterExecutablePath, processId);
        AddSession(session);
        return session;
    }

    public async Task StopAsync(DebugSession session)
    {
        var wasActive = ReferenceEquals(ActiveSession, session);
        await session.StopAsync();

        // The session's own StateChanged handler (wired in CreateSession) already removes it from
        // Sessions once IsActive goes false; if it was the active session, pick a new one now that
        // removal has happened.
        if (wasActive)
        {
            SetActiveSession(Sessions.FirstOrDefault());
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var session in Sessions.ToList())
        {
            await StopAsync(session);
        }
    }

    private DebugSession CreateSession()
    {
        var session = new DebugSession(_breakpointManager, _debugLocation);
        session.StateChanged += () => OnSessionStateChanged(session);
        session.OutputReceived += line => OutputReceived?.Invoke(session, line);
        return session;
    }

    private void AddSession(DebugSession session)
    {
        Sessions.Add(session);
        SetActiveSession(session);
        SessionsChanged?.Invoke();
    }

    private void OnSessionStateChanged(DebugSession session)
    {
        if (!session.IsActive && Sessions.Contains(session))
        {
            Sessions.Remove(session);
            if (ReferenceEquals(ActiveSession, session))
            {
                SetActiveSession(Sessions.FirstOrDefault());
            }

            SessionsChanged?.Invoke();
        }

        StateChanged?.Invoke();
    }
}
