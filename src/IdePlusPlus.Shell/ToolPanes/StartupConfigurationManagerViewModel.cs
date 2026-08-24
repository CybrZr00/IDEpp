using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Run;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>Display label shown for "use whatever profile Load's default resolution picks" -
/// <see cref="StartupConfigurationEntryRowViewModel.SelectedLaunchProfileDisplayName"/> maps this
/// to/from a null <see cref="StartupConfigurationEntry.LaunchProfileName"/>.</summary>
public static class LaunchProfileDisplay
{
    public const string DefaultProfileLabel = "(Default)";
}

/// <summary>One project's row in the Startup Configuration Manager's editor grid for a single
/// <see cref="StartupConfiguration"/>.</summary>
public sealed partial class StartupConfigurationEntryRowViewModel : ObservableObject
{
    public StartupConfigurationEntryRowViewModel(string projectPath, string projectName, StartupAction action, string? launchProfileName)
    {
        ProjectPath = projectPath;
        ProjectName = projectName;
        this.action = action;

        var profileNames = LaunchSettingsLoader.LoadProfileNames(projectPath).Select(p => p.Name).ToList();
        AvailableLaunchProfileDisplayNames = new ObservableCollection<string>([LaunchProfileDisplay.DefaultProfileLabel, .. profileNames]);
        selectedLaunchProfileDisplayName = launchProfileName is not null && profileNames.Contains(launchProfileName)
            ? launchProfileName
            : LaunchProfileDisplay.DefaultProfileLabel;
    }

    public string ProjectPath { get; }

    public string ProjectName { get; }

    /// <summary>False when this row's project no longer exists in the currently loaded solution
    /// (renamed/removed since the configuration was saved) - shown greyed-out/badged rather than
    /// silently dropped, per the design's edge-case handling.</summary>
    public bool ProjectExistsInSolution { get; init; } = true;

    [ObservableProperty]
    private StartupAction action;

    public IReadOnlyList<StartupAction> AvailableActions { get; } = [StartupAction.None, StartupAction.Start, StartupAction.StartWithoutDebugging];

    public ObservableCollection<string> AvailableLaunchProfileDisplayNames { get; }

    [ObservableProperty]
    private string selectedLaunchProfileDisplayName;

    /// <summary>The value to persist into <see cref="StartupConfigurationEntry.LaunchProfileName"/> -
    /// null when the display selection is the "(Default)" sentinel.</summary>
    public string? LaunchProfileName =>
        SelectedLaunchProfileDisplayName == LaunchProfileDisplay.DefaultProfileLabel ? null : SelectedLaunchProfileDisplayName;

    public StartupConfigurationEntry ToModel() => new(ProjectPath, Action, LaunchProfileName);
}

/// <summary>One named, saved <see cref="StartupConfiguration"/> being edited in the manager dialog -
/// always has exactly one row per project currently in the solution, defaulting unmatched projects to
/// <see cref="StartupAction.None"/> so a project added after the configuration was last saved shows up
/// (unincluded) rather than being invisible.</summary>
public sealed partial class StartupConfigurationEditorViewModel : ObservableObject
{
    public StartupConfigurationEditorViewModel(string name, bool isActive, ObservableCollection<StartupConfigurationEntryRowViewModel> rows)
    {
        this.name = name;
        this.isActive = isActive;
        Rows = rows;
    }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private bool isActive;

    public ObservableCollection<StartupConfigurationEntryRowViewModel> Rows { get; }

    public StartupConfiguration ToModel() => new(Name, Rows.Select(r => r.ToModel()).ToList());
}

/// <summary>
/// Backs the Startup Configuration Manager dialog: lets the user create/duplicate/delete/rename named
/// startup configurations, edit each one's per-project Action/launch-profile/order, and pick which one
/// is active. Every configuration is seeded with one row per project in the currently loaded solution
/// (see <see cref="StartupConfigurationEditorViewModel"/>), so editing never needs to special-case a
/// project that isn't mentioned in a configuration's saved entries yet.
/// </summary>
public sealed partial class StartupConfigurationManagerViewModel : ObservableObject
{
    private readonly IReadOnlyList<(string Path, string Name)> _solutionProjects;

    public StartupConfigurationManagerViewModel(
        IReadOnlyList<(string Path, string Name)> solutionProjects,
        IReadOnlyList<StartupConfiguration> configurations,
        string activeName)
    {
        _solutionProjects = solutionProjects;

        foreach (var configuration in configurations)
        {
            Configurations.Add(BuildEditor(configuration, isActive: configuration.Name == activeName));
        }

        selectedConfiguration = Configurations.FirstOrDefault(c => c.IsActive) ?? Configurations.FirstOrDefault();
    }

