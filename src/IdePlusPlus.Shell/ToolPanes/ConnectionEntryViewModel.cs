using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Connections;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Wraps one DiscoveredConnection for display - RedactedConnectionString is what's shown;
/// CopyConnectionStringCommand is the one place the raw value is read, an explicit user action. For
/// a SQL-Server-shaped connection, this also doubles as the root of a lazily-loaded database/table/
/// column tree (see SqlObjectNode): nothing is queried until the entry is expanded.
/// </summary>
public sealed partial class ConnectionEntryViewModel : ObservableObject
{
    private readonly DiscoveredConnection _connection;
    private readonly SqlSchemaService _sqlSchemaService;
    private bool _schemaLoadStarted;

    public ConnectionEntryViewModel(DiscoveredConnection connection, SqlSchemaService sqlSchemaService)
    {
        _connection = connection;
        _sqlSchemaService = sqlSchemaService;
    }

    public string Name => _connection.Name;

    public string RawConnectionString => _connection.RawConnectionString;

    public string RedactedConnectionString => ConnectionStringRedactor.Redact(_connection.RawConnectionString);

    public string SourceFilePath => _connection.SourceFilePath;

    public string? ProjectPath => _connection.ProjectPath;

    public string ProjectDisplayName =>
        _connection.ProjectPath is { } path ? Path.GetFileNameWithoutExtension(path) : "(solution)";

    public ConnectionSourceKind SourceKind => _connection.SourceKind;

    public bool CanBrowseSchema => SqlConnectionStringSniffer.LooksLikeSqlServer(_connection.RawConnectionString);

    public ObservableCollection<SqlObjectNode> DatabaseNodes { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isLoadingSchema;

    [ObservableProperty]
    private string? schemaStatusMessage;

    /// <summary>Fired by OpenSourceFileCommand - wired to MainWindowViewModel.OpenPath by
    /// MainWindowViewModel itself, the same callback-delegate pattern already used for
    /// TaskExplorer.OutputLine, since this pane has no reference to the shell's own view model.</summary>
    public Action<string>? OpenFileRequested { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_schemaLoadStarted && CanBrowseSchema)
        {
            _schemaLoadStarted = true;
            _ = LoadDatabasesAsync();
        }
    }

    private async Task LoadDatabasesAsync()
    {
        IsLoadingSchema = true;
        try
        {
            var (success, databases, error) = await _sqlSchemaService.ListDatabasesAsync(_connection.RawConnectionString);
            if (!success)
            {
                SchemaStatusMessage = error;
                return;
            }

            if (databases.Count == 0)
            {
                SchemaStatusMessage = "No user databases found.";
                return;
            }

            foreach (var database in databases)
            {
                DatabaseNodes.Add(SqlObjectNode.ForDatabase(database, _connection.RawConnectionString, _sqlSchemaService));
            }
        }
        finally
        {
            IsLoadingSchema = false;
        }
    }

    [RelayCommand]
    private void CopyConnectionString() => Clipboard.SetText(RawConnectionString);

    private bool CanOpenSourceFile() => SourceFilePath.Length > 0;

    [RelayCommand(CanExecute = nameof(CanOpenSourceFile))]
    private void OpenSourceFile() => OpenFileRequested?.Invoke(SourceFilePath);
}
