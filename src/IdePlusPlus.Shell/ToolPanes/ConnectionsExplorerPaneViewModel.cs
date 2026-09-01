using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Connections;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.Views;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Lists connection strings discovered across the open solution's config files (appsettings*.json,
/// web.config/app.config, .env), grouped by project, with passwords redacted - plus any
/// manually-added connections, whose values are held in SecureSettingsStore rather than plaintext.
/// Mirrors NuGetPaneViewModel's shape: an IsBusy-gated set of RelayCommands, re-running the
/// underlying scan after a mutation rather than trusting an optimistic in-memory patch.
/// </summary>
public sealed partial class ConnectionsExplorerPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "ConnectionsExplorer";

    private readonly SecureSettingsStore _secureSettingsStore;
    private readonly SqlSchemaService _sqlSchemaService;

    private string? _solutionDirectory;
    private IReadOnlyList<string> _solutionFilePaths = [];
    private IReadOnlyList<(string ProjectPath, string ProjectDirectory)> _projects = [];

    public ConnectionsExplorerPaneViewModel(SecureSettingsStore secureSettingsStore, SqlSchemaService sqlSchemaService)
        : base(PaneContentId, "Connections")
    {
        _secureSettingsStore = secureSettingsStore;
        _sqlSchemaService = sqlSchemaService;
    }

    public ObservableCollection<ConnectionEntryViewModel> Connections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddConnectionCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    /// <summary>Set by MainWindowViewModel (to OpenPath) and handed down to every entry this pane
    /// creates - see ConnectionEntryViewModel.OpenFileRequested.</summary>
    public Action<string>? OpenFileRequested { get; set; }

    public async Task SetSolutionAsync(
        string? solutionDirectory,
        IReadOnlyList<string> solutionFilePaths,
        IReadOnlyList<(string ProjectPath, string ProjectDirectory)> projects)
    {
        _solutionDirectory = solutionDirectory;
        _solutionFilePaths = solutionFilePaths;
        _projects = projects;
        RefreshCommand.NotifyCanExecuteChanged();
        AddConnectionCommand.NotifyCanExecuteChanged();
        await RefreshAsync();
    }

    private bool CanRunCommand() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunCommand))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var solutionFilePaths = _solutionFilePaths;
            var projects = _projects;
            var discovered = await Task.Run(() => ConnectionStringScanner.Scan(solutionFilePaths, projects));

            var entries = discovered
                .Select(d => new ConnectionEntryViewModel(d, _sqlSchemaService))
                .ToList();

            if (_solutionDirectory is { } solutionDirectory)
            {
                foreach (var stored in ConnectionsStore.Load(solutionDirectory))
                {
                    if (_secureSettingsStore.GetSecret($"Connection:{stored.Id}") is { } connectionString)
                    {
                        entries.Add(new ConnectionEntryViewModel(
                            new DiscoveredConnection(stored.Name, connectionString, "", null, ConnectionSourceKind.UserAdded),
                            _sqlSchemaService));
                    }
                }
            }

            Connections.Clear();
            foreach (var entry in entries.OrderBy(e => e.ProjectDisplayName).ThenBy(e => e.Name))
            {
                entry.OpenFileRequested = OpenFileRequested;
                Connections.Add(entry);
            }

            StatusMessage = Connections.Count == 0 ? "No connection strings found in this solution." : null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAddConnection() => !IsBusy && _solutionDirectory is not null;

    /// <summary>Constructs and shows the modal Add Connection dialog directly - this codebase has no
    /// IDialogService abstraction anywhere (AddPackageDialog is shown the same direct way from its
    /// owning pane view model).</summary>
    [RelayCommand(CanExecute = nameof(CanAddConnection))]
    private async Task AddConnectionAsync()
    {
        var solutionDirectory = _solutionDirectory!;
        var dialogViewModel = new AddConnectionDialogViewModel();
        var dialog = new AddConnectionDialog(dialogViewModel) { Owner = Application.Current?.MainWindow };

        if (dialog.ShowDialog() != true
            || string.IsNullOrWhiteSpace(dialogViewModel.Name)
            || string.IsNullOrWhiteSpace(dialogViewModel.ConnectionString))
        {
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        _secureSettingsStore.SetSecret($"Connection:{id}", dialogViewModel.ConnectionString.Trim());

        var existing = ConnectionsStore.Load(solutionDirectory).ToList();
        existing.Add(new StoredConnection(id, dialogViewModel.Name.Trim()));
        ConnectionsStore.Save(solutionDirectory, existing);

        await RefreshAsync();
    }
}
