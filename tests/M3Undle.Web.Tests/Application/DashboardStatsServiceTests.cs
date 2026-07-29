using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class DashboardStatsServiceTests
{
    [TestMethod]
    public async Task GetStatsAsync_ExpiringProviders_EmptyWhenNoProvidersHaveExpiry()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        db.Providers.Add(NewProvider("p1", playlistExpiresUtc: null));
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        Assert.IsEmpty(stats.ExpiringProviders);
    }

    [TestMethod]
    public async Task GetStatsAsync_ExpiringProviders_IncludesProviderExpiringSoon()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        db.Providers.Add(NewProvider("p1", playlistExpiresUtc: DateTime.UtcNow.AddDays(5)));
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        Assert.HasCount(1, stats.ExpiringProviders);
        Assert.AreEqual("p1", stats.ExpiringProviders[0].ProviderId);
    }

    [TestMethod]
    public async Task GetStatsAsync_ExpiringProviders_IncludesAlreadyExpiredProvider()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        db.Providers.Add(NewProvider("p1", playlistExpiresUtc: DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        Assert.HasCount(1, stats.ExpiringProviders);
    }

    [TestMethod]
    public async Task GetStatsAsync_ExpiringProviders_ExcludesProviderExpiryBeyond30Days()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        db.Providers.Add(NewProvider("p1", playlistExpiresUtc: DateTime.UtcNow.AddDays(35)));
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        Assert.IsEmpty(stats.ExpiringProviders);
    }

    [TestMethod]
    public async Task GetStatsAsync_ExpiringProviders_OrderedByEarliestExpiryFirst()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        db.Providers.Add(NewProvider("p1", playlistExpiresUtc: DateTime.UtcNow.AddDays(20)));
        db.Providers.Add(NewProvider("p2", playlistExpiresUtc: DateTime.UtcNow.AddDays(3)));
        db.Providers.Add(NewProvider("p3", playlistExpiresUtc: DateTime.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        Assert.HasCount(3, stats.ExpiringProviders);
        Assert.AreEqual("p3", stats.ExpiringProviders[0].ProviderId);
        Assert.AreEqual("p2", stats.ExpiringProviders[1].ProviderId);
        Assert.AreEqual("p1", stats.ExpiringProviders[2].ProviderId);
    }

    [TestMethod]
    public async Task GetStatsAsync_ActiveProfileProviderExpiresUtc_UsesEarliestLinkedEnabledProviderExpiry()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var laterExpiry = DateTime.UtcNow.AddDays(40);
        var earlierExpiry = DateTime.UtcNow.AddDays(10);

        db.Profiles.Add(NewProfile("profile-1", isActive: true));
        db.Profiles.Add(NewProfile("other-profile", isActive: false));
        db.Providers.Add(NewProvider("p1", playlistExpiresUtc: laterExpiry));
        db.Providers.Add(NewProvider("p2", playlistExpiresUtc: earlierExpiry));
        db.Providers.Add(NewProvider("p3", playlistExpiresUtc: DateTime.UtcNow.AddDays(1)));
        db.ProfileProviders.Add(NewProfileProvider("profile-1", "p1"));
        db.ProfileProviders.Add(NewProfileProvider("profile-1", "p2"));
        db.ProfileProviders.Add(NewProfileProvider("other-profile", "p3"));
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        Assert.AreEqual(earlierExpiry, stats.ActiveProfileProviderExpiresUtc);
    }

    [TestMethod]
    public async Task GetStatsAsync_ActiveProfileProviderExpiresUtc_NullWhenActiveProfileHasNoProviderExpiry()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        db.Profiles.Add(NewProfile("profile-1", isActive: true));
        db.Providers.Add(NewProvider("p1", playlistExpiresUtc: null));
        db.ProfileProviders.Add(NewProfileProvider("profile-1", "p1"));
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        Assert.IsNull(stats.ActiveProfileProviderExpiresUtc);
    }

    [TestMethod]
    public async Task GetStatsAsync_ProfileWithoutProvider_DoesNotPresentOldSnapshotAsOutput()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;

        db.Profiles.Add(NewProfile("profile-1", isActive: true));
        db.Snapshots.Add(new Snapshot
        {
            SnapshotId = "snapshot-orphaned",
            ProfileId = "profile-1",
            CreatedUtc = now,
            Status = "active",
            PlaylistPath = "playlist.m3u",
            XmltvPath = "guide.xml",
            ChannelIndexPath = "channels.json",
            StatusJsonPath = "status.json",
            ChannelCountPublished = 15,
            LiveChannelCount = 10,
            VodChannelCount = 5,
        });
        await db.SaveChangesAsync();

        var stats = await CreateService(fixture).GetStatsAsync(CancellationToken.None);

        var profile = stats.ProfileSummaries.Single();
        Assert.IsFalse(profile.IsPublished);
        Assert.AreEqual(ProfileHealthStatus.NoOutput, profile.HealthStatus);
        Assert.AreEqual(0, profile.LiveCount);
        Assert.AreEqual(0, profile.MovieCount);
        Assert.IsNull(profile.LastPublishedUtc);
        Assert.AreEqual(0, stats.PublishedLiveCount);
        Assert.IsNull(stats.LastPublishedUtc);
    }

    private static DashboardStatsService CreateService(TestFixture fixture)
        => new(fixture.Services.GetRequiredService<IServiceScopeFactory>());

    private static Provider NewProvider(string id, DateTime? playlistExpiresUtc) => new()
    {
        ProviderId = id,
        Name = id,
        Enabled = true,
        PlaylistUrl = "http://example.com/playlist.m3u",
        TimeoutSeconds = 20,
        PlaylistExpiresUtc = playlistExpiresUtc,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static Profile NewProfile(string id, bool isActive) => new()
    {
        ProfileId = id,
        Name = id,
        OutputName = "m3undle",
        MergeMode = "replace",
        Enabled = true,
        IsActive = isActive,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static ProfileProvider NewProfileProvider(string profileId, string providerId) => new()
    {
        ProfileId = profileId,
        ProviderId = providerId,
        Priority = 1,
        Enabled = true,
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
        public ServiceProvider Services { get; } = services;

        public ApplicationDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;
            return new ApplicationDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
