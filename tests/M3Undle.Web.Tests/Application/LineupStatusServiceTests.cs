using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class LineupStatusServiceTests
{
    [TestMethod]
    public async Task GetStatusAsync_WhenActiveProfileHasLinkedProvider_ReturnsThatProviderAndSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedAsync(fixture.Connection, db =>
        {
            var now = DateTime.UtcNow;

            db.Profiles.AddRange(
                MakeProfile("profile-active", "Active Profile", enabled: true, isActive: true, now),
                MakeProfile("profile-other", "Other Profile", enabled: true, isActive: false, now));

            db.Providers.AddRange(
                MakeProvider("provider-active", "Provider Active", enabled: true, now),
                MakeProvider("provider-other", "Provider Other", enabled: true, now));

            db.ProfileProviders.AddRange(
                new ProfileProvider { ProfileId = "profile-active", ProviderId = "provider-active", Priority = 1, Enabled = true },
                new ProfileProvider { ProfileId = "profile-other", ProviderId = "provider-other", Priority = 1, Enabled = true });

            db.Snapshots.Add(new Snapshot
            {
                SnapshotId = "snapshot-1",
                ProfileId = "profile-active",
                CreatedUtc = now,
                Status = "active",
                PlaylistPath = "playlist.m3u",
                XmltvPath = "guide.xml",
                ChannelIndexPath = "channels.json",
                StatusJsonPath = "status.json",
                ChannelCountPublished = 42,
                LiveChannelCount = 42,
            });

            db.FetchRuns.Add(new FetchRun
            {
                FetchRunId = "fetch-1",
                ProviderId = "provider-active",
                StartedUtc = now.AddMinutes(-5),
                FinishedUtc = now.AddMinutes(-4),
                Status = "ok",
                Type = "snapshot",
                ChannelCountSeen = 42,
            });
        });

        var service = new LineupStatusService(fixture.Services.GetRequiredService<IServiceScopeFactory>());

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual("ok", status.Status);
        Assert.IsNotNull(status.Lineup);
        Assert.AreEqual("provider-active", status.Lineup.ActiveProvider?.ProviderId);
        Assert.AreEqual("profile-active", status.Lineup.ActiveSnapshot?.ProfileId);
        Assert.AreEqual(42, status.Lineup.ActiveSnapshot?.ChannelCountPublished);
    }

    [TestMethod]
    public async Task GetStatusAsync_WhenLatestRefreshFailedButSnapshotExists_ReturnsDegraded()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedAsync(fixture.Connection, db =>
        {
            var now = DateTime.UtcNow;

            db.Profiles.Add(MakeProfile("profile-active", "Active Profile", enabled: true, isActive: true, now));
            db.Providers.Add(MakeProvider("provider-active", "Provider Active", enabled: true, now));
            db.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = "profile-active",
                ProviderId = "provider-active",
                Priority = 1,
                Enabled = true,
            });
            db.Snapshots.Add(new Snapshot
            {
                SnapshotId = "snapshot-1",
                ProfileId = "profile-active",
                CreatedUtc = now,
                Status = "active",
                PlaylistPath = "playlist.m3u",
                XmltvPath = "guide.xml",
                ChannelIndexPath = "channels.json",
                StatusJsonPath = "status.json",
                ChannelCountPublished = 8,
                LiveChannelCount = 8,
            });
            db.FetchRuns.Add(new FetchRun
            {
                FetchRunId = "fetch-1",
                ProviderId = "provider-active",
                StartedUtc = now.AddMinutes(-2),
                FinishedUtc = now.AddMinutes(-1),
                Status = "fail",
                Type = "snapshot",
                ErrorSummary = "upstream timeout",
            });
        });

        var service = new LineupStatusService(fixture.Services.GetRequiredService<IServiceScopeFactory>());

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual("degraded", status.Status);
        Assert.IsNotNull(status.Lineup);
        Assert.AreEqual("degraded", status.Lineup.Status);
        Assert.AreEqual("upstream timeout", status.Lineup.LastRefresh?.ErrorSummary);
    }

    private static async Task SeedAsync(SqliteConnection connection, Action<ApplicationDbContext> seed)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        seed(db);
        await db.SaveChangesAsync();
    }

    private static Profile MakeProfile(string id, string name, bool enabled, bool isActive, DateTime now) => new()
    {
        ProfileId = id,
        Name = name,
        Enabled = enabled,
        IsActive = isActive,
        OutputName = "m3undle",
        MergeMode = "replace",
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    private static Provider MakeProvider(string id, string name, bool enabled, DateTime now) => new()
    {
        ProviderId = id,
        Name = name,
        Enabled = enabled,
        PlaylistUrl = "http://example.com/playlist.m3u",
        TimeoutSeconds = 20,
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return new TestFixture(connection, provider);
    }

    private sealed class TestFixture(SqliteConnection connection, ServiceProvider services) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public ServiceProvider Services { get; } = services;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
