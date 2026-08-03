using System.Net;
using System.Text.Json;
using M3Undle.Web.Api;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using M3Undle.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Api;

[TestClass]
public sealed class XtreamEndpointTests
{
    [TestMethod]
    public async Task PlayerApi_NoAction_ReturnsAccountInfoWithEchoedCredentials()
    {
        await using var factory = new XtreamApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/player_api.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "test-user",
            ["password"] = "secret",
        }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var userInfo = json.RootElement.GetProperty("user_info");
        var serverInfo = json.RootElement.GetProperty("server_info");

        // Clients like IPTV Smarters use the returned username/password for all
        // subsequent requests — empty values cause the download phase to silently stop.
        Assert.AreEqual(1, userInfo.GetProperty("auth").GetInt32());
        Assert.AreEqual("test-user", userInfo.GetProperty("username").GetString());
        Assert.AreEqual("secret", userInfo.GetProperty("password").GetString());

        Assert.IsTrue(serverInfo.TryGetProperty("url", out _));
        Assert.IsTrue(serverInfo.TryGetProperty("port", out _));
    }

    [TestMethod]
    public async Task PlayerApi_GetAccountInfoAction_ReturnsAccountInfoWithEchoedCredentials()
    {
        await using var factory = new XtreamApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/player_api.php?username=test-user&password=secret&action=get_account_info");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var userInfo = json.RootElement.GetProperty("user_info");

        Assert.AreEqual(1, userInfo.GetProperty("auth").GetInt32());
        Assert.AreEqual("test-user", userInfo.GetProperty("username").GetString());
        Assert.AreEqual("secret", userInfo.GetProperty("password").GetString());
    }

    [TestMethod]
    public async Task PlayerApi_PostFormAction_ReturnsLiveStreams()
    {
        await using var factory = new XtreamApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/player_api.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "test-user",
            ["password"] = "secret",
            ["action"] = "get_live_streams",
        }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(JsonValueKind.Array, json.RootElement.ValueKind);
        var streams = json.RootElement.EnumerateArray().ToArray();
        Assert.HasCount(2, streams);
        Assert.IsTrue(streams.All(x => x.GetProperty("stream_type").GetString() == "live"));
    }

    [TestMethod]
    public async Task PlayerApi_GetVodInfo_ReturnsMatchingMetadataAndActualContainer()
    {
        await using var factory = new XtreamApiFactory();
        using var client = factory.CreateClient();

        using var streamsResponse = await client.GetAsync(
            "/player_api.php?username=test-user&password=secret&action=get_vod_streams");
        using var streamsJson = JsonDocument.Parse(await streamsResponse.Content.ReadAsStringAsync());
        var vodId = streamsJson.RootElement[0].GetProperty("stream_id").GetString();

        using var response = await client.GetAsync(
            $"/player_api.php?username=test-user&password=secret&action=get_vod_info&vod_id={vodId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("Movie One", json.RootElement.GetProperty("movie_data").GetProperty("name").GetString());
        Assert.AreEqual(vodId, json.RootElement.GetProperty("movie_data").GetProperty("stream_id").GetString());
        Assert.AreEqual("mkv", json.RootElement.GetProperty("movie_data").GetProperty("container_extension").GetString());
        Assert.AreEqual("Movies", json.RootElement.GetProperty("info").GetProperty("genre").GetString());
    }

    [TestMethod]
    public async Task PlayerApi_GetSeriesInfo_EmitsEpisodeInfoAndActualContainer()
    {
        await using var factory = new XtreamApiFactory();
        using var client = factory.CreateClient();

        using var seriesResponse = await client.GetAsync(
            "/player_api.php?username=test-user&password=secret&action=get_series");
        using var seriesJson = JsonDocument.Parse(await seriesResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(1, seriesJson.RootElement.GetArrayLength());
        Assert.AreEqual("Show One", seriesJson.RootElement[0].GetProperty("name").GetString());
        var seriesIdProperty = seriesJson.RootElement[0].GetProperty("series_id");
        Assert.AreEqual(JsonValueKind.Number, seriesIdProperty.ValueKind,
            "Xtream clients such as Smarters require series_id to be a JSON number.");
        var seriesId = seriesIdProperty.GetInt32();
        Assert.IsTrue(seriesId is >= 1 and <= XtreamStreamIdCache.MaxStreamId,
            "Numeric series IDs must remain below the BrightScript scientific-notation threshold.");

        using var response = await client.GetAsync(
            $"/player_api.php?username=test-user&password=secret&action=get_series_info&series_id={seriesId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var episodes = json.RootElement.GetProperty("episodes").GetProperty("1");
        Assert.AreEqual(2, episodes.GetArrayLength());
        var episode = episodes[0];
        Assert.AreEqual(1, episode.GetProperty("episode_num").GetInt32());
        Assert.AreEqual(2, episodes[1].GetProperty("episode_num").GetInt32());
        Assert.AreEqual("mp4", episode.GetProperty("container_extension").GetString());
        Assert.IsTrue(episode.TryGetProperty("info", out _));
        Assert.IsTrue(episode.TryGetProperty("custom_sid", out _));
        StringAssert.EndsWith(episode.GetProperty("direct_source").GetString()!, ".mp4");

        var legacySeriesId = XtreamStreamIdCache.ToLegacyStreamId("series:Show One");
        using var legacyResponse = await client.GetAsync(
            $"/player_api.php?username=test-user&password=secret&action=get_series_info&series_id={legacySeriesId}");
        using var legacyJson = JsonDocument.Parse(await legacyResponse.Content.ReadAsStringAsync());
        Assert.IsTrue(legacyJson.RootElement.TryGetProperty("episodes", out _),
            "Previously advertised 31-bit series IDs must continue resolving for cached clients.");
    }

    [TestMethod]
    public void ResolveContainerExtension_IgnoresQueryAndFragmentAndUsesFallback()
    {
        Assert.AreEqual("mkv", XtreamEndpoints.ResolveContainerExtension("https://provider/movie.mkv?token=x#part", "mp4"));
        Assert.AreEqual("mp4", XtreamEndpoints.ResolveContainerExtension("https://provider/play?id=1", "mp4"));
    }

    [TestMethod]
    public async Task GetPhp_PostFormCredentials_ReturnsM3uPlaylist()
    {
        await using var factory = new XtreamApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/get.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "test-user",
            ["password"] = "secret",
        }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/x-mpegurl", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.StartsWith(body, "#EXTM3U");
        StringAssert.Contains(body, "Alpha");
    }

    [TestMethod]
    public async Task XmltvPhp_GetWithQueryStringCredentials_ReturnsXmlFeed()
    {
        await using var factory = new XmltvPhpFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/xmltv.php?username=test-user&password=secret");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/xml", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.StartsWith(body, "<?xml");
    }

    [TestMethod]
    public async Task XmltvPhp_PostFormCredentials_ReturnsXmlFeed()
    {
        await using var factory = new XmltvPhpFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/xmltv.php", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "test-user",
            ["password"] = "secret",
        }));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/xml", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public void BuildGeneratedXtreamHlsManifestRedirectUrl_UsesXtreamPathCredentials()
    {
        var context = CreateXtreamRouteContext(
            scheme: "http",
            host: "toontown-tv-srv1:8080",
            pathBase: "/iptv",
            username: "john@example.com",
            password: "doe pass");

        var url = XtreamEndpoints.BuildGeneratedXtreamHlsManifestRedirectUrl(context, "session 1");

        Assert.AreEqual(
            "/iptv/hls/generated/john%40example.com/doe%20pass/session%201/index.m3u8",
            url);
        Assert.IsFalse(url.Contains("username=", StringComparison.Ordinal));
        Assert.IsFalse(url.Contains("password=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildGeneratedXtreamHlsAssetBaseUrl_UsesXtreamPathCredentials()
    {
        var context = CreateXtreamRouteContext(
            scheme: "http",
            host: "toontown-tv-srv1:8080",
            pathBase: string.Empty,
            username: "john@example.com",
            password: "doe pass");

        var url = XtreamEndpoints.BuildGeneratedXtreamHlsAssetBaseUrl(context, "session 1");

        Assert.AreEqual(
            "http://toontown-tv-srv1:8080/hls/generated/john%40example.com/doe%20pass/session%201",
            url);
    }

    private sealed class XtreamApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-xtream-endpoints-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["M3Undle:Paths:DataDirectory"] = _tempDataDir,
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (dbDescriptor is not null)
                    services.Remove(dbDescriptor);

                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite(WebApplicationFactoryTestCleanup.CreateSqliteConnectionString(_tempDataDir))
                           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

                services.RemoveAll<IAccessResolver>();
                services.AddScoped<IAccessResolver, StubAccessResolver>();

                services.RemoveAll<ILineupRenderer>();
                services.AddScoped<ILineupRenderer, StubLineupRenderer>();
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
        }
    }

    private static DefaultHttpContext CreateXtreamRouteContext(
        string scheme,
        string host,
        string pathBase,
        string username,
        string password)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = HostString.FromUriComponent(host);
        context.Request.PathBase = pathBase;
        context.Request.RouteValues["xtreamUser"] = username;
        context.Request.RouteValues["xtreamPass"] = password;
        return context;
    }

    private sealed class XmltvPhpFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-xmltvphp-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["M3Undle:Paths:DataDirectory"] = _tempDataDir,
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (dbDescriptor is not null)
                    services.Remove(dbDescriptor);

                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite(WebApplicationFactoryTestCleanup.CreateSqliteConnectionString(_tempDataDir))
                           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

                services.RemoveAll<IAccessResolver>();
                services.AddScoped<IAccessResolver, StubAccessResolver>();

                services.RemoveAll<ILineupRenderer>();
                services.AddScoped<ILineupRenderer, StubLineupRenderer>();

                services.RemoveAll<IXmlTvSerializer>();
                services.AddSingleton<IXmlTvSerializer, StubXmlTvSerializer>();
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
        }
    }

    private sealed class StubXmlTvSerializer : IXmlTvSerializer
    {
        private const string MinimalXmlTv = """<?xml version="1.0" encoding="utf-8"?><tv></tv>""";

        public IResult Serialize(RenderedLineup lineup)
            => Results.Content(MinimalXmlTv, "application/xml");
    }

    private sealed class StubAccessResolver : IAccessResolver
    {
        public ValueTask<ClientAccessResolutionResult> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var credential = new AccessCredential(
                Id: "test-credential",
                Username: "test-user",
                PasswordHash: string.Empty,
                Enabled: true,
                AuthType: AccessCredentialAuthType.UsernamePassword);

            var access = new ResolvedClientAccess(
                Credential: credential,
                Binding: new AccessBinding(
                    CredentialId: credential.Id,
                    ActiveProfileId: "profile-1",
                    AllowedProfileIds: ["profile-1"],
                    VirtualTunerId: "hdhr-main"),
                Transport: ClientCredentialTransport.Form,
                UrlCredential: new AccessUrlCredential("test-user", "secret"));

            return ValueTask.FromResult(ClientAccessResolutionResult.Success(access));
        }
    }

    private sealed class StubLineupRenderer : ILineupRenderer
    {
        public Task<RenderedLineup?> TryRenderActiveLineupAsync(string? profileId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<RenderedLineup?>(new RenderedLineup(
                SnapshotId: "snapshot-1",
                ProfileId: profileId ?? "profile-1",
                SnapshotCreatedUtc: DateTime.UtcNow,
                ChannelIndexPath: "unused",
                XmltvPath: null,
                Channels:
                [
                    new RenderedLineupChannel("live-1", "Alpha", "alpha.tv", "Alpha", null, "News", 11, "http://example.com/live/alpha.ts", "live"),
                    new RenderedLineupChannel("vod-1", "Movie One", "movie.one", "Movie One", null, "Movies", null, "http://example.com/movie/one.mkv", "vod"),
                    new RenderedLineupChannel("series-1", "Show One S01E01 — Pilot", "show.one", "Show One", null, "Series", null, "http://example.com/series/one.mp4?token=secret", "series"),
                    new RenderedLineupChannel("series-2", "Show One S01E02 — Second", "show.one", "Show One", null, "Series", null, "http://example.com/series/two.mkv?token=secret", "series"),
                    new RenderedLineupChannel("live-2", "Bravo", "bravo.tv", "Bravo HD", null, "News", null, "http://example.com/live/bravo.ts", "live"),
                ]));
        }
    }
}
