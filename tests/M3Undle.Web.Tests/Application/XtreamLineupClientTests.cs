using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Tests.Stubs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class XtreamLineupClientTests
{
    // -------------------------------------------------------------------------
    // Live channels
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task BuildLineup_LiveChannels_ReturnsCorrectUrlsAndNames()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = """[{"category_id":"1","category_name":"News"}]""",
            ["/player_api.php?action=get_live_streams"] = """[{"stream_id":100,"name":"CNN","epg_channel_id":"cnn.us","stream_icon":"","category_id":"1"}]""",
        };

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            SimpleProvider("p1"), "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.HasCount(1, result.Channels);
        var ch = result.Channels[0];
        Assert.AreEqual("CNN", ch.DisplayName);
        Assert.AreEqual("cnn.us", ch.TvgId);
        Assert.AreEqual("News", ch.GroupTitle);
        Assert.AreEqual("http://panel.test:8080/live/user/pass/100.ts", ch.StreamUrl);
    }

    [TestMethod]
    public async Task BuildLineup_StringTypedStreamId_ParsesCorrectly()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = """[{"category_id":"1","category_name":"Sports"}]""",
            ["/player_api.php?action=get_live_streams"] = """[{"stream_id":"42","name":"ESPN","epg_channel_id":"espn.hd","category_id":"1"}]""",
        };

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            SimpleProvider("p1"), "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.HasCount(1, result.Channels);
        Assert.AreEqual("http://panel.test:8080/live/user/pass/42.ts", result.Channels[0].StreamUrl);
    }

    [TestMethod]
    public async Task BuildLineup_MissingEpgChannelId_TvgIdIsNull()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = """[{"category_id":"1","category_name":"Live"}]""",
            ["/player_api.php?action=get_live_streams"] = """[{"stream_id":1,"name":"Channel A","category_id":"1"}]""",
        };

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            SimpleProvider("p1"), "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.HasCount(1, result.Channels);
        Assert.IsNull(result.Channels[0].TvgId);
    }

    [TestMethod]
    public async Task BuildLineup_CategoryIdsArray_EmitsOneChannelPerCategory()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = """[{"category_id":"1","category_name":"UK"},{"category_id":"2","category_name":"US"}]""",
            ["/player_api.php?action=get_live_streams"] = """[{"stream_id":7,"name":"BBC","epg_channel_id":"bbc","category_ids":[1,2]}]""",
        };

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            SimpleProvider("p1"), "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.HasCount(2, result.Channels);
        var groups = result.Channels.Select(c => c.GroupTitle).ToHashSet();
        Assert.IsTrue(groups.Contains("UK"));
        Assert.IsTrue(groups.Contains("US"));
        // Both entries point to the same stream URL
        Assert.IsTrue(result.Channels.All(c => c.StreamUrl == "http://panel.test:8080/live/user/pass/7.ts"));
    }

    // -------------------------------------------------------------------------
    // VOD channels
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task BuildLineup_VodEnabled_ReturnsVodChannels()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
            ["/player_api.php?action=get_vod_categories"] = """[{"category_id":"10","category_name":"Movies"}]""",
            ["/player_api.php?action=get_vod_streams"] = """[{"stream_id":200,"name":"The Matrix","stream_icon":"","container_extension":"mkv","category_id":"10"}]""",
        };

        var provider = SimpleProvider("p1");
        provider.IncludeVod = true;

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            provider, "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.HasCount(1, result.Channels);
        var ch = result.Channels[0];
        Assert.AreEqual("The Matrix", ch.DisplayName);
        Assert.AreEqual("http://panel.test:8080/movie/user/pass/200.mkv", ch.StreamUrl);
        Assert.AreEqual("Movies", ch.GroupTitle);
        Assert.AreEqual("200", ch.CatalogItemId);
        Assert.AreEqual("The Matrix", ch.CatalogTitle);
    }

    [TestMethod]
    public async Task BuildLineup_VodDisabled_VodChannelsNotFetched()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
        };

        var provider = SimpleProvider("p1");
        provider.IncludeVod = false;

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            provider, "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.IsEmpty(result.Channels);
        Assert.IsFalse(handler.RequestedPaths.Any(p => p.Contains("get_vod")),
            "VOD endpoints should not be called when IncludeVod is false.");
    }

    // -------------------------------------------------------------------------
    // Series + cache
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task BuildLineup_SeriesFirstSync_DoesNotBlockAndEnqueuesBackgroundExpansion()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
            ["/player_api.php?action=get_series_categories"] = """[{"category_id":"5","category_name":"Drama"}]""",
            ["/player_api.php?action=get_series"] = """[{"series_id":1001,"name":"Breaking Bad","cover":"","category_id":"5","last_modified":"1000"}]""",
        };

        var provider = SimpleProvider("p1");
        provider.IncludeSeries = true;

        var (client, _, queue) = CreateClient(handler, seedProviderId: "p1");
        var result = await client.BuildLineupFromCredentialsAsync(
            provider, "http://panel.test:8080", "user", "pass", CancellationToken.None);

        // No inline results from the stub queue — expansion is handed to the worker.
        Assert.IsEmpty(result.Channels);
        Assert.IsNotNull(result.CatalogItems);
        var indexedSeries = result.CatalogItems.Single();
        Assert.AreEqual("1001", indexedSeries.ProviderItemId);
        Assert.AreEqual("Breaking Bad", indexedSeries.Title);
        Assert.AreEqual("Drama", indexedSeries.GroupTitle);
        Assert.AreEqual("series", indexedSeries.ContentType);
        Assert.IsFalse(handler.RequestedPaths.Any(p => p.Contains("get_series_info")),
            "The lineup client itself must never call get_series_info — the expansion service owns that.");

        Assert.HasCount(1, queue.Jobs);
        var job = queue.Jobs[0];
        Assert.AreEqual("p1", job.ProviderId);
        Assert.AreEqual("http://panel.test:8080", job.BaseUrl);
        Assert.HasCount(1, job.Series);
        Assert.AreEqual(1001, job.Series[0].SeriesId);
        Assert.AreEqual(1000L, job.Series[0].LastModifiedEpoch);
    }

    [TestMethod]
    public async Task BuildLineup_InlineExpansionResults_PublishImmediately()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
            ["/player_api.php?action=get_series_categories"] = """[{"category_id":"5","category_name":"Drama"}]""",
            ["/player_api.php?action=get_series"] = """[{"series_id":1001,"name":"Breaking Bad","cover":"","category_id":"5","last_modified":"1000"}]""",
        };

        var provider = SimpleProvider("p1");
        provider.IncludeSeries = true;

        var (client, _, queue) = CreateClient(handler, seedProviderId: "p1");
        queue.InlineResults[1001] = new XtreamSeriesExpanded(1001, 1000L,
            SeriesInfoJson(1001, "Breaking Bad", season: "1", episodes: [("1", 1, "Pilot", "mkv")]));

        var result = await client.BuildLineupFromCredentialsAsync(
            provider, "http://panel.test:8080", "user", "pass", CancellationToken.None);

        // Episodes fetched inside the inline budget appear in the very first lineup.
        Assert.HasCount(1, result.Channels);
        Assert.Contains("Pilot", result.Channels[0].DisplayName);
        Assert.Contains("S01E01", result.Channels[0].DisplayName);
        Assert.AreEqual("Drama", result.Channels[0].GroupTitle);
        Assert.AreEqual("1001", result.Channels[0].CatalogItemId);
        Assert.AreEqual("Breaking Bad", result.Channels[0].CatalogTitle);
    }

    [TestMethod]
    public async Task BuildLineup_SeriesUnchangedLastModified_SkipsGetSeriesInfo()
    {
        // Pre-populate cache with matching last_modified
        var episodesJson = SeriesInfoJson(1001, "Breaking Bad",
            season: "1", episodes: [("10", 1, "Pilot", "mkv"), ("11", 2, "Cat's in the Bag", "mkv")]);

        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
            ["/player_api.php?action=get_series_categories"] = """[{"category_id":"5","category_name":"Drama"}]""",
            ["/player_api.php?action=get_series"] = """[{"series_id":1001,"name":"Breaking Bad","cover":"","category_id":"5","last_modified":"9999"}]""",
        };

        var provider = SimpleProvider("p1");
        provider.IncludeSeries = true;

        var (client, scopeFactory, queue) = CreateClient(handler, seedProviderId: "p1");

        // Pre-populate the cache
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ctx.XtreamSeriesCache.Add(new XtreamSeriesCache
            {
                ProviderId = "p1",
                SeriesId = 1001,
                LastModifiedEpoch = 9999L,
                EpisodesJson = episodesJson,
            });
            await ctx.SaveChangesAsync();
        }

        var result = await client.BuildLineupFromCredentialsAsync(
            provider, "http://panel.test:8080", "user", "pass", CancellationToken.None);

        // Episodes should still come from cache
        Assert.HasCount(2, result.Channels);

        // Nothing to expand — no background job queued
        Assert.IsEmpty(queue.Jobs);
    }

    [TestMethod]
    public async Task BuildLineup_SeriesChangedLastModified_PublishesStaleEpisodesAndEnqueuesRefetch()
    {
        var oldEpisodesJson = SeriesInfoJson(1001, "Breaking Bad",
            season: "1", episodes: [("10", 1, "Pilot", "mkv")]);

        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
            ["/player_api.php?action=get_series_categories"] = "[]",
            ["/player_api.php?action=get_series"] = """[{"series_id":1001,"name":"Breaking Bad","cover":"","category_id":"5","last_modified":"2000"}]""",
        };

        var provider = SimpleProvider("p1");
        provider.IncludeSeries = true;

        var (client, scopeFactory, queue) = CreateClient(handler, seedProviderId: "p1");

        // Pre-populate the cache with old last_modified
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ctx.XtreamSeriesCache.Add(new XtreamSeriesCache
            {
                ProviderId = "p1",
                SeriesId = 1001,
                LastModifiedEpoch = 1000L,  // differs from 2000 in get_series response
                EpisodesJson = oldEpisodesJson,
            });
            await ctx.SaveChangesAsync();
        }

        var result = await client.BuildLineupFromCredentialsAsync(
            provider, "http://panel.test:8080", "user", "pass", CancellationToken.None);

        // Stale cached episodes still publish until the background re-fetch lands.
        Assert.HasCount(1, result.Channels);
        Assert.IsFalse(handler.RequestedPaths.Any(p => p.Contains("get_series_info")));

        // Changed series queued for background re-expansion with the NEW epoch.
        Assert.HasCount(1, queue.Jobs);
        Assert.AreEqual(2000L, queue.Jobs[0].Series.Single(s => s.SeriesId == 1001).LastModifiedEpoch);
    }

    [TestMethod]
    public async Task BuildLineup_SeriesEpisodes_GetCategoryNameAsGroupTitle()
    {
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = AuthOk(),
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
            ["/player_api.php?action=get_series_categories"] = """[{"category_id":"5","category_name":"Drama"}]""",
            ["/player_api.php?action=get_series"] = """[{"series_id":1001,"name":"Breaking Bad","cover":"","category_id":"5","last_modified":"1000"}]""",
        };

        var provider = SimpleProvider("p1");
        provider.IncludeSeries = true;

        var (client, scopeFactory, _) = CreateClient(handler, seedProviderId: "p1");

        // Episodes already cached (as the background worker would have left them).
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ctx.XtreamSeriesCache.Add(new XtreamSeriesCache
            {
                ProviderId = "p1",
                SeriesId = 1001,
                LastModifiedEpoch = 1000L,
                EpisodesJson = SeriesInfoJson(1001, "Breaking Bad",
                    season: "1", episodes: [("1", 1, "Pilot", "mkv")]),
            });
            await ctx.SaveChangesAsync();
        }

        var result = await client.BuildLineupFromCredentialsAsync(
            provider, "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.HasCount(1, result.Channels);
        Assert.AreEqual("Drama", result.Channels[0].GroupTitle);
        Assert.AreEqual("http://panel.test:8080/series/user/pass/1.mkv", result.Channels[0].StreamUrl);
    }

    // -------------------------------------------------------------------------
    // AccountInfo
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task BuildLineup_ValidAuth_ReturnsAccountInfo()
    {
        var expiry = new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = $"{{\"user_info\":{{\"auth\":1,\"exp_date\":\"{expiry.ToUnixTimeSeconds()}\",\"status\":\"Active\",\"max_connections\":\"2\"}}}}",
            ["/player_api.php?action=get_live_categories"] = "[]",
            ["/player_api.php?action=get_live_streams"] = "[]",
        };

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            SimpleProvider("p1"), "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.IsNotNull(result.AccountInfo);
        Assert.AreEqual(expiry.UtcDateTime, result.AccountInfo!.ExpiresUtc);
        Assert.AreEqual("Active", result.AccountInfo.Status);
        Assert.AreEqual(2, result.AccountInfo.MaxConnections);
    }

    [TestMethod]
    public async Task BuildLineup_AuthFailsNonFatal_StillReturnsChannels()
    {
        // Auth probe returns non-auth response (panel quirk) — lineup should still be built
        var handler = new MultiRouteHandler
        {
            ["/player_api.php"] = """{"user_info":{"auth":0}}""",
            ["/player_api.php?action=get_live_categories"] = """[{"category_id":"1","category_name":"Live"}]""",
            ["/player_api.php?action=get_live_streams"] = """[{"stream_id":55,"name":"Sky News","category_id":"1"}]""",
        };

        var (client, _, _) = CreateClient(handler);
        var result = await client.BuildLineupFromCredentialsAsync(
            SimpleProvider("p1"), "http://panel.test:8080", "user", "pass", CancellationToken.None);

        Assert.IsNull(result.AccountInfo);
        Assert.HasCount(1, result.Channels);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (XtreamLineupClient Client, IServiceScopeFactory ScopeFactory, RecordingSeriesExpansionQueue Queue) CreateClient(
        HttpMessageHandler handler, string? seedProviderId = null)
    {
        var scopeFactory = CreateScopeFactory(seedProviderId);
        var envSvc = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envSvc);
        var factory = new FakeHttpClientFactory(handler);
        var queue = new RecordingSeriesExpansionQueue();

        var client = new XtreamLineupClient(
            factory,
            scopeFactory,
            encryption,
            new RefreshActivityTracker(),
            queue,
            NullLogger<XtreamLineupClient>.Instance);

        return (client, scopeFactory, queue);
    }

    internal static IServiceScopeFactory CreateScopeFactory(string? seedProviderId)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // Ensure schema exists
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.EnsureCreated();

            // Seed a Provider row so the xtream_series_cache FK constraint is satisfied.
            if (seedProviderId is not null)
            {
                var now = DateTime.UtcNow;
                db.Providers.Add(new Provider
                {
                    ProviderId = seedProviderId,
                    Name = seedProviderId,
                    Enabled = true,
                    PlaylistUrl = string.Empty,
                    TimeoutSeconds = 30,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                });
                db.SaveChanges();
            }
        }

        return scopeFactory;
    }

    private static Provider SimpleProvider(string providerId) => new()
    {
        ProviderId = providerId,
        Name = providerId,
        Enabled = true,
        PlaylistUrl = string.Empty,
        TimeoutSeconds = 30,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static string AuthOk()
        => """{"user_info":{"auth":1,"exp_date":"1893456000","status":"Active","max_connections":"2"}}""";

    private static string SeriesInfoJson(
        int seriesId, string name, string season,
        IEnumerable<(string EpId, int EpNum, string Title, string Ext)> episodes)
    {
        var epArray = string.Join(",", episodes.Select(e =>
            $"{{\"id\":\"{e.EpId}\",\"episode_num\":{e.EpNum},\"title\":\"{e.Title}\",\"container_extension\":\"{e.Ext}\"}}"));
        return $"{{\"info\":{{\"name\":\"{name}\"}},\"episodes\":{{\"{season}\":[{epArray}]}}}}";
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Routes GET requests to different JSON responses based on path+query.
    private sealed class MultiRouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);
        public List<string> RequestedPaths { get; } = [];

        public string this[string pathAndQuery]
        {
            set => _routes[pathAndQuery] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;

            // Strip credentials from query so route matching works regardless of user/pass values.
            // Match on action= or lack of action (bare player_api.php).
            var matchKey = ExtractRouteKey(path);
            RequestedPaths.Add(matchKey);

            if (_routes.TryGetValue(matchKey, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string ExtractRouteKey(string pathAndQuery)
        {
            // Extract just the path and action parameter, ignoring username/password.
            var uri = new Uri("http://x" + pathAndQuery);
            var queryParts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            var action = queryParts.FirstOrDefault(p => p.StartsWith("action=", StringComparison.Ordinal));
            var seriesId = queryParts.FirstOrDefault(p => p.StartsWith("series_id=", StringComparison.Ordinal));

            if (action is null && seriesId is null)
                return uri.AbsolutePath; // bare /player_api.php

            var key = $"{uri.AbsolutePath}?{action}";
            if (seriesId is not null)
                key += $"&{seriesId}";
            return key;
        }
    }
}
