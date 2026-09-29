using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Backs AddConnectionDialog: a name and a connection string, nothing else. The owning Window reads
/// Name/ConnectionString directly off this DataContext when its own Add button is clicked and sets
/// its own DialogResult - no extra confirmation event needed (same pattern as AddPackageDialog).
/// </summary>
public sealed partial class AddConnectionDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string name = "";

    [ObservableProperty]
    private string connectionString = "";
}
