using System.Net.Http;
using System.Windows;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Debug;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Run;
using IdePlusPlus.Shell;
using IdePlusPlus.Shell.ToolPanes;
using Microsoft.Extensions.DependencyInjection;

namespace IdePlusPlus.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Must happen before any code in the process touches a Microsoft.Build.* type -
        // see the comment on SolutionLoader.EnsureMSBuildRegistered for why.
        SolutionLoader.EnsureMSBuildRegistered();

        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // TextEditorHost is constructed by WPF via an implicit DataTemplate (a parameterless
        // constructor), not DI, so it reaches these two app-wide singletons through the static
        // EditorServices locator instead. Must be set before the first document tab renders.
        EditorServices.BreakpointManager = _serviceProvider.GetRequiredService<BreakpointManager>();
        EditorServices.DebugLocation = _serviceProvider.GetRequiredService<DebugLocationIndicator>();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SolutionExplorerPaneViewModel>();
        services.AddSingleton<OutputPaneViewModel>();
        services.AddSingleton<CopilotChatPaneViewModel>();
        services.AddSingleton<CallStackPaneViewModel>();
        services.AddSingleton<LocalsPaneViewModel>();
        services.AddSingleton<WelcomeDocumentViewModel>();
        services.AddSingleton<EditorWorkspace>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<RunService>();
        services.AddSingleton<BreakpointManager>();
        services.AddSingleton<DebugLocationIndicator>();
        services.AddSingleton<DebugSession>();
        services.AddSingleton<SecureSettingsStore>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<OpenRouterChatClient>();
        services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<OpenRouterChatClient>());
        services.AddSingleton<EditApplicationService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
