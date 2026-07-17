using M3Undle.Web.Data;
using M3Undle.Web.Data.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Persistence;

// The Alpha_Schema baseline is FROZEN as of the first release that shipped portable
// backup/restore. Editing an already-shipped migration in place means databases that applied
// the old version can never be migrated to the new shape — Migrate() sees the name as applied
// and silently no-ops, which breaks both in-place container upgrades and backup restore.
// Every schema change from now on must be a new migration (dotnet ef migrations add).
[TestClass]
public sealed class MigrationBaselineTests
{
    [TestMethod]
    public async Task ApplicationDbContext_MigrationsStartFromFrozenAlphaBaseline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var migrations = db.Database.GetService<IMigrationsAssembly>().Migrations;
        Assert.IsTrue(migrations.First().Key.EndsWith("_Alpha_Schema", StringComparison.Ordinal),
            "The Alpha_Schema baseline must remain the first migration.");

        await db.Database.MigrateAsync();

        await using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";";
        Assert.AreEqual(migrations.Count, Convert.ToInt32(await historyCommand.ExecuteScalarAsync()),
            "Every known migration must apply to an empty database.");
    }

    [TestMethod]
    public void AlphaSchemaBaseline_IsFrozen_AddANewMigrationInstead()
    {
        // Tripwire for in-place edits to the shipped baseline: any change to what Alpha_Schema
        // does shows up as a different operation count. If this fails, revert the baseline edit
        // and put the schema change in a new migration — do not update the expected count.
        var baseline = new Alpha_Schema();
        Assert.AreEqual(116, baseline.UpOperations.Count,
            "Alpha_Schema shipped in v1.0.0-beta.1 and is frozen. Add a new migration (dotnet ef migrations add) instead of editing it.");
    }

    [TestMethod]
    public async Task ExistingBaselineOnlyDatabase_MigratesInPlace_GainsBackupScheduleColumns()
    {
        // Simulates upgrading a beta.1–beta.6 database (Alpha_Schema only) with this build —
        // the first in-place upgrade this project supports, and the path a restored older
        // backup takes through the restore engine's MigrateAsync call.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260605114014_Alpha_Schema");
        Assert.IsFalse(await ColumnExistsAsync(connection, "site_settings", "backup_schedule_enabled"),
            "Sanity: the baseline alone must not contain the backup-schedule column.");

        await db.Database.MigrateAsync();

        Assert.IsTrue(await ColumnExistsAsync(connection, "site_settings", "backup_schedule_enabled"));
        Assert.IsTrue(await ColumnExistsAsync(connection, "site_settings", "backup_last_run_utc"));
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
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
