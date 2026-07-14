using System.Net;
using System.Net.Http.Headers;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Observability;
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
public sealed class VodSeriesMonitorEndpointTests
{
    [TestMethod]
    [DataRow("movie", "movie-key", "Movie One", "http://upstream.test/movie/1001.mkv")]
    [DataRow("series", "series-key", "Test Show — S01E01", "http://upstream.test/series/2001.mkv")]
    public async Task XtreamDirectRelay_AppearsInMonitorWhilePlaying_ThenMovesToRecentlyEnded(
        string route,
        string streamKey,
        string displayName,
        string upstreamUrl)
    {
        var streamId = XtreamStreamIdCache.BuildAssignment([MonitorFactory.MovieKey, MonitorFactory.SeriesKey])
            .KeyToId[streamKey];
        await AssertDirectRelayLifecycleAsync(
            $"/{route}/test-user/secret/{streamId}.mkv", route, streamKey, displayName, upstreamUrl);
    }

    [TestMethod]
    [DataRow("movie", "movie-key", "Movie One", "http://upstream.test/movie/1001.mkv")]
    [DataRow("series", "series-key", "Test Show — S01E01", "http://upstream.test/series/2001.mkv")]
    public async Task CompatibilityDirectRelay_AppearsInMonitorWhilePlaying_ThenMovesToRecentlyEnded(
        string route,
        string streamKey,
        string displayName,
        string upstreamUrl)
    {
        await AssertDirectRelayLifecycleAsync(
            $"/{route}/{streamKey}", route, streamKey, displayName, upstreamUrl);
    }

