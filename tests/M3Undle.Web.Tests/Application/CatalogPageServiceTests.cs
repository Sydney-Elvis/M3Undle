using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
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

    private static CatalogPageService CreateService(CatalogFixture fixture) =>
        new(fixture.Services.GetRequiredService<IServiceScopeFactory>(), new AppEventBus());

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
