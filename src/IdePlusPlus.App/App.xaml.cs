using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.LmStudio;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Connections;
using IdePlusPlus.Debug;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Publish;
using IdePlusPlus.Roslyn;
using IdePlusPlus.Run;
using IdePlusPlus.NuGet;
using IdePlusPlus.PluginContracts;
using IdePlusPlus.Shell;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.Shell.Views;
using IdePlusPlus.SourceControl;
using IdePlusPlus.Tasks;
using IdePlusPlus.Templates;
using IdePlusPlus.TestRunner;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace IdePlusPlus.App;

public partial class App : Application
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "logs", "log-.txt");

    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(LogFilePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        // Wired before anything else so a failure during DI setup or plugin loading is captured
        // too, not just exceptions raised once the main window is up.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Must happen before any code in the process touches a Microsoft.Build.* type -
        // see the comment on SolutionLoader.EnsureMSBuildRegistered for why.
        SolutionLoader.EnsureMSBuildRegistered();

        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // TextEditorHost is constructed by WPF via an implicit DataTemplate (a parameterless
        // constructor), not DI, so it reaches these app-wide singletons through the static
        // EditorServices locator instead. Must be set before the first document tab renders.
        EditorServices.BreakpointManager = _serviceProvider.GetRequiredService<BreakpointManager>();
        EditorServices.DebugLocation = _serviceProvider.GetRequiredService<DebugLocationIndicator>();
        EditorServices.CaretPosition = _serviceProvider.GetRequiredService<CaretPositionIndicator>();
        EditorServices.CompletionProvider = _serviceProvider.GetRequiredService<DeepSeekFimClient>();
        EditorServices.DiagnosticsProvider = _serviceProvider.GetRequiredService<RoslynWorkspaceService>();
        EditorServices.MemberCompletionProvider = _serviceProvider.GetRequiredService<RoslynCompletionService>();
        EditorServices.DefinitionProvider = _serviceProvider.GetRequiredService<RoslynDefinitionService>();
        EditorServices.GoToLine = new GoToLineRequest();
        EditorServices.DiffPreview = new DiffPreviewRequest();

        // Both delegates re-query PluginHost fresh on every invocation (not memoized here), so it
        // doesn't matter that PluginLoader.LoadPlugins() below hasn't run yet - by the time either
        // one is actually invoked (a right-click, a mouse hover), every plugin's registrations from
        // startup are already in place.
        var pluginHost = _serviceProvider.GetRequiredService<PluginHost>();
        EditorServices.EditorContextMenuCommands = () => pluginHost.EditorContextMenuCommands
            .Select(c => new EditorContextMenuCommandDescriptor(
                c.Header,
                state => c.CanExecute(new EditorContextMenuContext(state.FilePath, state.SelectedText, state.Line, state.Column)),
                state => c.Execute(new EditorContextMenuContext(state.FilePath, state.SelectedText, state.Line, state.Column))))
            .ToList();
        EditorServices.HoverProviderLookup = extension => pluginHost.HoverProviders.TryGetValue(extension, out var provider)
            ? (request, ct) => provider.GetHoverTextAsync(new PluginHoverContext(request.FilePath, request.Line, request.Column, request.Word), ct)
            : null;

        // Applies the saved theme before the first window renders, to avoid a light-then-dark flash.
        _serviceProvider.GetRequiredService<ThemeService>().Initialize();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();

        // After MainWindowViewModel exists (plugins can add Tools > Plugins commands to it) but
        // before Show(), so a plugin-registered command is present in the menu from the first frame.
        _serviceProvider.GetRequiredService<PluginLoader>().LoadPlugins();

        // MCP servers (npx-launched ones especially) can take a while to come up - connect in the
        // background rather than blocking startup; GetToolDefinitions() just returns fewer tools
        // until they're ready.
        var output = _serviceProvider.GetRequiredService<OutputPaneViewModel>();
        var mainWindowViewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();

        // Ctrl+Click go-to-definition's cross-file half: EditorDefinitionOutcome.Found only carries
        // a file/line/column, since IdePlusPlus.Editor can't reference Shell to open a document
        // itself - this composes MainWindowViewModel.OpenPath (which also brings the tab to front)
        // with EditorServices.GoToLine.Request, the same two steps DiffPreviewRequest's own remarks
        // describe as already proven to work together for a file that wasn't already open.
        EditorServices.OpenAndGoTo = (filePath, line, column) =>
        {
            if (mainWindowViewModel.OpenPath(filePath) is { } document)
            {
                EditorServices.GoToLine.Request(document, line, column);
            }
        };
        EditorServices.StatusLog = line => output.AppendLine(line);

        var dispatcher = Dispatcher;

        // From here on the Output pane is written from background threads (MCP stderr, plugin logs,
        // task processes). The pane updates its buffers under a lock on the caller's thread and
        // marshals only the UI refresh, so every logger - including PluginHost.LogInfo, which was
        // never marshalled before - is safe without each call site wrapping in dispatcher.Invoke.
        output.Post = action => dispatcher.BeginInvoke(action);

        var mcpServerManager = _serviceProvider.GetRequiredService<McpServerManager>();
        mcpServerManager.OnLog += line => output.AppendLine(OutputPaneViewModel.McpChannel, line);
        _serviceProvider.GetRequiredService<McpServersPaneViewModel>().Post = action => dispatcher.BeginInvoke(action);
        mcpServerManager.ProjectTrustPrompt = request =>
            dispatcher.Invoke(() => Task.FromResult(ConfirmProjectMcpServers(mainWindow, request)));

        // Resolved before the chat pane: SetSolutionContext (which loads a solution's hooks and may
        // prompt) can run as soon as the pane exists, and the prompt has to be wired by then.
        var hookService = _serviceProvider.GetRequiredService<HookService>();
        hookService.OnLog += line => output.AppendLine(OutputPaneViewModel.PluginsChannel, line);
        hookService.ProjectTrustPrompt = request =>
            dispatcher.Invoke(() => Task.FromResult(ConfirmProjectHooks(mainWindow, request)));

        var copilotChatPane = _serviceProvider.GetRequiredService<CopilotChatPaneViewModel>();
        copilotChatPane.ShellCommandApprovalPrompt =
            (command, reason) => dispatcher.Invoke(() => Task.FromResult(ConfirmShellCommand(mainWindow, command, reason)));
        copilotChatPane.TeamShellCommandApprovalPrompt =
            (command, reason) => dispatcher.Invoke(() => Task.FromResult(ConfirmTeamShellCommand(mainWindow, command, reason)));
        copilotChatPane.WebFetchApprovalPrompt =
            (url, reason) => dispatcher.Invoke(() => Task.FromResult(ConfirmWebFetch(mainWindow, url, reason)));
        copilotChatPane.ReverseEngineeringApprovalPrompt =
            path => dispatcher.Invoke(() => Task.FromResult(ConfirmReverseEngineering(mainWindow, path)));
        copilotChatPane.NewConversationConfirmation = (warning, canAcceptAll) =>
            ConfirmNewConversation(mainWindow, warning, canAcceptAll);
        copilotChatPane.LaterTeamBranchPrompt = (chosen, later, laterCommits) => ConfirmUltraStartFrom(mainWindow, chosen, later, laterCommits);
        copilotChatPane.UltraBuildFailedPrompt = report => ConfirmUltraBuildFailed(mainWindow, report);

        // Priming the SDK's template cache on a machine that has never run `dotnet new` takes a
        // second or two and is otherwise invisible, so it says so in the Output pane.
        _serviceProvider.GetRequiredService<TemplateCatalog>().OnLog +=
            line => output.AppendLine(line);

        var javaScriptTaskDiscovery = _serviceProvider.GetRequiredService<JavaScriptTaskDiscoveryService>();
        javaScriptTaskDiscovery.OnLog += line => output.AppendLine(OutputPaneViewModel.TasksChannel, line);
        javaScriptTaskDiscovery.TrustPrompt = request =>
            dispatcher.Invoke(() => Task.FromResult(ConfirmJavaScriptTaskDiscovery(mainWindow, request)));
        _ = mcpServerManager.ReconnectAllAsync();

        mainWindow.Show();

        // Fire-and-forget, same as MCP reconnect above - a large solution shouldn't delay the
        // window appearing.
        _ = mainWindowViewModel.TryReopenLastSolutionOnStartupAsync();
    }

    /// <summary>
    /// Asks before running the servers a solution ships in its own MCP config. The commands are
    /// listed in full rather than summarized: "this repository wants to run three MCP servers" is
    /// not something anyone can meaningfully consent to, whereas the actual command lines are.
    /// Defaults to No, and a dismissed dialog is a No.
    /// </summary>
    private static bool ConfirmProjectMcpServers(Window owner, McpProjectTrustRequest request)
    {
        var commands = string.Join(
            Environment.NewLine,
            request.Servers.Select(s => $"{s.Name}:  {s.Command} {string.Join(' ', s.Args)}".TrimEnd()));

        return ApprovalDialog.Ask(
            owner,
            "IDE++ - run this solution's MCP servers?",
            $"This solution ships its own MCP server configuration:{Environment.NewLine}{Environment.NewLine}"
            + $"{request.ConfigPath}{Environment.NewLine}{Environment.NewLine}"
            + "Opening it would run these commands on your machine:",
            commands,
            warning: "Only allow this if you trust the source of this solution. You will be asked again if "
                     + "the file changes.");
    }

    /// <summary>
    /// Asks before running the hooks a solution ships in its own settings file. A hook runs on your
    /// machine whenever the assistant edits a file or calls a tool, so the actual command lines are
    /// listed rather than summarized. Defaults to No, and a dismissed dialog is a No.
    /// </summary>
    private static bool ConfirmProjectHooks(Window owner, HookTrustRequest request)
    {
        return ApprovalDialog.Ask(
            owner,
            "IDE++ - run this solution's hooks?",
            $"This solution ships its own hooks:{Environment.NewLine}{Environment.NewLine}"
            + $"{request.ConfigPath}{Environment.NewLine}{Environment.NewLine}"
            + "These commands would run on your machine as the AI assistant edits files and calls tools:",
            string.Join(Environment.NewLine, request.CommandLines),
            warning: "Only allow this if you trust the source of this solution. You will be asked again if "
                     + "the file changes.");
    }

    /// <summary>
    /// Asks before listing a solution's gulp/grunt tasks, because listing them runs the file that
    /// defines them - gulp builds its task graph by executing the gulpfile, and there is no way to
    /// enumerate tasks without it. The files are named in full for the same reason MCP's prompt
    /// lists commands: "scan this solution's task files" is not something anyone can meaningfully
    /// consent to. Defaults to No, and a dismissed dialog is a No.
    /// </summary>
    private static bool ConfirmJavaScriptTaskDiscovery(Window owner, JavaScriptTaskTrustRequest request)
    {
        return ApprovalDialog.Ask(
            owner,
            "IDE++ - run this solution's task files?",
            "Listing this solution's gulp/grunt tasks means running these files:",
            string.Join(Environment.NewLine, request.ManifestPaths),
            warning: "That executes whatever they do when loaded, as you, with your permissions. Only allow "
                     + "this if you trust the source of this solution. You will be asked again if they change.");
    }

    /// <summary>
    /// New Chat's warning about what it would throw away. When edits are among it, "Accept all and
    /// exit" applies them first instead - the way out for a pile of reviews that would otherwise have
    /// to be clicked through one by one. Defaults to Cancel.
    /// </summary>
    private static NewConversationChoice ConfirmNewConversation(Window owner, string warning, bool canAcceptAll)
    {
        var dialog = ApprovalDialog.Create(
            owner, "IDE++ - new conversation", "Start a new conversation?", warning);
        dialog.AllowButton.Content = "_Discard and start new";
        dialog.DenyButton.Content = "_Cancel";
        if (canAcceptAll)
        {
            dialog.OfferAllowAll("Accept _all and exit");
        }

        if (dialog.ShowDialog() != true)
        {
            return NewConversationChoice.Cancel;
        }

        return dialog.Choice == ApprovalChoice.AllowAll
            ? NewConversationChoice.AcceptAllThenStart
            : NewConversationChoice.Discard;
    }

    /// <summary>
    /// Asks before the copilot runs a shell command. The command line is shown verbatim and never
    /// summarized: the exact text is the only thing worth consenting to, and a paraphrase of it is
    /// precisely how something unwanted gets waved through. Defaults to No.
    /// </summary>
    private static bool ConfirmShellCommand(Window owner, string command, string reason)
    {
        return ApprovalDialog.Ask(
            owner,
            "IDE++ - run this command?",
            "The AI assistant wants to run a command on your machine:",
            command,
            note: $"Its reason: {reason}",
            warning: "It runs as you, in the solution directory, with your permissions. Only allow it if the "
                     + "command above is something you would be willing to run yourself.");
    }

    /// <summary>
    /// Asked before the copilot opens a binary in Ghidra. The full path is shown verbatim. Analyzing a
    /// binary runs Ghidra (Java) over it on this machine; the path is the thing to consent to. No.
    /// </summary>
    private static bool ConfirmReverseEngineering(Window owner, string path)
    {
        return ApprovalDialog.Ask(
            owner,
            "IDE++ - analyze this binary?",
            "The AI assistant wants to open and analyze a binary with Ghidra:",
            path,
            warning: "Ghidra imports and disassembles the file on your machine. Only allow it for a file you "
                     + "mean to reverse engineer.");
    }

    /// <summary>
    /// ConfirmShellCommand for an Ultra team's workers, plus a way to stop being asked: several
    /// workers in parallel can otherwise want one approval after another for the whole run. "Allow
    /// all" is scoped to that one run and its warning says so - it is still the exact command shown
    /// that the user is looking at when they choose it. Defaults to No.
    /// </summary>
    private static ApprovalChoice ConfirmTeamShellCommand(Window owner, string command, string reason)
    {
        return ApprovalDialog.AskWithAllowAll(
            owner,
            "IDE++ - run this command?",
            "A worker on the Ultra team wants to run a command on your machine:",
            command,
            "Allow all for this _run",
            note: $"Its reason: {reason}",
            warning: "It runs as you, in that worker's worktree, with your permissions. \"Allow all for this run\" "
                     + "lets every worker on this team run any command, without asking, until the run ends - "
                     + "including any extra rounds you continue it for.");
    }

    /// <summary>
    /// Asked when an Ultra run would carry on from a team branch that a later run already carried on
    /// from. Yes, the default, takes the later branch - it has everything the chosen one does - and
    /// closing the box starts nothing.
    /// </summary>
    private static UltraStartChoice ConfirmUltraStartFrom(Window owner, string chosen, string later, int laterCommits)
    {
        var newLine = Environment.NewLine;
        return MessageBox.Show(
                owner,
                $"{later} already carries on from {chosen}, with {laterCommits} commit(s) of later work on top."
                + $"{newLine}{newLine}Starting from {chosen} leaves that work out, and the team would likely do it again."
                + $"{newLine}{newLine}Yes - start from {later}{newLine}No - start from {chosen} anyway{newLine}Cancel - don't start",
                "IDE++ - carry on from an older run?",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) switch
        {
            MessageBoxResult.Yes => UltraStartChoice.StartFromLater,
            MessageBoxResult.No => UltraStartChoice.StartFromChosen,
            _ => UltraStartChoice.Cancel,
        };
    }

    /// <summary>
    /// Asked when an Ultra message is sent and the solution does not build. Yes, the default, has the
    /// copilot fix the build first - a team can only merge work that builds, and in Plan Mode the plan is
    /// the expensive part. The start of the report is shown so the question is about something real.
    /// </summary>
    private static UltraBuildChoice ConfirmUltraBuildFailed(Window owner, string report)
    {
        var newLine = Environment.NewLine;
        var lines = report.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
        var shown = string.Join(newLine, lines.Take(8).Select(line => line.Length <= 200 ? line : line[..200] + "..."));
        if (lines.Count > 8)
        {
            shown += $"{newLine}... ({lines.Count - 8} more line(s) in the build report)";
        }

        return MessageBox.Show(
                owner,
                "The solution doesn't build as it stands. An Ultra team merges only work that builds, so it "
                + $"can't get anywhere on it - and a plan made now would be wasted.{newLine}{newLine}{shown}{newLine}{newLine}"
                + $"Yes - have the copilot fix the build first (your message is kept for after){newLine}"
                + $"No - go ahead anyway{newLine}Cancel - don't start",
                "IDE++ - the solution doesn't build",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes) switch
        {
            MessageBoxResult.Yes => UltraBuildChoice.FixFirst,
            MessageBoxResult.No => UltraBuildChoice.Continue,
            _ => UltraBuildChoice.Cancel,
        };
    }

    /// <summary>
    /// Asked before the copilot fetches a new host. The URL is shown verbatim, same rationale as
    /// ConfirmShellCommand - a paraphrase ("a documentation page") is not something anyone can
    /// meaningfully consent to. Defaults to No.
    /// </summary>
    private static bool ConfirmWebFetch(Window owner, string url, string reason)
    {
        return ApprovalDialog.Ask(
            owner,
            "IDE++ - fetch this page?",
            "The AI assistant wants to fetch a webpage:",
            url,
            note: $"Its reason: {reason}",
            warning: "Local, private-network, and cloud-metadata addresses are always refused regardless of this "
                     + "prompt. Only allow it if you're comfortable with the assistant reading this page - "
                     + "remember that whatever it says becomes part of the conversation.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
        Log.CloseAndFlush();
    }

    /// <summary>
    /// Logged and swallowed rather than crashing the whole IDE - a single bad exception (e.g. from a
    /// plugin's Tools-menu callback, or a stray binding/command handler) shouldn't take the process
    /// down. Serilog captures the full exception for post-mortem diagnosis regardless of whether the
    /// Output pane exists yet.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread");
        LogToOutputPaneIfAvailable($"[ERROR] Unhandled exception: {e.Exception.Message}");
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Fires on a non-UI thread with no way to prevent termination once e.IsTerminating is true -
        // logging here is purely to leave a forensic trail for whatever crashed the process.
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception on a background thread (IsTerminating={IsTerminating})", e.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void LogToOutputPaneIfAvailable(string message)
    {
        if (_serviceProvider?.GetService<OutputPaneViewModel>() is { } output)
        {
            Dispatcher.Invoke(() => output.AppendLine(message));
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SolutionExplorerPaneViewModel>();
        services.AddSingleton<OutputPaneViewModel>();
        services.AddSingleton<ErrorListPaneViewModel>();
        services.AddSingleton<CopilotChatPaneViewModel>();
        services.AddSingleton<CallStackPaneViewModel>();
        services.AddSingleton<LocalsPaneViewModel>();
        services.AddSingleton<WelcomeDocumentViewModel>();
        services.AddSingleton<EditorWorkspace>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<RunService>();
        services.AddSingleton<BreakpointManager>();
        services.AddSingleton<DebugLocationIndicator>();
        services.AddSingleton<CaretPositionIndicator>();
        services.AddSingleton<DebugSessionManager>();
        services.AddSingleton<SecureSettingsStore>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<PluginToolWindowRegistry>();
        services.AddSingleton<PluginKeyBindingRegistry>();
        services.AddSingleton<PluginChatToolRegistry>();
        services.AddSingleton<PluginBuildEventRegistry>();
        services.AddSingleton<PluginHost>();
        // PluginLoader depends on the IPluginHost interface (not the concrete PluginHost) so it can be
        // unit-tested without constructing a real MainWindowViewModel/WPF Application - resolve it to
        // the same singleton PluginHost instance registered above.
        services.AddSingleton<IPluginHost>(sp => sp.GetRequiredService<PluginHost>());
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<HttpClient>();

        // Chat and embedding requests get their own client with no overall timeout. The shared one above
        // keeps HttpClient's default of 100 seconds, which is far too short for them: a local model that
        // has to load, or work through a long conversation, before its first token routinely takes
        // longer, and the timeout then killed the turn and showed it as "[cancelled]". The connect
        // timeout still fails fast on a server that isn't there, and a stream that goes quiet is
        // given up on by OpenAiCompatibleChat's idle timeout, so nothing can hang forever.
        var modelHttpClient = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        services.AddSingleton(sp => new OpenRouterChatClient(modelHttpClient, sp.GetRequiredService<SecureSettingsStore>()));
        services.AddSingleton(sp => new LmStudioChatClient(
            modelHttpClient, sp.GetRequiredService<AppSettingsStore>(), sp.GetRequiredService<SecureSettingsStore>()));
        services.AddSingleton(sp => new DeepSeekChatClient(modelHttpClient, sp.GetRequiredService<SecureSettingsStore>()));
        services.AddSingleton(sp => new AnthropicChatClient(modelHttpClient, sp.GetRequiredService<SecureSettingsStore>()));
        services.AddSingleton<McpServerConfigStore>();
        services.AddSingleton<McpProjectTrustStore>();
        services.AddSingleton<HookService>();
        services.AddSingleton<McpServerManager>();
        services.AddSingleton<DeepSeekFimClient>();
        services.AddSingleton<EditApplicationService>();
        services.AddSingleton<ProjectMemoryStore>();
        services.AddSingleton<SkillStore>();
        services.AddSingleton<ScheduleStore>();
        // A read_file/list_files call needs to see a file's unsaved editor content where it's open
        // (EditApplicationService never writes an open buffer to disk on its own) - one shared
        // instance, rather than one per consumer, so the chat pane and the scheduler agree on what
        // "current" means for the same file.
        services.AddSingleton(sp => new WorkspaceFileReader(sp.GetRequiredService<EditApplicationService>().TryGetUnsavedText));
        services.AddSingleton<ReadOnlyToolDispatcher>();
        services.AddSingleton<ScheduledAutomationService>();
        services.AddSingleton<ScheduledAutomationsPaneViewModel>();
        services.AddSingleton<McpServersPaneViewModel>();
        services.AddSingleton<BinaryExplorerPaneViewModel>();
        services.AddSingleton<ExternalToolchainStore>();
        services.AddSingleton<ChatHistoryStore>();
        services.AddSingleton(sp => new LmStudioEmbeddingClient(
            modelHttpClient, sp.GetRequiredService<AppSettingsStore>(), sp.GetRequiredService<SecureSettingsStore>()));
        services.AddSingleton(new LocalEmbeddingModelStore());
        services.AddSingleton(sp => new OnnxEmbeddingClient(sp.GetRequiredService<LocalEmbeddingModelStore>(), modelHttpClient));
        services.AddSingleton<IEmbeddingClient>(sp => new EmbeddingClientSelector(
            sp.GetRequiredService<AppSettingsStore>(),
            sp.GetRequiredService<OnnxEmbeddingClient>(),
            sp.GetRequiredService<LmStudioEmbeddingClient>()));
        services.AddSingleton<CodebaseIndexService>();
        services.AddSingleton<GitService>();
        services.AddSingleton<SourceControlPaneViewModel>();
        services.AddSingleton<NuGetService>();
        services.AddSingleton<PublishService>();
        services.AddSingleton<TemplateCatalog>();
        services.AddSingleton<TemplateInstantiationService>();
        services.AddSingleton<TemplatePackageService>();
        services.AddSingleton<NuGetSearchClient>();
        services.AddSingleton<NuGetPaneViewModel>();
        services.AddSingleton<TestDiscoveryService>();
        services.AddSingleton<TestRunService>();
        services.AddSingleton<TestExplorerPaneViewModel>();
        services.AddSingleton<TaskRunService>();
        services.AddSingleton<JavaScriptTaskTrustStore>();
        services.AddSingleton<JavaScriptTaskDiscoveryService>();
        services.AddSingleton<TaskExplorerPaneViewModel>();
        services.AddSingleton<RoslynWorkspaceService>();
        services.AddSingleton<RoslynCompletionService>();
        services.AddSingleton<RoslynDefinitionService>();
        services.AddSingleton<SqlSchemaService>();
        services.AddSingleton<ConnectionsExplorerPaneViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
