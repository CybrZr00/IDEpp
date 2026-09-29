using IdePlusPlus.PluginContracts;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

/// <summary>One build-event handler a plugin registered via IPluginHost.RegisterBuildEventHandler.</summary>
public sealed record PluginBuildEventHandler(
    string PluginName,
    BuildEventKind Kind,
    Func<BuildEventContext, CancellationToken, Task> Execute);

/// <summary>
/// Tracks build-lifecycle handlers plugins register, keyed by the owning plugin's name (same
/// rationale as <see cref="PluginKeyBindingRegistry"/>), and runs them when the shell reaches the
/// matching point.
///
/// Two containment rules, both there so a plugin cannot make the IDE unable to build: a handler
/// that throws is logged and skipped rather than failing the build, and one that has not returned
/// within <see cref="HandlerTimeout"/> is abandoned. Abandoned rather than killed - there is no way
/// to kill arbitrary in-process work - so its token is cancelled and the build moves on without it.
/// </summary>
public sealed class PluginBuildEventRegistry
{
    /// <summary>Long enough for real work (a codegen step, a file sync), short enough that a hung
    /// handler is a one-off annoyance rather than a permanently broken Build button.</summary>
    public static readonly TimeSpan DefaultHandlerTimeout = TimeSpan.FromMinutes(2);

    private readonly OutputPaneViewModel _output;
    private readonly TimeSpan _handlerTimeout;
    private readonly List<PluginBuildEventHandler> _handlers = [];

    /// <summary>The timeout is a constructor parameter purely so tests can assert the timeout path
    /// without waiting two minutes for it.</summary>
    public PluginBuildEventRegistry(OutputPaneViewModel output, TimeSpan? handlerTimeout = null)
    {
        _output = output;
        _handlerTimeout = handlerTimeout ?? DefaultHandlerTimeout;
    }

    public IReadOnlyList<PluginBuildEventHandler> Handlers => _handlers;

    public void Register(string pluginName, BuildEventKind kind, Func<BuildEventContext, CancellationToken, Task> handler) =>
        _handlers.Add(new PluginBuildEventHandler(pluginName, kind, handler));

    /// <summary>Runs every handler registered for <paramref name="context"/>'s event, one at a
    /// time in registration order. Never throws.</summary>
    public async Task DispatchAsync(BuildEventContext context)
    {
        foreach (var handler in _handlers.Where(h => h.Kind == context.Kind))
        {
            await RunOneAsync(handler, context);
        }
    }

    private async Task RunOneAsync(PluginBuildEventHandler handler, BuildEventContext context)
    {
        using var timeout = new CancellationTokenSource(_handlerTimeout);
        try
        {
            var work = handler.Execute(context, timeout.Token);

            // WaitAsync rather than just awaiting: a handler that ignores its token would otherwise
            // hold the build open for as long as it liked, timeout or no timeout.
            await work.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _output.AppendLine(
                $"[Plugin] {handler.PluginName}'s {context.Kind} handler did not finish in time - continuing without it.");
        }
        catch (Exception ex)
        {
            _output.AppendLine($"[Plugin] {handler.PluginName}'s {context.Kind} handler threw: {ex}");
        }
    }
}
