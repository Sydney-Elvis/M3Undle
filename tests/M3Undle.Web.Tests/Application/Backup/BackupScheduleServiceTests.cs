using M3Undle.Web.Application.Backup;
using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application.Backup;

[TestClass]
public sealed class BackupScheduleServiceTests
{
    [TestMethod]
    public async Task GetSettingsAsync_Default_IsDisabledWithNoLastRun()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var service = new BackupScheduleService(db);

        var settings = await service.GetSettingsAsync();

        Assert.IsFalse(settings.Enabled);
        Assert.IsNull(settings.LastRunUtc);
    }

    [TestMethod]
    public async Task SetEnabledAsync_Persists()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var db = fixture.CreateDbContext())
            await new BackupScheduleService(db).SetEnabledAsync(true);

        await using var verify = fixture.CreateDbContext();
        var settings = await new BackupScheduleService(verify).GetSettingsAsync();
        Assert.IsTrue(settings.Enabled);
    }

    [TestMethod]
    public async Task RecordRunAsync_Persists()
    {
        await using var fixture = await CreateFixtureAsync();
        var runUtc = new DateTime(2026, 7, 17, 3, 0, 0, DateTimeKind.Utc);

        await using (var db = fixture.CreateDbContext())
            await new BackupScheduleService(db).RecordRunAsync(runUtc);

        await using var verify = fixture.CreateDbContext();
        var settings = await new BackupScheduleService(verify).GetSettingsAsync();
        Assert.AreEqual(runUtc, settings.LastRunUtc);
    }

    [TestMethod]
    public async Task GetNextScheduledBackupUtcAsync_Disabled_ReturnsNull()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var service = new BackupScheduleService(db);

        Assert.IsNull(await service.GetNextScheduledBackupUtcAsync());
    }

    [TestMethod]
    public async Task GetNextScheduledBackupUtcAsync_EnabledWithRecentRun_ReturnsSevenDaysAfterLastRun()
    {
        await using var fixture = await CreateFixtureAsync();
        var lastRun = DateTime.UtcNow.AddDays(-2);

        await using (var db = fixture.CreateDbContext())
        {
            var service = new BackupScheduleService(db);
            await service.SetEnabledAsync(true);
            await service.RecordRunAsync(lastRun);
        }

        await using var verify = fixture.CreateDbContext();
        var next = await new BackupScheduleService(verify).GetNextScheduledBackupUtcAsync();

        Assert.IsNotNull(next);
        var expected = lastRun.AddDays(7);
        Assert.IsLessThan(5d, Math.Abs((next!.Value - expected).TotalSeconds), $"Expected next backup near {expected:u}, got {next:u}.");
    }

    [TestMethod]
    public async Task GetNextScheduledBackupUtcAsync_EnabledButOverdue_RollsForwardRatherThanReturningThePast()
    {
        await using var fixture = await CreateFixtureAsync();
        var lastRun = DateTime.UtcNow.AddDays(-20); // 2 whole weeks overdue

        await using (var db = fixture.CreateDbContext())
        {
            var service = new BackupScheduleService(db);
            await service.SetEnabledAsync(true);
            await service.RecordRunAsync(lastRun);
        }

        await using var verify = fixture.CreateDbContext();
        var next = await new BackupScheduleService(verify).GetNextScheduledBackupUtcAsync();

        Assert.IsNotNull(next);
        Assert.IsTrue(next!.Value > DateTime.UtcNow, "An overdue schedule must roll forward, not return a time already in the past.");
    }

    [TestMethod]
    public async Task GetNextScheduledBackupUtcAsync_EnabledWithNoPriorRun_UsesNowAsBaseline()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var db = fixture.CreateDbContext())
            await new BackupScheduleService(db).SetEnabledAsync(true);

        var before = DateTime.UtcNow;
        await using var verify = fixture.CreateDbContext();
        var next = await new BackupScheduleService(verify).GetNextScheduledBackupUtcAsync();
        var after = DateTime.UtcNow;

        Assert.IsNotNull(next);
        Assert.IsTrue(next!.Value >= before.AddDays(7) && next.Value <= after.AddDays(7));
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var fixture = new TestFixture(connection, options);

        await using var db = fixture.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        return fixture;
    }

    private sealed class TestFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options) : IAsyncDisposable
    {
        public ApplicationDbContext CreateDbContext() => new(options);

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
