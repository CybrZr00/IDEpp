using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.LmStudio;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Debug;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Run;
using IdePlusPlus.Shell;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.SourceControl;
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
        EditorServices.GoToLine = new GoToLineRequest();

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
        var dispatcher = Dispatcher;
        var mcpServerManager = _serviceProvider.GetRequiredService<McpServerManager>();
        mcpServerManager.OnLog += line => dispatcher.Invoke(() => output.AppendLine(line));
        _ = mcpServerManager.ReconnectAllAsync();

        mainWindow.Show();

        // Fire-and-forget, same as MCP reconnect above - a large solution shouldn't delay the
        // window appearing.
        _ = _serviceProvider.GetRequiredService<MainWindowViewModel>().TryReopenLastSolutionOnStartupAsync();
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
        services.AddSingleton<DebugSession>();
        services.AddSingleton<SecureSettingsStore>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<PluginHost>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<OpenRouterChatClient>();
        services.AddSingleton<LmStudioChatClient>();
        services.AddSingleton<McpServerConfigStore>();
        services.AddSingleton<McpServerManager>();
        services.AddSingleton<DeepSeekFimClient>();
        services.AddSingleton<EditApplicationService>();
        services.AddSingleton<ProjectMemoryStore>();
        services.AddSingleton<LmStudioEmbeddingClient>();
        services.AddSingleton<CodebaseIndexService>();
        services.AddSingleton<GitService>();
        services.AddSingleton<SourceControlPaneViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
