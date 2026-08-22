using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.NuGet;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Shell.Views;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Per-project installed-package list backed by NuGetService, plus an Add Package dialog that
/// searches nuget.org. Mirrors SourceControlPaneViewModel's shape (an IsBusy-gated set of
/// RelayCommands, re-running the underlying "list" query after every mutation rather than trusting
/// an optimistic in-memory update - see the CPM shared-file risk in the design doc).
/// </summary>
public sealed partial class NuGetPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "NuGet";

    private readonly NuGetService _nuGetService;
    private readonly NuGetSearchClient _nuGetSearchClient;
    private string? _csprojPath;

    public NuGetPaneViewModel(NuGetService nuGetService, NuGetSearchClient nuGetSearchClient)
        : base(PaneContentId, "NuGet")
    {
        _nuGetService = nuGetService;
        _nuGetSearchClient = nuGetSearchClient;
    }

    public ObservableCollection<InstalledPackage> InstalledPackages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemovePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenAddPackageDialogCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? projectDisplayName;

    public async Task SetProjectAsync(SolutionTreeNode? project)
    {
        _csprojPath = project?.FullPath;
        InstalledPackages.Clear();
        StatusMessage = null;
        ProjectDisplayName = project?.Name;

        RefreshCommand.NotifyCanExecuteChanged();
        RemovePackageCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        OpenAddPackageDialogCommand.NotifyCanExecuteChanged();

        if (_csprojPath is not null)
        {
            await RefreshAsync();
        }
    }

    private bool CanRunCommand() => !IsBusy && _csprojPath is not null;

    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private async Task RefreshAsync()
    {
        if (_csprojPath is null)
        {
            return;
        }

        var packages = await _nuGetService.ListPackagesAsync(_csprojPath);
        InstalledPackages.Clear();
        foreach (var package in packages)
        {
            InstalledPackages.Add(package);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private async Task RemovePackageAsync(InstalledPackage package)
    {
        if (_csprojPath is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _nuGetService.RemovePackageAsync(_csprojPath, package.Id);
            StatusMessage = result.Success ? $"Removed {package.Id}." : (result.Output.Length > 0 ? result.Output : "Failed.");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private async Task RestoreAsync()
    {
        if (_csprojPath is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (success, output) = await _nuGetService.RestoreAsync(_csprojPath, line => StatusMessage = line);
            StatusMessage = success ? "Restore completed." : (output.Length > 0 ? output : "Restore failed.");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    /// <summary>Constructs and shows the modal Add Package dialog directly - this codebase has no
    /// IDialogService abstraction anywhere (SettingsWindow/GoToLineWindow are shown the same
    /// direct way, just from code-behind instead); doing it here keeps the "search, pick, confirm,
    /// then AddPackageAsync+Refresh" flow in one place.</summary>
    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private async Task OpenAddPackageDialogAsync()
    {
        if (_csprojPath is null)
        {
            return;
        }

        var dialogViewModel = new AddPackageDialogViewModel(_nuGetSearchClient);
        var dialog = new AddPackageDialog(dialogViewModel) { Owner = Application.Current?.MainWindow };

        if (dialog.ShowDialog() != true || dialogViewModel.SelectedResult is not { } selected)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var version = string.IsNullOrWhiteSpace(dialogViewModel.SelectedVersion) ? null : dialogViewModel.SelectedVersion.Trim();
            var (success, output) = await _nuGetService.AddPackageAsync(_csprojPath, selected.Id, version, line => StatusMessage = line);
            StatusMessage = success ? $"Added {selected.Id}." : (output.Length > 0 ? output : "Failed.");
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }
}