    public ObservableCollection<StartupConfigurationEditorViewModel> Configurations { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DuplicateConfigurationCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteConfigurationCommand))]
    private StartupConfigurationEditorViewModel? selectedConfiguration;

    public IReadOnlyList<StartupConfiguration> BuildResultConfigurations() => Configurations.Select(c => c.ToModel()).ToList();

    public string ActiveConfigurationName => (Configurations.FirstOrDefault(c => c.IsActive) ?? Configurations[0]).Name;

    private StartupConfigurationEditorViewModel BuildEditor(StartupConfiguration configuration, bool isActive)
    {
        var entriesByPath = configuration.Entries.ToDictionary(e => e.ProjectPath);
        var rows = new ObservableCollection<StartupConfigurationEntryRowViewModel>();

        // Existing entries first, in their saved order, then any solution project not yet represented -
        // preserves the user's saved ordering rather than resetting it to solution order every edit.
        foreach (var entry in configuration.Entries)
        {
            var projectName = _solutionProjects.FirstOrDefault(p => p.Path == entry.ProjectPath).Name;
            rows.Add(new StartupConfigurationEntryRowViewModel(entry.ProjectPath, projectName ?? Path.GetFileNameWithoutExtension(entry.ProjectPath), entry.Action, entry.LaunchProfileName)
            {
                ProjectExistsInSolution = projectName is not null,
            });
        }

        foreach (var project in _solutionProjects.Where(p => !entriesByPath.ContainsKey(p.Path)))
        {
            rows.Add(new StartupConfigurationEntryRowViewModel(project.Path, project.Name, StartupAction.None, launchProfileName: null));
        }

        return new StartupConfigurationEditorViewModel(configuration.Name, isActive, rows);
    }

    [RelayCommand]
    private void NewConfiguration()
    {
        var rows = new ObservableCollection<StartupConfigurationEntryRowViewModel>(
            _solutionProjects.Select(p => new StartupConfigurationEntryRowViewModel(p.Path, p.Name, StartupAction.None, launchProfileName: null)));
        var editor = new StartupConfigurationEditorViewModel(GenerateUniqueName("New Configuration"), isActive: false, rows);
        Configurations.Add(editor);
        SelectedConfiguration = editor;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConfiguration))]
    private void DuplicateConfiguration()
    {
        if (SelectedConfiguration is not { } source)
        {
            return;
        }

        var rows = new ObservableCollection<StartupConfigurationEntryRowViewModel>(
            source.Rows.Select(r => new StartupConfigurationEntryRowViewModel(r.ProjectPath, r.ProjectName, r.Action, r.LaunchProfileName) { ProjectExistsInSolution = r.ProjectExistsInSolution }));
        var editor = new StartupConfigurationEditorViewModel(GenerateUniqueName($"{source.Name} - Copy"), isActive: false, rows);
        Configurations.Add(editor);
        SelectedConfiguration = editor;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedConfiguration))]
    private void DeleteConfiguration()
    {
        if (SelectedConfiguration is not { } toRemove)
        {
            return;
        }

        var wasActive = toRemove.IsActive;
        var index = Configurations.IndexOf(toRemove);
        Configurations.Remove(toRemove);

        var newSelection = Configurations.Count > index ? Configurations[index] : Configurations.LastOrDefault();
        if (wasActive && newSelection is not null)
        {
            newSelection.IsActive = true;
        }

        SelectedConfiguration = newSelection;
    }

    private bool HasSelectedConfiguration() => SelectedConfiguration is not null;

    private bool CanDeleteSelectedConfiguration() => SelectedConfiguration is not null && Configurations.Count > 1;

    [RelayCommand]
    private void MoveRowUp(StartupConfigurationEntryRowViewModel? row)
    {
        if (SelectedConfiguration is not { } configuration || row is null)
        {
            return;
        }

        var index = configuration.Rows.IndexOf(row);
        if (index > 0)
        {
            configuration.Rows.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveRowDown(StartupConfigurationEntryRowViewModel? row)
    {
        if (SelectedConfiguration is not { } configuration || row is null)
        {
            return;
        }

        var index = configuration.Rows.IndexOf(row);
        if (index >= 0 && index < configuration.Rows.Count - 1)
        {
            configuration.Rows.Move(index, index + 1);
        }
    }

    private string GenerateUniqueName(string baseName)
    {
        if (Configurations.All(c => c.Name != baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (Configurations.Any(c => c.Name == $"{baseName} ({suffix})"))
        {
            suffix++;
        }

        return $"{baseName} ({suffix})";
    }
}
