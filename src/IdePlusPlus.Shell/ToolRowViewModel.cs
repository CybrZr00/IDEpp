using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Shell;

/// <summary>One installable reverse-engineering toolchain row in the Settings → Tools section.</summary>
public sealed partial class ToolRowViewModel(ExternalTool tool) : ObservableObject
{
    public string Id => tool.Id;

    public string DisplayName => tool.DisplayName;

    public string Description => tool.Description;

    public ExternalTool Tool => tool;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    private ToolchainState state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool isBusy;

    [ObservableProperty]
    private string? progress;

    public string StateText => State switch
    {
        ToolchainState.Installed => "Installed by IDE++",
        ToolchainState.DetectedExternally => "Detected on this machine",
        _ => "Not installed",
    };

    public string InstallButtonText => State == ToolchainState.NotInstalled ? "Install" : "Reinstall";

    public bool CanInstall => !IsBusy;
}
