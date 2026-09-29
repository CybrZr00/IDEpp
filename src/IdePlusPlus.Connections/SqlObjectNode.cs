using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Connections;

public enum SqlObjectNodeKind
{
    Database,
    TablesFolder,
    ViewsFolder,
    Table,
    View,
    Column,
}

/// <summary>
/// One node in a connection's database/table/column tree - mirrors SolutionTreeNode's shape (a
/// folder-like grouping kind plus leaf kinds), except children are only populated the first time a
/// node is expanded, via SqlSchemaService, rather than all at once: eagerly enumerating every
/// database's every table's every column up front is not something a real Object Browser does
/// either, and would be needlessly slow and chatty against the server for a large database.
/// </summary>
public sealed partial class SqlObjectNode : ObservableObject
{
    private readonly Func<Task>? _loadChildren;
    private bool _childrenLoadStarted;

    private SqlObjectNode(string name, SqlObjectNodeKind kind, Func<Task>? loadChildren)
    {
        Name = name;
        Kind = kind;
        _loadChildren = loadChildren;
    }

    public string Name { get; }

    public SqlObjectNodeKind Kind { get; }

    public ObservableCollection<SqlObjectNode> Children { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? loadError;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoadStarted && _loadChildren is not null)
        {
            _childrenLoadStarted = true;
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await _loadChildren!();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public static SqlObjectNode ForDatabase(string databaseName, string connectionString, SqlSchemaService schemaService)
    {
        SqlObjectNode? node = null;
        node = new SqlObjectNode(databaseName, SqlObjectNodeKind.Database, async () =>
        {
            var (success, tables, error) = await schemaService.ListTablesAsync(connectionString, databaseName);
            if (!success)
            {
                node!.LoadError = error;
                return;
            }

            var tablesFolder = new SqlObjectNode("Tables", SqlObjectNodeKind.TablesFolder, null);
            var viewsFolder = new SqlObjectNode("Views", SqlObjectNodeKind.ViewsFolder, null);

            foreach (var table in tables.Where(t => !t.IsView))
            {
                tablesFolder.Children.Add(ForTable(table, databaseName, connectionString, schemaService));
            }

            foreach (var view in tables.Where(t => t.IsView))
            {
                viewsFolder.Children.Add(ForTable(view, databaseName, connectionString, schemaService));
            }

            if (tablesFolder.Children.Count > 0)
            {
                node!.Children.Add(tablesFolder);
            }

            if (viewsFolder.Children.Count > 0)
            {
                node!.Children.Add(viewsFolder);
            }
        });

        return node;
    }

    private static SqlObjectNode ForTable(
        SqlTableInfo table, string databaseName, string connectionString, SqlSchemaService schemaService)
    {
        var kind = table.IsView ? SqlObjectNodeKind.View : SqlObjectNodeKind.Table;
        var displayName = $"{table.Schema}.{table.Name}";

        SqlObjectNode? node = null;
        node = new SqlObjectNode(displayName, kind, async () =>
        {
            var (success, columns, error) = await schemaService.ListColumnsAsync(
                connectionString, databaseName, table.Schema, table.Name);
            if (!success)
            {
                node!.LoadError = error;
                return;
            }

            foreach (var column in columns)
            {
                var nullable = column.IsNullable ? "?" : "";
                node!.Children.Add(new SqlObjectNode(
                    $"{column.Name} : {column.DataType}{nullable}", SqlObjectNodeKind.Column, null));
            }
        });

        return node;
    }
}