    private static async Task AssertDirectRelayLifecycleAsync(
        string requestPath,
        string route,
        string streamKey,
        string displayName,
        string upstreamUrl)
    {
        await using var factory = new MonitorFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        await factory.SeedAsync();

        using var requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.UserAgent.ParseAdd("IPTVnator-monitor-test");
        request.Headers.Range = new RangeHeaderValue(128, 255);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"fixture-v1\""));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCancellation.Token);
        Assert.AreEqual("bytes=128-255", factory.UpstreamHandler.LastRange);
        Assert.AreEqual("\"fixture-v1\"", factory.UpstreamHandler.LastIfRange);
        // The entry's own provider must be pinned for upstream identity — not the
        // higher-priority decoy provider also bound to the profile.
        StringAssert.Contains(factory.UpstreamHandler.LastUserAgent, "MonitorProviderUA");
        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual("bytes", string.Join(",", response.Headers.AcceptRanges));
        Assert.AreEqual("bytes 128-255/4096", response.Content.Headers.ContentRange?.ToString());
        Assert.AreEqual(128, response.Content.Headers.ContentLength);
        Assert.AreEqual("\"fixture-v1\"", response.Headers.ETag?.ToString());

        await using var responseStream = await response.Content.ReadAsStreamAsync(requestCancellation.Token);
        var consumeTask = ConsumeAsync(responseStream, requestCancellation.Token);

        var registry = factory.Services.GetRequiredService<StreamingRegistry>();
        await WaitUntilAsync(
            () => registry.GetActiveSessions().Any(x => x.DisplayName == displayName),
            TimeSpan.FromSeconds(5));

        var session = registry.GetActiveSessions().Single(x => x.DisplayName == displayName);
        Assert.AreEqual(MonitorFactory.ProviderId, session.ProviderId, ignoreCase: true);
        Assert.AreEqual($"snapshot:{streamKey}", session.ProviderChannelId, ignoreCase: true);
        Assert.AreEqual("Direct", session.RelayMode);

        var monitorClient = registry.GetActiveClients().Single(x => x.SessionId == session.SessionId);
        Assert.AreEqual(requestPath, monitorClient.RequestedRoute);
        StringAssert.Contains(monitorClient.UserAgent!, "IPTVnator-monitor-test");

        var provider = registry.GetActiveProviderStreams().Single(x => x.SessionId == session.SessionId);
        Assert.AreEqual(MonitorFactory.ProviderId, provider.ProviderId, ignoreCase: true);
        Assert.AreEqual($"snapshot:{streamKey}", provider.ProviderChannelId, ignoreCase: true);

        await WaitUntilAsync(
            () => registry.TryGetSession(session.SessionId)?.TotalBytesRelayed > 0,
            TimeSpan.FromSeconds(5));
        Assert.IsGreaterThan(0L, registry.GetActiveClients().Single(x => x.SessionId == session.SessionId).BytesSent);

        var endpointTitleFragment = route == "series" ? "S01E01" : displayName;
        await AssertMonitorEndpointContainsAsync(client, "/status/streams", endpointTitleFragment, MonitorFactory.ProviderId);
        await AssertMonitorEndpointContainsAsync(client, "/status/streams/clients", endpointTitleFragment, route);
        await AssertMonitorEndpointContainsAsync(client, "/status/streams/providers", MonitorFactory.ProviderId, $"snapshot:{streamKey}");

        requestCancellation.Cancel();
        try
        {
            await consumeTask;
        }
        catch (OperationCanceledException)
        {
        }

        await responseStream.DisposeAsync();
        response.Dispose();

        await WaitUntilAsync(
            () => registry.GetActiveSessions().All(x => x.SessionId != session.SessionId)
                  && registry.GetActiveClients().All(x => x.SessionId != session.SessionId)
                  && registry.GetActiveProviderStreams().All(x => x.SessionId != session.SessionId),
            TimeSpan.FromSeconds(10));

        Assert.IsTrue(registry.GetRecentEndedSessions().Any(x => x.SessionId == session.SessionId));
        Assert.IsTrue(factory.UpstreamHandler.RequestedUrls.Contains(upstreamUrl));
    }

    private static async Task ConsumeAsync(System.IO.Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, cancellationToken) > 0)
        {
        }
    }

    private static async Task AssertMonitorEndpointContainsAsync(
        HttpClient client,
        string path,
        params string[] expectedValues)
    {
        using var response = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        var body = await response.Content.ReadAsStringAsync();
        foreach (var expected in expectedValues)
            StringAssert.Contains(body, expected, StringComparison.OrdinalIgnoreCase, path);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(25);
        }

        Assert.Fail($"Condition was not met within {timeout}.");
    }

    private sealed class MonitorFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        public const string ProviderId = "provider-monitor";
        public const string DecoyProviderId = "provider-decoy";
        public const string MovieKey = "movie-key";
        public const string SeriesKey = "series-key";

        private const string ProfileId = "profile-monitor";
        private const string SnapshotId = "snapshot-monitor";

        private readonly string _tempDataDir = Path.Combine(
            Path.GetTempPath(),
            $"m3undle-vod-series-monitor-{Guid.NewGuid():N}");

        public HoldingUpstreamHandler UpstreamHandler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["M3Undle:Paths:DataDirectory"] = _tempDataDir,
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(x =>
                    x.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (dbDescriptor is not null)
                    services.Remove(dbDescriptor);

                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite(WebApplicationFactoryTestCleanup.CreateSqliteConnectionString(_tempDataDir))
                        .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));

                services.RemoveAll<IAccessResolver>();
                services.AddScoped<IAccessResolver, MonitorAccessResolver>();
                services.RemoveAll<ILineupRenderer>();
                services.AddScoped<ILineupRenderer, MonitorLineupRenderer>();
                services.AddHttpClient("stream-relay")
                    .ConfigurePrimaryHttpMessageHandler(() => UpstreamHandler);
            });
        }

        public async Task SeedAsync()
        {
            var indexPath = Path.Combine(_tempDataDir, "channel_index.ndjson");
            await ChannelIndexStore.WriteAsync(
                indexPath,
                ChannelIndexStore.GetIdxPath(indexPath),
                [
                    Entry(MovieKey, "Movie One", "Movies", "http://upstream.test/movie/1001.mkv"),
                    Entry(SeriesKey, "Test Show — S01E01", "Series", "http://upstream.test/series/2001.mkv"),
                ],
                CancellationToken.None);

            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Profiles.Add(new Profile
            {
                ProfileId = ProfileId,
                Name = "Monitor Profile",
                Enabled = true,
                IsActive = true,
                OutputName = "m3undle",
                MergeMode = "default",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            db.Providers.Add(new Provider
            {
                ProviderId = ProviderId,
                Name = "Monitor Provider",
                Enabled = true,
                PlaylistUrl = "http://upstream.test/playlist.m3u",
                UserAgent = "MonitorProviderUA/1.0",
                TimeoutSeconds = 30,
                IncludeVod = true,
                IncludeSeries = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            // A second, higher-priority provider on the same profile. Direct relays must
            // pin the entry's own provider, so this one's identity must never reach upstream.
            db.Providers.Add(new Provider
            {
                ProviderId = DecoyProviderId,
                Name = "Decoy Provider",
                Enabled = true,
                PlaylistUrl = "http://decoy.test/playlist.m3u",
                UserAgent = "DecoyProviderUA/9.9",
                TimeoutSeconds = 30,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            db.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = ProfileId,
                ProviderId = ProviderId,
                Priority = 1,
                Enabled = true,
            });
            db.ProfileProviders.Add(new ProfileProvider
            {
                ProfileId = ProfileId,
                ProviderId = DecoyProviderId,
                Priority = 0,
                Enabled = true,
            });
            db.Snapshots.Add(new Snapshot
            {
                SnapshotId = SnapshotId,
                ProfileId = ProfileId,
                CreatedUtc = DateTime.UtcNow,
                Status = "active",
                PlaylistPath = Path.Combine(_tempDataDir, "m3undle.m3u"),
                XmltvPath = Path.Combine(_tempDataDir, "guide.xml"),
                ChannelIndexPath = indexPath,
                StatusJsonPath = Path.Combine(_tempDataDir, "status.json"),
                ChannelCountPublished = 2,
                VodChannelCount = 1,
                SeriesChannelCount = 1,
            });
            await db.SaveChangesAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            UpstreamHandler.Dispose();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
        }

        private static ChannelIndexEntry Entry(string key, string name, string group, string url)
            => new(
                StreamKey: key,
                DisplayName: name,
                TvgId: null,
                TvgName: name,
                LogoUrl: null,
                GroupTitle: group,
                TvgChno: null,
                ProviderChannelId: string.Empty,
                StreamUrl: url,
                ProviderId: ProviderId);
    }

    private sealed class MonitorAccessResolver : IAccessResolver
    {
        public ValueTask<ClientAccessResolutionResult> ResolveAsync(
            HttpContext context,
            CancellationToken cancellationToken)
        {
            var credential = new AccessCredential(
                Id: "monitor-credential",
                Username: "test-user",
                PasswordHash: string.Empty,
                Enabled: true,
                AuthType: AccessCredentialAuthType.UsernamePassword);
            return ValueTask.FromResult(ClientAccessResolutionResult.Success(new ResolvedClientAccess(
                Credential: credential,
                Binding: new AccessBinding(
                    CredentialId: credential.Id,
                    ActiveProfileId: "profile-monitor",
                    AllowedProfileIds: ["profile-monitor"],
                    VirtualTunerId: "monitor-tuner"),
                Transport: ClientCredentialTransport.None,
                UrlCredential: new AccessUrlCredential("test-user", "secret"))));
        }
    }

    private sealed class MonitorLineupRenderer : ILineupRenderer
    {
        public Task<RenderedLineup?> TryRenderActiveLineupAsync(
            string? profileId,
            CancellationToken cancellationToken)
            => Task.FromResult<RenderedLineup?>(new RenderedLineup(
                SnapshotId: "snapshot-monitor",
                ProfileId: "profile-monitor",
                SnapshotCreatedUtc: DateTime.UtcNow,
                ChannelIndexPath: "unused",
                XmltvPath: null,
                Channels:
                [
                    new RenderedLineupChannel(
                        MonitorFactory.MovieKey,
                        "Movie One",
                        null,
                        "Movie One",
                        null,
                        "Movies",
                        null,
                        "http://upstream.test/movie/1001.mkv",
                        "vod"),
                    new RenderedLineupChannel(
                        MonitorFactory.SeriesKey,
                        "Test Show — S01E01",
                        null,
                        "Test Show — S01E01",
                        null,
                        "Series",
                        null,
                        "http://upstream.test/series/2001.mkv",
                        "series"),
                ]));
    }

    public sealed class HoldingUpstreamHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _requestedUrls = [];
        private string? _lastRange;
        private string? _lastIfRange;
        private string? _lastUserAgent;

        public IReadOnlyList<string> RequestedUrls
        {
            get
            {
                lock (_gate)
                    return _requestedUrls.ToArray();
            }
        }

        public string? LastRange
        {
            get
            {
                lock (_gate)
                    return _lastRange;
            }
        }

        public string? LastIfRange
        {
            get
            {
                lock (_gate)
                    return _lastIfRange;
            }
        }

        public string? LastUserAgent
        {
            get
            {
                lock (_gate)
                    return _lastUserAgent;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _requestedUrls.Add(request.RequestUri!.ToString());
                _lastRange = request.Headers.Range?.ToString();
                _lastIfRange = request.Headers.IfRange?.ToString();
                _lastUserAgent = request.Headers.UserAgent.ToString();
            }

            var content = new StreamContent(new HoldingReadStream());
            content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
            content.Headers.ContentRange = new ContentRangeHeaderValue(128, 255, 4096);
            content.Headers.ContentLength = 128;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = content,
                RequestMessage = request,
            };
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return Task.FromResult(response);
        }
    }

    private sealed class HoldingReadStream : System.IO.Stream
    {
        private static readonly byte[] Payload = Enumerable.Range(0, 4096)
            .Select(x => (byte)(x % 251))
            .ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken);
            var count = Math.Min(buffer.Length, Payload.Length);
            Payload.AsMemory(0, count).CopyTo(buffer);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
