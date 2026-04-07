using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Settings;

[TestClass]
public sealed class RefreshScheduleServiceTests
{
    [TestMethod]
    public async Task GetNextScheduledRefreshUtcAsync_UsesLatestActiveSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();
        var createdUtc = DateTime.UtcNow.AddHours(-2);

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(new Profile
            {
                ProfileId = "profile-1",
                Name = "Profile 1",
                OutputName = "Profile 1",
                MergeMode = "single",
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });

            var siteSettings = await setup.SiteSettings.OrderBy(x => x.Id).FirstAsync();
            siteSettings.RefreshScheduleKind = "6h";
            siteSettings.RefreshStartupCatchup = true;

            setup.Snapshots.Add(new Snapshot
            {
                SnapshotId = "snap-1",
                ProfileId = "profile-1",
                CreatedUtc = createdUtc,
                Status = "active",
                PlaylistPath = "playlist.m3u",
                XmltvPath = "guide.xml",
                ChannelIndexPath = "index.ndjson",
                StatusJsonPath = "status.json",
                ChannelCountPublished = 1,
                LiveChannelCount = 1,
                VodChannelCount = 0,
                SeriesChannelCount = 0,
            });

            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var service = new RefreshScheduleService(db, new AppEventBus());

        var nextRefresh = await service.GetNextScheduledRefreshUtcAsync();

        Assert.IsNotNull(nextRefresh);
        var expected = createdUtc.AddHours(6);
        Assert.IsLessThan(5d, Math.Abs((nextRefresh!.Value - expected).TotalSeconds),
            $"Expected next refresh near {expected:u}, got {nextRefresh:u}.");
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
