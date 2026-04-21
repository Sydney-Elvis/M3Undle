using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Persistence;

[TestClass]
public sealed class Alpha5SchemaMigrationTests
{
    private const string PreAlpha5SchemaMigration = "20260314145015_Alpha4_Schema";
    private const string Alpha5SchemaMigration = "20260322000000_Alpha5_Schema";

    [TestMethod]
    public async Task Alpha5SchemaMigration_BackfillsActiveProfile_FromLegacyProviderActiveLink()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(PreAlpha5SchemaMigration);

        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO profiles (profile_id, name, enabled, output_name, merge_mode, created_utc, updated_utc)
            VALUES ({0}, {1}, 1, {2}, {3}, {4}, {4});
            """,
            "profile-1", "Profile 1", "m3undle", "replace", now);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO providers (provider_id, name, playlist_url, enabled, is_active, needs_env_var_substitution, timeout_seconds, created_utc, updated_utc)
            VALUES ({0}, {1}, {2}, 1, 1, 0, 20, {3}, {3});
            """,
            "provider-1", "Provider 1", "http://example.com/playlist.m3u", now);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO profile_providers (profile_id, provider_id, priority, enabled)
            VALUES ({0}, {1}, 1, 1);
            """,
            "profile-1", "provider-1");

        await migrator.MigrateAsync(Alpha5SchemaMigration);

        var activeProfileId = await db.Profiles
            .AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => x.ProfileId)
            .SingleOrDefaultAsync();
        Assert.AreEqual("profile-1", activeProfileId);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM pragma_table_info('providers') WHERE name = 'is_active'";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task Alpha5SchemaMigration_EnforcesSingleActiveProfile_UniquePartialIndex()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(Alpha5SchemaMigration);

        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO profiles (profile_id, name, enabled, is_active, output_name, merge_mode, created_utc, updated_utc)
            VALUES ({0}, {1}, 1, 1, {2}, {3}, {4}, {4});
            """,
            "profile-1", "Profile 1", "m3undle", "replace", now);

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO profiles (profile_id, name, enabled, is_active, output_name, merge_mode, created_utc, updated_utc)
                VALUES ({0}, {1}, 1, 1, {2}, {3}, {4}, {4});
                """,
                "profile-2", "Profile 2", "m3undle", "replace", now);
            Assert.Fail("Expected SqliteException for duplicate active profile.");
        }
        catch (SqliteException)
        {
            // Expected path.
        }
    }

    [TestMethod]
    public async Task StartupRepair_RecoversPartialAlpha5Schema_BeforeMigrateRetries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(PreAlpha5SchemaMigration);

        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"idx_providers_is_active\";");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"providers\" ADD COLUMN \"force_mpegts\" INTEGER NOT NULL DEFAULT 0;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"snapshots\" ADD COLUMN \"change_class\" TEXT NULL;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"site_settings\" ADD COLUMN \"generated_hls_enabled\" INTEGER NOT NULL DEFAULT 1;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"site_settings\" ADD COLUMN \"generated_hls_ffmpeg_path\" TEXT NULL;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"site_settings\" ADD COLUMN \"generated_hls_settings_restart_required\" INTEGER NOT NULL DEFAULT 0;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"site_settings\" ADD COLUMN \"hdhr_advertised_base_url\" TEXT NULL;");

        await StartupMigrationRepair.RepairAlpha5PartialSchemaAsync(db);
        await migrator.MigrateAsync();

        await using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText = """
            SELECT COUNT(*)
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = '20260322000000_Alpha5_Schema';
            """;
        Assert.AreEqual(1, Convert.ToInt32(await historyCommand.ExecuteScalarAsync()));

        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('site_settings') WHERE name = 'hdhr_advertised_base_url';";
        Assert.AreEqual(1, Convert.ToInt32(await columnCommand.ExecuteScalarAsync()));

        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'profile_custom_groups';";
        Assert.AreEqual(1, Convert.ToInt32(await tableCommand.ExecuteScalarAsync()));
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
