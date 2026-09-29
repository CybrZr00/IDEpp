using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Templates;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Installs and removes template packages, so a template that isn't on the machine can be fetched
/// without leaving the IDE.
///
/// <para>Only user-installed packages can be removed - the SDK's own templates come from its
/// installation directory and are the installer's business, so they are listed for context and their
/// Uninstall is disabled rather than being offered and then failing.</para>
/// </summary>
public sealed partial class TemplatePackagesDialogViewModel : ObservableObject
{
    private readonly TemplatePackageService _packages;
    private readonly string? _workingDirectory;

    public TemplatePackagesDialogViewModel(TemplatePackageService packages, string? workingDirectory)
    {
        _packages = packages;
        _workingDirectory = workingDirectory;
        Refresh();
    }

    public ObservableCollection<TemplatePackage> Packages { get; } = [];

    [ObservableProperty]
    private TemplatePackage? selectedPackage;

    [ObservableProperty]
    private string packageToInstall = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage =
        "Enter a NuGet package id - or an id::version, or a path to a folder or .nupkg.";

    /// <summary>True when anything was installed or removed, so the caller knows to re-read the
    /// template list rather than doing it unconditionally.</summary>
    public bool PackagesChanged { get; private set; }

    public bool CanInstall => !IsBusy && !string.IsNullOrWhiteSpace(PackageToInstall);

    public bool CanUninstall => !IsBusy && SelectedPackage is { IsUserInstalled: true };

    partial void OnIsBusyChanged(bool value) => NotifyCommandsChanged();

    partial void OnPackageToInstallChanged(string value) => NotifyCommandsChanged();

    partial void OnSelectedPackageChanged(TemplatePackage? value) => NotifyCommandsChanged();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var package = PackageToInstall.Trim();
        await RunAsync(
            $"Installing {package}...",
            () => _packages.InstallAsync(package, _workingDirectory),
            $"Installed {package}.");

        PackageToInstall = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (SelectedPackage is not { } package)
        {
            return;
        }

        await RunAsync(
            $"Removing {package.PackageId}...",
            () => _packages.UninstallAsync(package.PackageId, _workingDirectory),
            $"Removed {package.PackageId}.");
    }

    private async Task RunAsync(string busyMessage, Func<Task<TemplatePackageResult>> work, string successMessage)
    {
        IsBusy = true;
        StatusMessage = busyMessage;
        try
        {
            var result = await work();

            if (result.Succeeded)
            {
                PackagesChanged = true;
                StatusMessage = successMessage;
                Refresh();
            }
            else
            {
                // The CLI's own message, not a paraphrase: "package not found" and "no such version"
                // are different problems and only it knows which happened.
                StatusMessage = FirstMeaningfulLine(result.Output);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FirstMeaningfulLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Length > 0)
            ?? "That didn't work, and the command said nothing about why.";

    private void NotifyCommandsChanged()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    private void Refresh()
    {
        Packages.Clear();
        foreach (var package in _packages.Installed())
        {
            Packages.Add(package);
        }
    }
}
