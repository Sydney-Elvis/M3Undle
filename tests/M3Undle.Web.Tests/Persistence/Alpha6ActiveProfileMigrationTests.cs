using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Persistence;

[TestClass]
public sealed class Alpha6ActiveProfileMigrationTests
{
    private const string PreAlpha6Migration = "20260403182105_20260403180000_Alpha5_CustomGroups";
    private const string Alpha6Migration = "20260404000000_Alpha6_ActiveProfile";

    [TestMethod]
    public async Task Alpha6Migration_BackfillsActiveProfile_FromLegacyProviderActiveLink()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(PreAlpha6Migration);

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

        await migrator.MigrateAsync(Alpha6Migration);

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
    public async Task Alpha6Migration_EnforcesSingleActiveProfile_UniquePartialIndex()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(Alpha6Migration);

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

    private static ApplicationDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ApplicationDbContext(options);
    }
}
