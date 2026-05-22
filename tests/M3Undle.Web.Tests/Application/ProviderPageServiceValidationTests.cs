using M3Undle.Web.Application;
using M3Undle.Web.Contracts.Providers;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class ProviderPageServiceValidationTests
{
    [TestMethod]
    public async Task CreateProviderAsync_WhenPlaylistAndXtreamInputsMixed_ReturnsValidationError()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Services);

        var request = new CreateProviderRequest
        {
            Name = "Mixed Provider",
            PlaylistUrl = "https://example.test/playlist.m3u8",
            XtreamBaseUrl = "https://panel.example.test",
            XtreamUsername = "user",
            XtreamPassword = "pass",
            TimeoutSeconds = 120,
        };

        var (provider, error) = await service.CreateProviderAsync(request, CancellationToken.None);

        Assert.IsNull(provider);
        Assert.AreEqual("Playlist/file fields and Xtream fields are mutually exclusive.", error);

        await using var verify = fixture.CreateDbContext();
        Assert.AreEqual(0, await verify.Providers.CountAsync());
    }

    [TestMethod]
    public async Task CreateProviderAsync_WhenXtreamFieldsProvidedWithoutBaseUrl_ReturnsValidationError()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Services);

        var request = new CreateProviderRequest
        {
            Name = "Invalid Xtream Provider",
            XtreamUsername = "user",
            XtreamPassword = "pass",
            XtreamIncludeXmltv = true,
            TimeoutSeconds = 120,
        };

        var (provider, error) = await service.CreateProviderAsync(request, CancellationToken.None);

        Assert.IsNull(provider);
        Assert.AreEqual("xtreamUsername/xtreamPassword/xtreamIncludeXmltv require xtreamBaseUrl.", error);

        await using var verify = fixture.CreateDbContext();
        Assert.AreEqual(0, await verify.Providers.CountAsync());
    }

    [TestMethod]
    public async Task DeleteProviderAsync_WhenRefreshInProgress_ReturnsConflictError()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(
            fixture.Services,
            refreshTrigger: new TestRefreshTrigger(isRefreshing: true));

        var error = await service.DeleteProviderAsync("provider-1", CancellationToken.None);

        Assert.AreEqual("A snapshot refresh is currently in progress. Please wait for it to finish before deleting a provider.", error);
    }

    [TestMethod]
    public async Task DeleteProviderAsync_WhenProviderDoesNotExist_ReturnsNotFound()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Services);

        var error = await service.DeleteProviderAsync("missing-provider", CancellationToken.None);

        Assert.AreEqual("Provider not found.", error);
    }

    [TestMethod]
    public async Task DeleteProviderAsync_WhenProviderExists_DeletesProviderAndDependents()
    {
        await using var fixture = await CreateFixtureAsync();

        const string providerId = "provider-1";
        const string profileId = "profile-1";
        const string fetchRunId = "fetch-1";
        const string providerGroupId = "provider-group-1";
        const string providerChannelId = "provider-channel-1";
        const string canonicalChannelId = "canonical-channel-1";

        await using (var seed = fixture.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            seed.Profiles.Add(new Profile
            {
                ProfileId = profileId,
                Name = "Primary Profile",
                OutputName = "m3undle",
                MergeMode = "replace",
                Enabled = true,
                IsActive = true,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            seed.Providers.Add(new Provider
            {
                ProviderId = providerId,
                Name = "Provider One",
                Enabled = true,
                PlaylistUrl = "https://example.test/playlist.m3u",
                TimeoutSeconds = 120,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            seed.FetchRuns.Add(new FetchRun
            {
                FetchRunId = fetchRunId,
                ProviderId = providerId,
                StartedUtc = now,
                FinishedUtc = now,
                Status = "success",
                Type = "snapshot",
            });
            seed.ProviderGroups.Add(new ProviderGroup
            {
                ProviderGroupId = providerGroupId,
                ProviderId = providerId,
                RawName = "News",
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Active = true,
                ContentType = "live",
            });
            seed.ProviderChannels.Add(new ProviderChannel
            {
                ProviderChannelId = providerChannelId,
                ProviderId = providerId,
                DisplayName = "Channel One",
                StreamUrl = "https://example.test/stream/1",
                ProviderGroupId = providerGroupId,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Active = true,
                ContentType = "live",
                LastFetchRunId = fetchRunId,
            });
            seed.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = profileId,
                ProviderId = providerId,
                Priority = 1,
                Enabled = true,
            });
            seed.CanonicalChannels.Add(new CanonicalChannel
            {
                ChannelId = canonicalChannelId,
                ProfileId = profileId,
                DisplayName = "Canonical Channel One",
                ChannelNumber = 1,
                Enabled = true,
                IsEvent = false,
                EventPolicy = "none",
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            seed.ChannelSources.Add(new ChannelSource
            {
                ChannelSourceId = "channel-source-1",
                ChannelId = canonicalChannelId,
                ProviderId = providerId,
                ProviderChannelId = providerChannelId,
                Priority = 1,
                Enabled = true,
                HealthState = "healthy",
                CreatedUtc = now,
                UpdatedUtc = now,
            });

            await seed.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var reader = eventBus.Subscribe(out var unsubscriber);
        try
        {
            var service = CreateService(fixture.Services, eventBus: eventBus);

            var error = await service.DeleteProviderAsync(providerId, CancellationToken.None);

            Assert.IsNull(error);
            Assert.IsTrue(reader.TryRead(out var evt));
            Assert.AreEqual(AppEventKind.ProviderChanged, evt.Kind);
        }
        finally
        {
            unsubscriber.Dispose();
        }

        await using var verify = fixture.CreateDbContext();
        Assert.AreEqual(0, await verify.ChannelSources.CountAsync(x => x.ProviderId == providerId));
        Assert.AreEqual(0, await verify.ProviderChannels.CountAsync(x => x.ProviderId == providerId));
        Assert.AreEqual(0, await verify.ProviderGroups.CountAsync(x => x.ProviderId == providerId));
        Assert.AreEqual(0, await verify.FetchRuns.CountAsync(x => x.ProviderId == providerId));
        Assert.AreEqual(0, await verify.ProfileProviders.CountAsync(x => x.ProviderId == providerId));
        Assert.AreEqual(0, await verify.Providers.CountAsync(x => x.ProviderId == providerId));
        Assert.AreEqual(1, await verify.Profiles.CountAsync(x => x.ProfileId == profileId));
        Assert.AreEqual(1, await verify.CanonicalChannels.CountAsync(x => x.ChannelId == canonicalChannelId));
    }

    private static ProviderPageService CreateService(
        ServiceProvider services,
        IRefreshTrigger? refreshTrigger = null,
        AppEventBus? eventBus = null)
    {
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        return new ProviderPageService(
            services.GetRequiredService<IServiceScopeFactory>(),
            fetcher: null!,
            configService: null!,
            envVarService: envVars,
            encryption,
            refreshTrigger: refreshTrigger ?? new TestRefreshTrigger(),
            eventBus: eventBus ?? new AppEventBus(),
            logger: NullLogger<ProviderPageService>.Instance);
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

    private sealed class TestRefreshTrigger(bool isRefreshing = false) : IRefreshTrigger
    {
        public bool IsRefreshing { get; private set; } = isRefreshing;
        public DateTime? RefreshStartedAt => null;
        public bool TriggerRefresh()
        {
            IsRefreshing = true;
            return true;
        }
        public bool TriggerBuildOnly() => true;
        public void CancelRefresh() => IsRefreshing = false;
    }
}
