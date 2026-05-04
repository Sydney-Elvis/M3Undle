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

    [TestMethod]
    public async Task GetEffectiveSettingsAsync_WhenNoProfileOverride_UsesGlobalSchedule()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfileAsync(fixture, "profile-1");

        await using (var setup = fixture.CreateDbContext())
        {
            var siteSettings = await setup.SiteSettings.OrderBy(x => x.Id).FirstAsync();
            siteSettings.RefreshScheduleKind = "12h";
            siteSettings.RefreshStartupCatchup = false;
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var service = new RefreshScheduleService(db, new AppEventBus());

        var settings = await service.GetEffectiveSettingsAsync("profile-1");

        Assert.IsFalse(settings.UsesProfileOverride);
        Assert.AreEqual("12h", settings.Settings.ScheduleKind);
        Assert.IsFalse(settings.Settings.StartupCatchup);
    }

    [TestMethod]
    public async Task GetEffectiveSettingsAsync_WhenProfileOverrideExists_UsesProfileSchedule()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfileAsync(fixture, "profile-1");

        await using (var setup = fixture.CreateDbContext())
        {
            var profile = await setup.Profiles.SingleAsync(x => x.ProfileId == "profile-1");
            profile.RefreshScheduleKindOverride = "2h";
            profile.RefreshStartupCatchupOverride = false;

            var siteSettings = await setup.SiteSettings.OrderBy(x => x.Id).FirstAsync();
            siteSettings.RefreshScheduleKind = "12h";
            siteSettings.RefreshStartupCatchup = true;
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var service = new RefreshScheduleService(db, new AppEventBus());

        var settings = await service.GetEffectiveSettingsAsync("profile-1");

        Assert.IsTrue(settings.UsesProfileOverride);
        Assert.AreEqual("2h", settings.Settings.ScheduleKind);
        Assert.IsFalse(settings.Settings.StartupCatchup);
        Assert.AreEqual("12h", settings.GlobalSettings.ScheduleKind);
    }

    [TestMethod]
    public async Task GetNextScheduledRefreshUtcAsync_WithProfile_UsesThatProfilesSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();
        var targetSnapshotUtc = DateTime.UtcNow.AddHours(-3);
        var otherSnapshotUtc = DateTime.UtcNow.AddMinutes(-10);

        await SeedProfileAsync(fixture, "profile-1");
        await SeedProfileAsync(fixture, "profile-2");

        await using (var setup = fixture.CreateDbContext())
        {
            var siteSettings = await setup.SiteSettings.OrderBy(x => x.Id).FirstAsync();
            siteSettings.RefreshScheduleKind = "6h";

            setup.Snapshots.Add(CreateSnapshot("snap-1", "profile-1", targetSnapshotUtc));
            setup.Snapshots.Add(CreateSnapshot("snap-2", "profile-2", otherSnapshotUtc));

            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var service = new RefreshScheduleService(db, new AppEventBus());

        var nextRefresh = await service.GetNextScheduledRefreshUtcAsync("profile-1");

        Assert.IsNotNull(nextRefresh);
        var expected = targetSnapshotUtc.AddHours(6);
        Assert.IsLessThan(5d, Math.Abs((nextRefresh!.Value - expected).TotalSeconds),
            $"Expected next refresh near {expected:u}, got {nextRefresh:u}.");
    }

    [TestMethod]
    public async Task GetNextScheduledRefreshUtcAsync_WithManualProfileOverride_ReturnsNull()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfileAsync(fixture, "profile-1");

        await using (var setup = fixture.CreateDbContext())
        {
            var profile = await setup.Profiles.SingleAsync(x => x.ProfileId == "profile-1");
            profile.RefreshScheduleKindOverride = "manual";
            profile.RefreshStartupCatchupOverride = true;
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var service = new RefreshScheduleService(db, new AppEventBus());

        var nextRefresh = await service.GetNextScheduledRefreshUtcAsync("profile-1");

        Assert.IsNull(nextRefresh);
    }

    [TestMethod]
    public async Task UpdateProfileAsync_SavesOverrideAndPublishesScheduleChanged()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfileAsync(fixture, "profile-1");
        var eventBus = new AppEventBus();
        var events = eventBus.Subscribe(out var unsubscriber);
        using (unsubscriber)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var service = new RefreshScheduleService(db, eventBus);

                var result = await service.UpdateProfileAsync(
                    "profile-1",
                    new ProfileRefreshScheduleSettings(false, "4h", false));

                Assert.IsTrue(result.Succeeded, result.Error);
                Assert.IsTrue(await events.WaitToReadAsync());
                Assert.AreEqual(AppEventKind.RefreshScheduleChanged, (await events.ReadAsync()).Kind);
            }
        }

        await using var verify = fixture.CreateDbContext();
        var profile = await verify.Profiles.SingleAsync(x => x.ProfileId == "profile-1");
        Assert.AreEqual("4h", profile.RefreshScheduleKindOverride);
        Assert.IsFalse(profile.RefreshStartupCatchupOverride);
    }

    [TestMethod]
    public async Task UpdateProfileAsync_WhenInheritGlobal_ClearsOverride()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfileAsync(fixture, "profile-1");

        await using (var setup = fixture.CreateDbContext())
        {
            var seededProfile = await setup.Profiles.SingleAsync(x => x.ProfileId == "profile-1");
            seededProfile.RefreshScheduleKindOverride = "1h";
            seededProfile.RefreshStartupCatchupOverride = false;
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var service = new RefreshScheduleService(db, new AppEventBus());

        var result = await service.UpdateProfileAsync(
            "profile-1",
            new ProfileRefreshScheduleSettings(true, "manual", false));

        Assert.IsTrue(result.Succeeded, result.Error);

        await using var verify = fixture.CreateDbContext();
        var profile = await verify.Profiles.SingleAsync(x => x.ProfileId == "profile-1");
        Assert.IsNull(profile.RefreshScheduleKindOverride);
        Assert.IsNull(profile.RefreshStartupCatchupOverride);
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

    private static async Task SeedProfileAsync(TestFixture fixture, string profileId)
    {
        await using var db = fixture.CreateDbContext();
        db.Profiles.Add(new Profile
        {
            ProfileId = profileId,
            Name = profileId,
            OutputName = profileId,
            MergeMode = "single",
            Enabled = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static Snapshot CreateSnapshot(string snapshotId, string profileId, DateTime createdUtc)
        => new()
        {
            SnapshotId = snapshotId,
            ProfileId = profileId,
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
        };

    private sealed class TestFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options) : IAsyncDisposable
    {
        public ApplicationDbContext CreateDbContext() => new(options);

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
