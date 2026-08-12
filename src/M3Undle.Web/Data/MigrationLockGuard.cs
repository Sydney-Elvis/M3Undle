using System.Data.Common;
using System.Globalization;

namespace M3Undle.Web.Data;

/// <summary>
/// Removes an abandoned EF Core migration lock before migrations run.
/// </summary>
/// <remarks>
/// EF Core 9+ takes a database-wide lock before applying migrations. The SQLite provider has no
/// session-scoped lock to use, so it inserts a row into <c>__EFMigrationsLock</c> instead — and,
/// as Microsoft documents, that row is not cleaned up if the process is killed mid-migration:
/// "This prevents any subsequent migration from completing, because each attempt will wait
/// indefinitely for the lock to be released."
/// https://learn.microsoft.com/ef/core/providers/sqlite/limitations#concurrent-migrations-protection
///
/// The wait happens inside SqliteHistoryRepository.AcquireDatabaseLock() in a Thread.Sleep loop
/// with no timeout and no log output, so the symptom is a process that sits at 0% CPU forever
/// while the container reports "Up (unhealthy)" — indistinguishable from a dozen other faults.
/// A real install lost eleven days to this after a container was killed during a large series
/// expansion; see the beta.8.1 entry in CHANGELOG.md.
///
/// M3Undle runs one instance per data directory (SQLite permits a single writer), so a lock older
/// than <paramref name="staleAfter"/> cannot belong to a live migration. Younger locks are left
/// alone, so the protection EF is actually trying to provide still works if two processes ever do
/// race — they finish in seconds, far inside the window.
/// </remarks>
public static class MigrationLockGuard
{
    /// <summary>
    /// How old a lock row must be before it is treated as abandoned. Migrations here complete in
    /// well under a second; this is deliberately orders of magnitude larger so that a genuinely
    /// concurrent migration is never interrupted.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);

    private const string LockTable = "__EFMigrationsLock";

    /// <summary>
    /// Deletes abandoned lock rows. Returns the number removed — zero when the table is absent
    /// (a fresh database), empty, or holding a lock young enough to still be live.
    /// </summary>
    public static int ClearAbandonedLock(
        DbConnection connection,
        ILogger logger,
        TimeSpan? staleAfter = null,
        DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        var threshold = staleAfter ?? DefaultStaleAfter;
        var now = utcNow ?? DateTimeOffset.UtcNow;

        var openedHere = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
            openedHere = true;
        }

        try
        {
            if (!LockTableExists(connection))
                return 0;

            var oldest = ReadOldestLockAge(connection, now);
            if (oldest is null)
                return 0;

            var (age, timestampText) = oldest.Value;
            if (age < threshold)
            {
                logger.LogInformation(
                    "A migration lock taken {AgeSeconds:F0}s ago is present and may still be live; leaving it alone.",
                    age.TotalSeconds);
                return 0;
            }

            using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM \"{LockTable}\";";
            var removed = delete.ExecuteNonQuery();

            logger.LogWarning(
                "Removed {Removed} abandoned EF Core migration lock row(s) (oldest taken {Timestamp}, {AgeHours:F1} hours ago). "
                + "This is left behind when a previous start was killed mid-migration and would otherwise stall startup indefinitely.",
                removed,
                timestampText,
                age.TotalHours);

            return removed;
        }
        finally
        {
            if (openedHere)
                connection.Close();
        }
    }

    private static bool LockTableExists(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = LockTable;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// Returns the age of the oldest lock row, or null when there are none. An unparseable
    /// timestamp counts as maximally old: a row that cannot be dated cannot be shown to be live,
    /// and leaving it would reproduce the very hang this exists to prevent.
    /// </summary>
    private static (TimeSpan Age, string Timestamp)? ReadOldestLockAge(DbConnection connection, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"Timestamp\" FROM \"{LockTable}\";";

        TimeSpan? oldestAge = null;
        var oldestText = string.Empty;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var text = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var age = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var taken)
                ? now - taken
                : TimeSpan.MaxValue;

            if (oldestAge is null || age > oldestAge)
            {
                oldestAge = age;
                oldestText = string.IsNullOrEmpty(text) ? "(unreadable)" : text;
            }
        }

        return oldestAge is null ? null : (oldestAge.Value, oldestText);
    }
}
