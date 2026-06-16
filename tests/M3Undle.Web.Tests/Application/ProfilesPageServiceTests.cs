using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class ProfilesPageServiceTests
{
    [TestMethod]
    public async Task SetProfileActiveAsync_SwitchesActiveProfile_AndTriggersRefresh()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfilesAsync(fixture.Connection, ("p1", true), ("p2", false));

        var refreshTrigger = new TestRefreshTrigger();
        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            refreshTrigger,
            new AppEventBus());

        await SeedProfileProviderAsync(fixture, "p2");

        var error = await service.SetProfileActiveAsync("p2", CancellationToken.None);

        Assert.IsNull(error);
        Assert.AreEqual(1, refreshTrigger.TriggerRefreshCallCount);

        await using var verify = fixture.CreateDbContext();
        var p1 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p1");
        var p2 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p2");
        Assert.IsFalse(p1.IsActive);
        Assert.IsTrue(p2.IsActive);
    }

    [TestMethod]
    public async Task SetProfileActiveAsync_WhenRefreshInProgress_ReturnsConflict()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfilesAsync(fixture.Connection, ("p1", true));

        var refreshTrigger = new TestRefreshTrigger { IsRefreshingValue = true };
        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            refreshTrigger,
            new AppEventBus());

        var error = await service.SetProfileActiveAsync("p1", CancellationToken.None);

        Assert.IsNotNull(error);
        Assert.AreEqual(0, refreshTrigger.TriggerRefreshCallCount);
    }

    [TestMethod]
    public async Task SetProfileActiveAsync_WhenTargetProfileDisabled_ReturnsErrorAndDoesNotSwitchActiveProfile()
    {
        await using var fixture = await CreateFixtureAsync();

        var now = DateTime.UtcNow;
        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.Add(new Profile
            {
                ProfileId = "p1",
                Name = "Profile 1",
                Enabled = true,
                IsActive = true,
                OutputName = "m3undle",
                MergeMode = "replace",
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            db.Profiles.Add(new Profile
            {
                ProfileId = "p2",
                Name = "Profile 2",
                Enabled = false,
                IsActive = false,
                OutputName = "m3undle",
                MergeMode = "replace",
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var refreshTrigger = new TestRefreshTrigger();
        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            refreshTrigger,
            new AppEventBus());

        var error = await service.SetProfileActiveAsync("p2", CancellationToken.None);

        Assert.AreEqual("Disabled profiles cannot be activated.", error);
        Assert.AreEqual(0, refreshTrigger.TriggerRefreshCallCount);

        await using var verify = fixture.CreateDbContext();
        var p1 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p1");
        var p2 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p2");
        Assert.IsTrue(p1.IsActive);
        Assert.IsFalse(p2.IsActive);
    }

    [TestMethod]
    public async Task AutoActivateProfileIfNoneAsync_WhenNoActiveProfile_ActivatesTargetAndTriggersRefresh()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfilesAsync(fixture.Connection, ("p1", false), ("p2", false));

        var refreshTrigger = new TestRefreshTrigger();
        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            refreshTrigger,
            new AppEventBus());

        await service.AutoActivateProfileIfNoneAsync("p2", CancellationToken.None);

        Assert.AreEqual(1, refreshTrigger.TriggerRefreshCallCount);

        await using var verify = fixture.CreateDbContext();
        var p1 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p1");
        var p2 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p2");
        Assert.IsFalse(p1.IsActive);
        Assert.IsTrue(p2.IsActive);
    }

    [TestMethod]
    public async Task AutoActivateProfileIfNoneAsync_WhenActiveProfileExists_DoesNotChangeActiveProfile()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfilesAsync(fixture.Connection, ("p1", true), ("p2", false));

        var refreshTrigger = new TestRefreshTrigger();
        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            refreshTrigger,
            new AppEventBus());

        await service.AutoActivateProfileIfNoneAsync("p2", CancellationToken.None);

        Assert.AreEqual(0, refreshTrigger.TriggerRefreshCallCount);

        await using var verify = fixture.CreateDbContext();
        var p1 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p1");
        var p2 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p2");
        Assert.IsTrue(p1.IsActive);
        Assert.IsFalse(p2.IsActive);
    }

    [TestMethod]
    public async Task SetProfileEnabledAsync_DisablesProfile()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfilesAsync(fixture.Connection, ("p1", false));

        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var error = await service.SetProfileEnabledAsync("p1", enabled: false, CancellationToken.None);

        Assert.IsNull(error);

        await using var verify = fixture.CreateDbContext();
        var p1 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p1");
        Assert.IsFalse(p1.Enabled);
    }

    [TestMethod]
    public async Task SetProfileEnabledAsync_EnablesProfile()
    {
        await using var fixture = await CreateFixtureAsync();

        var now = DateTime.UtcNow;
        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.Add(new Profile
            {
                ProfileId = "p1",
                Name = "Profile 1",
                Enabled = false,
                IsActive = false,
                OutputName = "m3undle",
                MergeMode = "replace",
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var error = await service.SetProfileEnabledAsync("p1", enabled: true, CancellationToken.None);

        Assert.IsNull(error);

        await using var verify = fixture.CreateDbContext();
        var p1 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p1");
        Assert.IsTrue(p1.Enabled);
    }

    [TestMethod]
    public async Task SetProfileEnabledAsync_WhenDisablingActiveProfile_AlsoDeactivatesIt()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedProfilesAsync(fixture.Connection, ("p1", true));

        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var error = await service.SetProfileEnabledAsync("p1", enabled: false, CancellationToken.None);

        Assert.IsNull(error);

        await using var verify = fixture.CreateDbContext();
        var p1 = await verify.Profiles.SingleAsync(x => x.ProfileId == "p1");
        Assert.IsFalse(p1.Enabled);
        Assert.IsFalse(p1.IsActive, "Disabling the active profile must also deactivate it.");
    }

    [TestMethod]
    public async Task SetProfileEnabledAsync_WhenProfileNotFound_ReturnsError()
    {
        await using var fixture = await CreateFixtureAsync();

        var service = new ProfilesPageService(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new TestRefreshTrigger(),
            new AppEventBus());

        var error = await service.SetProfileEnabledAsync("does-not-exist", enabled: false, CancellationToken.None);

        Assert.IsNotNull(error);
    }

    private static async Task SeedProfilesAsync(SqliteConnection connection, params (string ProfileId, bool IsActive)[] profiles)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);

        var now = DateTime.UtcNow;
        foreach (var profile in profiles)
        {
            db.Profiles.Add(new Profile
            {
                ProfileId = profile.ProfileId,
                Name = profile.ProfileId,
                Enabled = true,
                IsActive = profile.IsActive,
                OutputName = "m3undle",
                MergeMode = "replace",
                CreatedUtc = now,
                UpdatedUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedProfileProviderAsync(TestFixture fixture, string profileId)
    {
        await using var db = fixture.CreateDbContext();
        var now = DateTime.UtcNow;
        var provider = new Provider
        {
            ProviderId = Guid.NewGuid().ToString(),
            Name = "Test Provider",
            Enabled = true,
            PlaylistUrl = "http://example.com/playlist.m3u",
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.Providers.Add(provider);
        db.ProfileProviders.Add(new ProfileProvider
        {
            ProfileId = profileId,
            ProviderId = provider.ProviderId,
            Priority = 1,
            Enabled = true,
        });
        await db.SaveChangesAsync();
    }

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

        public ApplicationDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Connection)
                .Options;
            return new ApplicationDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestRefreshTrigger : IRefreshTrigger
    {
        public bool IsRefreshingValue { get; set; }
        public int TriggerRefreshCallCount { get; private set; }

        public bool IsRefreshing => IsRefreshingValue;
        public DateTime? RefreshStartedAt => null;
        public string? CurrentActivity => null;

        public bool TriggerRefresh()
        {
            TriggerRefreshCallCount++;
            return true;
        }

        public bool TriggerBuildOnly() => true;
        public void CancelRefresh() { }
    }
}
