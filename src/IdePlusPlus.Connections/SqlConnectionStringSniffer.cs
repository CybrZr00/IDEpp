namespace IdePlusPlus.Connections;

/// <summary>Best-effort heuristic for whether a raw connection string is SQL-Server-shaped (ADO.NET
/// key=value form with Server/Data Source, not a different provider's URI scheme) - used only to
/// decide whether "browse schema" is offered for a given discovered connection at all. Not
/// authoritative: SqlSchemaService.ListDatabasesAsync is what actually proves a connection works.</summary>
public static class SqlConnectionStringSniffer
{
    private static readonly string[] NonSqlServerSchemes =
        ["postgres://", "postgresql://", "mongodb://", "mongodb+srv://", "mysql://", "redis://"];

    public static bool LooksLikeSqlServer(string connectionString)
    {
        if (NonSqlServerSchemes.Any(scheme => connectionString.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Addr=", StringComparison.OrdinalIgnoreCase);
    }
}
