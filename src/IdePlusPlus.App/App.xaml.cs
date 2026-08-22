using System.Windows;
using IdePlusPlus.Build;
using IdePlusPlus.Editor;
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
        services.AddSingleton<WelcomeDocumentViewModel>();
        services.AddSingleton<EditorWorkspace>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<RunService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
