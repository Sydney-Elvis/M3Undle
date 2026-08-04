using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

// The on-demand series-detail tests mutate process-wide M3UNDLE_ENCRYPTION_KEY(S) via EnvScope;
// the assembly parallelizes at method level (GlobalTestSettings.cs), so this must run
// sequentially relative to other [DoNotParallelize] tests (see SecretEncryptionServiceTests).
[TestClass]
[DoNotParallelize]
public sealed class CatalogPageServiceTests
{
    [TestMethod]
    public async Task ListGroupsAsync_ReturnsActiveCatalogGroupsForLinkedProvidersOnly()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var now = DateTime.UtcNow;

        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.AddRange(
                CreateProfile("profile-1", "Primary", active: true),
                CreateProfile("profile-2", "Other", active: false));

            db.Providers.AddRange(
                CreateProvider("provider-1", "Linked", includeVod: true, includeSeries: false),
                CreateProvider("provider-2", "Unlinked", includeVod: true, includeSeries: true));

            db.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = "profile-1",
                ProviderId = "provider-1",
                Enabled = true,
            });

            db.ProviderGroups.AddRange(
                CreateGroup("vod-active", "provider-1", "Movies", "vod", active: true, count: 15, now),
                CreateGroup("series-active", "provider-1", "Drama", "series", active: true, count: 27, now),
                CreateGroup("live-active", "provider-1", "News", "live", active: true, count: 4, now),
                CreateGroup("vod-inactive", "provider-1", "Old Movies", "vod", active: false, count: 9, now),
                CreateGroup("vod-unlinked", "provider-2", "Other Movies", "vod", active: true, count: 20, now));

            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture);
        var groups = await service.ListGroupsAsync("profile-1", CancellationToken.None);

        Assert.HasCount(2, groups);
        var movies = groups.Single(x => x.ProviderGroupId == "vod-active");
        Assert.AreEqual("Linked", movies.ProviderName);
        Assert.AreEqual(15, movies.ItemCount);
        Assert.IsTrue(movies.ContentTypeEnabled);

        var series = groups.Single(x => x.ProviderGroupId == "series-active");
        Assert.IsFalse(series.ContentTypeEnabled);
    }

    [TestMethod]
    public async Task GetDefaultProfileIdAsync_ReturnsEnabledActiveProfile()
    {
        await using var fixture = await CatalogFixture.CreateAsync();

        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.AddRange(
                CreateProfile("profile-1", "Primary", active: true),
                CreateProfile("profile-2", "Other", active: false));
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture);

        Assert.AreEqual("profile-1", await service.GetDefaultProfileIdAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateDecisionAsync_PersistsProfileScopedCatalogExclusion()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var now = DateTime.UtcNow;

        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.Add(CreateProfile("profile-1", "Primary", active: true));
            db.Providers.Add(CreateProvider("provider-1", "Linked", includeVod: true, includeSeries: true));
            db.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = "profile-1",
                ProviderId = "provider-1",
                Enabled = true,
            });
            db.ProviderGroups.Add(CreateGroup(
                "series-active", "provider-1", "Reality", "series", active: true, count: 27, now));
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture);
        Assert.IsTrue(await service.UpdateDecisionAsync(
            "profile-1", "series-active", "exclude", CancellationToken.None));

        var groups = await service.ListGroupsAsync("profile-1", CancellationToken.None);
        Assert.AreEqual("exclude", groups.Single().Decision);

        await using var verify = fixture.CreateDbContext();
        var filter = await verify.ProfileCatalogGroupFilters.SingleAsync();
        Assert.AreEqual("profile-1", filter.ProfileId);
        Assert.AreEqual("series-active", filter.ProviderGroupId);
        Assert.IsFalse(filter.IsNew);
    }

    [TestMethod]
    public async Task GetItemsAsync_SearchesAndPagesTitlesWithoutExposingProviderKeys()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var now = DateTime.UtcNow;

        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.Add(CreateProfile("profile-1", "Primary", active: true));
            db.Providers.Add(CreateProvider("provider-1", "Linked", includeVod: true, includeSeries: true));
            db.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = "profile-1",
                ProviderId = "provider-1",
                Enabled = true,
            });
            db.ProviderGroups.Add(CreateGroup(
                "movies", "provider-1", "Movies", "vod", active: true, count: 3, now));
            db.CatalogItems.AddRange(
                CreateCatalogItem("item-1", "movies", "id:101", "Alien", now),
                CreateCatalogItem("item-2", "movies", "id:102", "Aliens", now),
                CreateCatalogItem("item-3", "movies", "id:103", "Blade Runner", now));
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture);
        var result = await service.GetItemsAsync(
            "profile-1", "movies", page: 1, pageSize: 10, search: "alien", CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Total);
        CollectionAssert.AreEqual(new[] { "Alien", "Aliens" }, result.Items.Select(x => x.Title).ToArray());
    }

    [TestMethod]
    public async Task SearchItemsAsync_FindsMatchingMoviesAndSeriesAcrossCategories()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var now = DateTime.UtcNow;

        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.Add(CreateProfile("profile-1", "Primary", active: true));
            db.Providers.Add(CreateProvider("provider-1", "Linked", includeVod: true, includeSeries: true));
            db.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = "profile-1",
                ProviderId = "provider-1",
                Enabled = true,
            });
            db.ProviderGroups.AddRange(
                CreateGroup("movies", "provider-1", "Adventure", "vod", true, 1, now),
                CreateGroup("series", "provider-1", "Sci-Fi", "series", true, 1, now));
            var movie = CreateCatalogItem("movie-1", "movies", "id:101", "The Mandalorian Movie", now);
            var series = CreateCatalogItem("series-1", "series", "id:201", "The Mandalorian", now);
            series.ContentType = "series";
            series.EpisodeCount = 24;
            db.CatalogItems.AddRange(movie, series);
            await db.SaveChangesAsync();
        }

        var service = CreateService(fixture);
        var all = await service.SearchItemsAsync(
            "profile-1", null, 1, 10, "mandalorian", null, CancellationToken.None);
        Assert.AreEqual(2, all.Total);
        CollectionAssert.AreEquivalent(new[] { "vod", "series" }, all.Items.Select(x => x.ContentType).ToArray());
        Assert.IsTrue(all.Items.All(x => !string.IsNullOrWhiteSpace(x.GroupName)));

        var seriesOnly = await service.SearchItemsAsync(
            "profile-1", "series", 1, 10, "mandalorian", "sci", CancellationToken.None);
        Assert.AreEqual(1, seriesOnly.Total);
        Assert.AreEqual("series", seriesOnly.Items.Single().ContentType);
    }

    [TestMethod]
    public async Task GetItemDetailAsync_ParsesCachedSeriesAndEpisodes()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var now = DateTime.UtcNow;
        await using (var db = fixture.CreateDbContext())
        {
            db.Profiles.Add(CreateProfile("profile-1", "Primary", active: true));
            db.Providers.Add(CreateProvider("provider-1", "Linked", includeVod: true, includeSeries: true));
            db.ProfileProviders.Add(new ProfileProvider { ProfileId = "profile-1", ProviderId = "provider-1", Enabled = true });
            db.ProviderGroups.Add(CreateGroup("series", "provider-1", "Comedy", "series", true, 1, now));
            var item = CreateCatalogItem("series-1", "series", "id:201", "Abbott Elementary", now);
            item.ContentType = "series";
            item.ArtworkUrl = "https://images.example.test/abbott.jpg";
            db.CatalogItems.Add(item);
            db.XtreamSeriesCache.Add(new XtreamSeriesCache
            {
                ProviderId = "provider-1",
                SeriesId = 201,
                LastModifiedEpoch = 1,
                EpisodesJson = """{"info":{"plot":"Teachers make it work.","genre":"Comedy"},"episodes":{"1":[{"episode_num":1,"title":"Pilot","info":{"plot":"The first day.","release_date":"2021-12-07"}}]}}""",
            });
            await db.SaveChangesAsync();
        }

        var detail = await CreateService(fixture).GetItemDetailAsync(
            "profile-1", "series-1", CancellationToken.None);

        Assert.IsNotNull(detail);
        Assert.AreEqual("Teachers make it work.", detail.Plot);
        Assert.IsTrue(detail.HasArtwork);
        Assert.HasCount(1, detail.Seasons);
        Assert.AreEqual("Pilot", detail.Seasons[0].Episodes.Single().Title);
        Assert.AreEqual("The first day.", detail.Seasons[0].Episodes.Single().Plot);
    }

    [TestMethod]
    public async Task GetItemDetailAsync_FetchesSeriesInfoOnDemandWhenNotCached()
    {
        using var env = new EnvScope(key: RandomKey());
        var encryption = CreateEncryption();
        await using var fixture = await CatalogFixture.CreateAsync();
        await SeedUncachedSeriesAsync(fixture, encryption);

        const string seriesJson = """{"info":{"plot":"Paper company."},"episodes":{"1":[{"episode_num":1,"title":"Pilot"}]}}""";
        var handler = new RecordingHandler(seriesJson);

        var detail = await CreateService(fixture, handler, encryption)
            .GetItemDetailAsync("profile-1", "series-1", CancellationToken.None);

        Assert.IsNotNull(detail);
        Assert.IsNull(detail.MetadataNotice);
        Assert.AreEqual("Paper company.", detail.Plot);
        Assert.AreEqual("Pilot", detail.Seasons.Single().Episodes.Single().Title);

        var requested = handler.Requests.Single();
        StringAssert.Contains(requested, "action=get_series_info");
        StringAssert.Contains(requested, "series_id=201");

        // Written through so the next refresh builds episodes without re-fetching, but with
        // epoch 0 so the provider's real last_modified still compares unequal and re-expands.
        await using var db = fixture.CreateDbContext();
        var cached = await db.XtreamSeriesCache.SingleAsync(x => x.ProviderId == "provider-1" && x.SeriesId == 201);
        Assert.AreEqual(seriesJson, cached.EpisodesJson);
        Assert.AreEqual(0, cached.LastModifiedEpoch);
    }

    [TestMethod]
    public async Task GetItemDetailAsync_DoesNotRefetchSeriesThatIsAlreadyCached()
    {
        using var env = new EnvScope(key: RandomKey());
        var encryption = CreateEncryption();
        await using var fixture = await CatalogFixture.CreateAsync();
        await SeedUncachedSeriesAsync(fixture, encryption);

        await using (var db = fixture.CreateDbContext())
        {
            db.XtreamSeriesCache.Add(new XtreamSeriesCache
            {
                ProviderId = "provider-1",
                SeriesId = 201,
                LastModifiedEpoch = 99,
                EpisodesJson = """{"info":{"plot":"From cache."},"episodes":{}}""",
            });
            await db.SaveChangesAsync();
        }

        var handler = new RecordingHandler("""{"info":{"plot":"From network."}}""");
        var detail = await CreateService(fixture, handler, encryption)
            .GetItemDetailAsync("profile-1", "series-1", CancellationToken.None);

        Assert.IsNotNull(detail);
        Assert.AreEqual("From cache.", detail.Plot);
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task GetItemDetailAsync_KeepsNoticeWhenOnDemandSeriesFetchFails()
    {
        using var env = new EnvScope(key: RandomKey());
        var encryption = CreateEncryption();
        await using var fixture = await CatalogFixture.CreateAsync();
        await SeedUncachedSeriesAsync(fixture, encryption);

        var handler = new RecordingHandler(null, System.Net.HttpStatusCode.InternalServerError);
        var detail = await CreateService(fixture, handler, encryption)
            .GetItemDetailAsync("profile-1", "series-1", CancellationToken.None);

        Assert.IsNotNull(detail);
        Assert.IsNull(detail.Plot);
        StringAssert.Contains(detail.MetadataNotice, "not cached yet");

        // A failed lookup must not leave a poisoned empty cache row behind.
        await using var db = fixture.CreateDbContext();
        Assert.IsFalse(await db.XtreamSeriesCache.AnyAsync(x => x.ProviderId == "provider-1"));
    }

    // Series catalog item with an id: key and Xtream credentials, but no expansion cache row.
    private static async Task SeedUncachedSeriesAsync(CatalogFixture fixture, SecretEncryptionService encryption)
    {
        var now = DateTime.UtcNow;
        await using var db = fixture.CreateDbContext();
        db.Profiles.Add(CreateProfile("profile-1", "Primary", active: true));

        var provider = CreateProvider("provider-1", "Panel", includeVod: false, includeSeries: true);
        provider.XtreamBaseUrl = "http://panel.example.test";
        provider.XtreamUsername = "user";
        provider.XtreamEncryptedPassword = encryption.Encrypt("secret");
        db.Providers.Add(provider);

        db.ProfileProviders.Add(new ProfileProvider { ProfileId = "profile-1", ProviderId = "provider-1", Enabled = true });
        db.ProviderGroups.Add(CreateGroup("series", "provider-1", "Comedy", "series", true, 1, now));
        var item = CreateCatalogItem("series-1", "series", "id:201", "The Office", now);
        item.ContentType = "series";
        db.CatalogItems.Add(item);
        await db.SaveChangesAsync();
    }

    private static SecretEncryptionService CreateEncryption() =>
        new(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));

    private static string RandomKey()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static CatalogPageService CreateService(CatalogFixture fixture) =>
        new(fixture.Services.GetRequiredService<IServiceScopeFactory>(), new AppEventBus());

    private static CatalogPageService CreateService(
        CatalogFixture fixture, HttpMessageHandler handler, SecretEncryptionService encryption) =>
        new(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new AppEventBus(),
            new FakeHttpClientFactory(handler),
            encryption);

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(
        string? body,
        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? string.Empty),
            });
        }
    }

    /// <summary>Saves/restores the encryption key env vars around a test.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string? _previousKey;
        private readonly string? _previousKeys;

        public EnvScope(string? keys = null, string? key = null)
        {
            _previousKey = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY");
            _previousKeys = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS");
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", key);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", keys);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", _previousKey);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", _previousKeys);
        }
    }

    private static Profile CreateProfile(string id, string name, bool active)
    {
        var now = DateTime.UtcNow;
        return new Profile
        {
            ProfileId = id,
            Name = name,
            OutputName = "m3undle",
            MergeMode = "replace",
            Enabled = true,
            IsActive = active,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private static Provider CreateProvider(
        string id,
        string name,
        bool includeVod,
        bool includeSeries)
    {
        var now = DateTime.UtcNow;
        return new Provider
        {
            ProviderId = id,
            Name = name,
            PlaylistUrl = $"http://example.test/{id}",
            Enabled = true,
            IncludeVod = includeVod,
            IncludeSeries = includeSeries,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private static ProviderGroup CreateGroup(
        string id,
        string providerId,
        string name,
        string contentType,
        bool active,
        int count,
        DateTime now) => new()
        {
            ProviderGroupId = id,
            ProviderId = providerId,
            RawName = name,
            ContentType = contentType,
            Active = active,
            ChannelCount = count,
            FirstSeenUtc = now.AddDays(-1),
            LastSeenUtc = now,
        };

    private static CatalogItem CreateCatalogItem(
        string id,
        string providerGroupId,
        string providerItemKey,
        string title,
        DateTime now) => new()
        {
            CatalogItemId = id,
            ProviderId = "provider-1",
            ProviderGroupId = providerGroupId,
            ProviderItemKey = providerItemKey,
            ContentType = "vod",
            Title = title,
            Active = true,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private sealed class CatalogFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<ApplicationDbContext> options;
        private readonly ServiceProvider services;

        private CatalogFixture(
            SqliteConnection connection,
            DbContextOptions<ApplicationDbContext> options,
            ServiceProvider services)
        {
            this.connection = connection;
            this.options = options;
            this.services = services;
        }

        public IServiceProvider Services => services;

        public static async Task<CatalogFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
            var services = serviceCollection.BuildServiceProvider();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;
            var fixture = new CatalogFixture(connection, options, services);

            await using var db = fixture.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public ApplicationDbContext CreateDbContext() => new(options);

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
