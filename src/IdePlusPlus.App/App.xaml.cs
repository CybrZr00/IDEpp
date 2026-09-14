using System.IO;
using System.Linq;
using System.Net.Http;
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
        var mcpServerManager = _serviceProvider.GetRequiredService<McpServerManager>();
        mcpServerManager.OnLog += line => dispatcher.Invoke(() => output.AppendLine(line));
        mcpServerManager.ProjectTrustPrompt = request =>
            dispatcher.Invoke(() => Task.FromResult(ConfirmProjectMcpServers(mainWindow, request)));

        var copilotChatPane = _serviceProvider.GetRequiredService<CopilotChatPaneViewModel>();
        copilotChatPane.ShellCommandApprovalPrompt =
            (command, reason) => dispatcher.Invoke(() => Task.FromResult(ConfirmShellCommand(mainWindow, command, reason)));
        copilotChatPane.WebFetchApprovalPrompt =
            (url, reason) => dispatcher.Invoke(() => Task.FromResult(ConfirmWebFetch(mainWindow, url, reason)));

        // Priming the SDK's template cache on a machine that has never run `dotnet new` takes a
        // second or two and is otherwise invisible, so it says so in the Output pane.
        _serviceProvider.GetRequiredService<TemplateCatalog>().OnLog +=
            line => dispatcher.Invoke(() => output.AppendLine(line));

        var javaScriptTaskDiscovery = _serviceProvider.GetRequiredService<JavaScriptTaskDiscoveryService>();
        javaScriptTaskDiscovery.OnLog += line => dispatcher.Invoke(() => output.AppendLine(line));
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
            request.Servers.Select(s => $"    {s.Name}:  {s.Command} {string.Join(' ', s.Args)}".TrimEnd()));

        var result = MessageBox.Show(
            owner,
            $"""
             This solution ships its own MCP server configuration:

             {request.ConfigPath}

             Opening it would run these commands on your machine:

             {commands}

             Only allow this if you trust the source of this solution. You will be asked again if
             the file changes.
             """,
            "IDE++ - run this solution's MCP servers?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
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
        var files = string.Join(Environment.NewLine, request.ManifestPaths.Select(path => $"    {path}"));

        var result = MessageBox.Show(
            owner,
            $"""
             Listing this solution's gulp/grunt tasks means running these files:

             {files}

             That executes whatever they do when loaded, as you, with your permissions. Only allow
             this if you trust the source of this solution. You will be asked again if they change.
             """,
            "IDE++ - run this solution's task files?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Asks before the copilot runs a shell command. The command line is shown verbatim and never
    /// summarized: the exact text is the only thing worth consenting to, and a paraphrase of it is
    /// precisely how something unwanted gets waved through. Defaults to No.
    /// </summary>
    private static bool ConfirmShellCommand(Window owner, string command, string reason)
    {
        var result = MessageBox.Show(
            owner,
            $"""
             The AI assistant wants to run a command on your machine:

             {command}

             Its reason: {reason}

             It runs as you, in the solution directory, with your permissions. Only allow it if the
             command above is something you would be willing to run yourself.
             """,
            "IDE++ - run this command?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Asked before the copilot fetches a new host. The URL is shown verbatim, same rationale as
    /// ConfirmShellCommand - a paraphrase ("a documentation page") is not something anyone can
    /// meaningfully consent to. Defaults to No.
    /// </summary>
    private static bool ConfirmWebFetch(Window owner, string url, string reason)
    {
        var result = MessageBox.Show(
            owner,
            $"""
             The AI assistant wants to fetch a webpage:

             {url}

             Its reason: {reason}

             Local, private-network, and cloud-metadata addresses are always refused regardless of this
             prompt. Only allow it if you're comfortable with the assistant reading this page - remember
             that whatever it says becomes part of the conversation.
             """,
            "IDE++ - fetch this page?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
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
        services.AddSingleton<OpenRouterChatClient>();
        services.AddSingleton<LmStudioChatClient>();
        services.AddSingleton<McpServerConfigStore>();
        services.AddSingleton<McpProjectTrustStore>();
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
        services.AddSingleton<ChatHistoryStore>();
        services.AddSingleton<LmStudioEmbeddingClient>();
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
