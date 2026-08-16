using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Persistence;

// Regression coverage for the abandoned-migration-lock hang: EF Core's SQLite provider records
// its migration lock as a row in __EFMigrationsLock and never cleans it up if the process is
// killed mid-migration. Every later startup then waits forever inside AcquireDatabaseLock(),
// silently. A real install was unusable for eleven days because of this.
[TestClass]
public sealed class MigrationLockGuardTests
{
    [TestMethod]
    public async Task ClearAbandonedLock_RemovesLockOlderThanThreshold()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        CreateLockTable(connection);
        InsertLock(connection, DateTimeOffset.UtcNow.AddDays(-11));

        var removed = MigrationLockGuard.ClearAbandonedLock(connection, NullLogger.Instance);

        Assert.AreEqual(1, removed);
        Assert.AreEqual(0, CountLockRows(connection));
    }

    [TestMethod]
    public async Task ClearAbandonedLock_LeavesRecentLockAlone()
    {
        // A lock this young may belong to a migration still in flight. Removing it would defeat
        // the concurrency protection EF is providing.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        CreateLockTable(connection);
        InsertLock(connection, DateTimeOffset.UtcNow.AddSeconds(-5));

        var removed = MigrationLockGuard.ClearAbandonedLock(connection, NullLogger.Instance);

        Assert.AreEqual(0, removed);
        Assert.AreEqual(1, CountLockRows(connection));
    }

    [TestMethod]
    public async Task ClearAbandonedLock_TreatsUnparseableTimestampAsAbandoned()
    {
        // A row whose age can't be established can't be shown to be live, and leaving it would
        // reproduce the very hang this guard exists to prevent.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        CreateLockTable(connection);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, 'not-a-timestamp');";
            await insert.ExecuteNonQueryAsync();
        }

        var removed = MigrationLockGuard.ClearAbandonedLock(connection, NullLogger.Instance);

        Assert.AreEqual(1, removed);
    }

    [TestMethod]
    public async Task ClearAbandonedLock_NoOpOnFreshDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        Assert.AreEqual(0, MigrationLockGuard.ClearAbandonedLock(connection, NullLogger.Instance));
    }

    [TestMethod]
    public async Task ClearAbandonedLock_NoOpWhenTableIsEmpty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        CreateLockTable(connection);

        Assert.AreEqual(0, MigrationLockGuard.ClearAbandonedLock(connection, NullLogger.Instance));
    }

    // The timeout is the assertion that matters: if the guard stops working, Migrate() waits
    // forever rather than failing, so this must fail the run instead of hanging it.
    [TestMethod]
    [Timeout(120_000)]
    public async Task AbandonedLock_DoesNotStallMigrations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        CreateLockTable(connection);
        InsertLock(connection, DateTimeOffset.UtcNow.AddDays(-11));

        await using var db = CreateDb(connection);

        MigrationLockGuard.ClearAbandonedLock(connection, NullLogger.Instance);
        await db.Database.MigrateAsync();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        CollectionAssert.AreEquivalent(
            db.Database.GetMigrations().ToArray(),
            applied.ToArray(),
            "Every migration must apply once the abandoned lock is cleared.");
    }

    private static void CreateLockTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsLock" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
                "Timestamp" TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void InsertLock(SqliteConnection connection, DateTimeOffset takenAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, $timestamp);";
        command.Parameters.AddWithValue("$timestamp", takenAt.ToString("o"));
        command.ExecuteNonQuery();
    }

    private static int CountLockRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsLock\";";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static ApplicationDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ApplicationDbContext(options);
    }
}
