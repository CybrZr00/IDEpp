using Microsoft.Data.SqlClient;

namespace IdePlusPlus.Connections;

public sealed record SqlTableInfo(string Schema, string Name, bool IsView);

public sealed record SqlColumnInfo(string Name, string DataType, bool IsNullable);

/// <summary>
/// Queries a SQL Server instance's schema one level at a time - databases, then a chosen database's
/// tables/views, then a chosen table's columns - never all at once the way eagerly enumerating every
/// database's every table's every column on connect would. Each call opens its own short-lived
/// connection and is timeout-bounded, mirroring this codebase's "cap it, never block the UI thread"
/// posture for external processes (build/test runs) rather than keeping one live connection open per
/// pane for the app's whole lifetime.
/// </summary>
public sealed class SqlSchemaService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public Task<(bool Success, IReadOnlyList<string> Databases, string? Error)> ListDatabasesAsync(
        string connectionString, CancellationToken cancellationToken = default) =>
        RunAsync(connectionString, async connection =>
        {
            var databases = new List<string>();
            await using var command = new SqlCommand(
                "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name", connection)
            {
                CommandTimeout = (int)QueryTimeout.TotalSeconds,
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                databases.Add(reader.GetString(0));
            }

            return databases;
        }, cancellationToken);

    public Task<(bool Success, IReadOnlyList<SqlTableInfo> Tables, string? Error)> ListTablesAsync(
        string connectionString, string databaseName, CancellationToken cancellationToken = default) =>
        RunAsync(BuildConnectionStringForDatabase(connectionString, databaseName), async connection =>
        {
            var tables = new List<SqlTableInfo>();
            await using var command = new SqlCommand(
                "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME",
                connection)
            {
                CommandTimeout = (int)QueryTimeout.TotalSeconds,
            };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(new SqlTableInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2) == "VIEW"));
            }

            return tables;
        }, cancellationToken);

    public Task<(bool Success, IReadOnlyList<SqlColumnInfo> Columns, string? Error)> ListColumnsAsync(
        string connectionString, string databaseName, string schemaName, string tableName,
        CancellationToken cancellationToken = default) =>
        RunAsync(BuildConnectionStringForDatabase(connectionString, databaseName), async connection =>
        {
            var columns = new List<SqlColumnInfo>();
            await using var command = new SqlCommand(
                """
                SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
                ORDER BY ORDINAL_POSITION
                """,
                connection)
            {
                CommandTimeout = (int)QueryTimeout.TotalSeconds,
            };
            command.Parameters.AddWithValue("@schema", schemaName);
            command.Parameters.AddWithValue("@table", tableName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new SqlColumnInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2) == "YES"));
            }

            return columns;
        }, cancellationToken);

    private static string BuildConnectionStringForDatabase(string connectionString, string databaseName) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = databaseName }.ConnectionString;

    private static async Task<(bool Success, IReadOnlyList<T> Items, string? Error)> RunAsync<T>(
        string connectionString, Func<SqlConnection, Task<List<T>>> query, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(QueryTimeout);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(timeoutCts.Token);
            var items = await query(connection);
            return (true, items, null);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return (false, [], ex.Message);
        }
        catch (OperationCanceledException)
        {
            return (false, [], "Timed out connecting to the server.");
        }
    }
}
