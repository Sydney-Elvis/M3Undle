using Microsoft.Data.Sqlite;

namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Structural schema comparison between two SQLite databases: tables and their columns
/// (name, type, not-null, primary key). Used by restore to verify that a backup database,
/// after being migrated forward, actually matches the schema this app's migrations produce —
/// the migration *name* alone can't detect a divergent schema (e.g., a historical in-place
/// edit to an already-shipped migration). Indexes and default values are deliberately not
/// compared: their absence can't crash a query, and including them would risk false-positive
/// blocks on formatting differences between CREATE TABLE and ALTER TABLE ADD COLUMN.
/// </summary>
public static class SqliteSchemaComparer
{
    private sealed record ColumnInfo(string Type, bool NotNull, bool PrimaryKey);

    /// <summary>Returns human-readable differences; empty when the schemas are equivalent.</summary>
    public static async Task<IReadOnlyList<string>> CompareAsync(
        string expectedDbPath, string actualDbPath, CancellationToken cancellationToken)
    {
        var expected = await ReadSchemaAsync(expectedDbPath, cancellationToken);
        var actual = await ReadSchemaAsync(actualDbPath, cancellationToken);
        var differences = new List<string>();

        foreach (var table in expected.Keys.Except(actual.Keys, StringComparer.Ordinal).Order())
            differences.Add($"Table '{table}' is missing.");
        foreach (var table in actual.Keys.Except(expected.Keys, StringComparer.Ordinal).Order())
            differences.Add($"Table '{table}' is unexpected.");

        foreach (var table in expected.Keys.Intersect(actual.Keys, StringComparer.Ordinal).Order())
        {
            var expectedColumns = expected[table];
            var actualColumns = actual[table];

            foreach (var column in expectedColumns.Keys.Except(actualColumns.Keys, StringComparer.Ordinal).Order())
                differences.Add($"Table '{table}' is missing column '{column}'.");
            foreach (var column in actualColumns.Keys.Except(expectedColumns.Keys, StringComparer.Ordinal).Order())
                differences.Add($"Table '{table}' has unexpected column '{column}'.");

            foreach (var column in expectedColumns.Keys.Intersect(actualColumns.Keys, StringComparer.Ordinal).Order())
            {
                if (expectedColumns[column] != actualColumns[column])
                    differences.Add($"Table '{table}' column '{column}' differs: expected {expectedColumns[column]}, found {actualColumns[column]}.");
            }
        }

        return differences;
    }

    private static async Task<Dictionary<string, Dictionary<string, ColumnInfo>>> ReadSchemaAsync(
        string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            var tables = new List<string>();
            await using (var tablesCommand = connection.CreateCommand())
            {
                // sqlite_% covers SQLite internals; __EFMigrationsHistory is excluded so a
                // migrated database can be compared against one created straight from the model.
                tablesCommand.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name";
                await using var reader = await tablesCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    tables.Add(reader.GetString(0));
            }

            var schema = new Dictionary<string, Dictionary<string, ColumnInfo>>(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                var columns = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
                await using var columnsCommand = connection.CreateCommand();
                columnsCommand.CommandText = "SELECT name, type, \"notnull\", pk FROM pragma_table_info($table)";
                columnsCommand.Parameters.AddWithValue("$table", table);
                await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    columns[reader.GetString(0)] = new ColumnInfo(reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0);

                schema[table] = columns;
            }

            return schema;
        }
        finally
        {
            await connection.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }
    }
}
