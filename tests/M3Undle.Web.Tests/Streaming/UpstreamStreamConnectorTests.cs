using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class UpstreamStreamConnectorTests
{
    [TestMethod]
    public async Task ConnectAsync_WhenUpstreamReturns407_ClassifiesAsProxyAuthRequired()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.ts",
            ffmpegPath: "",
            handler: new RecordingHttpMessageHandler(_ => CreateStatusResponse(HttpStatusCode.ProxyAuthenticationRequired)));

        var ex = await AssertThrowsAsync<UpstreamConnectException>(
            () => fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None));

        Assert.AreEqual(UpstreamFailureKind.UpstreamProxyAuthRequired, ex.FailureKind);
        Assert.AreEqual(407, ex.StatusCode);
        Assert.IsNull(ex.RetryAfter);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenUpstreamReturns429_ParsesRetryAfterSeconds()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.ts",
            ffmpegPath: "",
            handler: new RecordingHttpMessageHandler(_ =>
            {
                var response = CreateStatusResponse((HttpStatusCode)429);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
                return response;
            }));

        var ex = await AssertThrowsAsync<UpstreamConnectException>(
            () => fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None));

        Assert.AreEqual(UpstreamFailureKind.UpstreamRateLimited, ex.FailureKind);
        Assert.AreEqual(429, ex.StatusCode);
        Assert.IsNotNull(ex.RetryAfter);
        Assert.AreEqual(17, (int)Math.Round(ex.RetryAfter.Value.TotalSeconds));
    }

    [TestMethod]
    public async Task ConnectAsync_WhenUpstreamReturns429WithHttpDate_ParsesRetryAfterDate()
    {
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(12);
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.ts",
            ffmpegPath: "",
            handler: new RecordingHttpMessageHandler(_ =>
            {
                var response = CreateStatusResponse((HttpStatusCode)429);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
                return response;
            }));

        var ex = await AssertThrowsAsync<UpstreamConnectException>(
            () => fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None));

        Assert.AreEqual(UpstreamFailureKind.UpstreamRateLimited, ex.FailureKind);
        Assert.IsNotNull(ex.RetryAfter);
        Assert.IsGreaterThan(0, ex.RetryAfter.Value.TotalSeconds);
        Assert.IsLessThanOrEqualTo(12, ex.RetryAfter.Value.TotalSeconds);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenUpstreamReturns401_RemainsFatalAuth()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.ts",
            ffmpegPath: "",
            handler: new RecordingHttpMessageHandler(_ => CreateStatusResponse(HttpStatusCode.Unauthorized)));

        var ex = await AssertThrowsAsync<UpstreamConnectException>(
            () => fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None));

        Assert.AreEqual(UpstreamFailureKind.UpstreamAuth, ex.FailureKind);
        Assert.AreEqual(401, ex.StatusCode);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenFfmpegProducesStartupBytes_ReturnsPrefixedRelayStream()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.ts?ffmpegMode=relay-success&prefix=HEAD&suffix=TAIL&delayMs=1200",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => throw new AssertFailedException("Direct HTTP fallback should not be used when FFmpeg relay starts.")));

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

        Assert.AreEqual("video/mp2t", connection.ContentType);
        Assert.AreEqual(0, fixture.Handler.RequestCount);

        var firstBytes = await ReadExactAsciiAsync(connection.Stream, 4, TimeSpan.FromSeconds(2));
        var secondBytes = await ReadExactAsciiAsync(connection.Stream, 4, TimeSpan.FromSeconds(4));

        Assert.AreEqual("HEAD", firstBytes);
        Assert.AreEqual("TAIL", secondBytes);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenNonForcedSourceIsExplicitHls_UsesFfmpegHlsRelay()
    {
        var argsFile = Path.GetTempFileName();
        try
        {
            using var fixture = await ConnectorFixture.CreateAsync(
                streamUrl: $"http://provider.test/channel.m3u8?ffmpegMode=relay-success&prefix=HEAD&suffix=TAIL&delayMs=200&argsOut={Uri.EscapeDataString(argsFile)}",
                ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
                handler: new RecordingHttpMessageHandler(_ => throw new AssertFailedException("Direct HTTP fallback should not be used for explicit HLS relay.")),
                forceMpegTs: false);

            await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

            Assert.AreEqual(UpstreamRelayModes.FfmpegHlsToMpegTs, connection.RelayMode);
            Assert.AreEqual(0, fixture.Handler.RequestCount);

            var args = (await File.ReadAllLinesAsync(argsFile)).ToList();
            Assert.IsTrue(args.Contains("-re"));
            Assert.IsTrue(args.Contains("-allowed_extensions"));
            Assert.IsTrue(args.Contains("ALL"));
            Assert.IsFalse(args.Contains("-reconnect_streamed"));
            Assert.IsFalse(
                args.Contains("-reconnect_at_eof"),
                "-reconnect_at_eof breaks HLS: every playlist/segment fetch ends in EOF and FFmpeg never pulls segments.");
            // HLS relay must NOT pass -map: FFmpeg's default selection picks the best variant
            // without pulling in all quality levels as separate video streams.
            Assert.IsFalse(args.Contains("-map"), "HLS relay must not use -map (causes multi-video MPEG-TS)");

            var firstBytes = await ReadExactAsciiAsync(connection.Stream, 4, TimeSpan.FromSeconds(2));
            Assert.AreEqual("HEAD", firstBytes);
        }
        finally
        {
            File.Delete(argsFile);
        }
    }

    [TestMethod]
    public async Task ConnectAsync_WhenNonForcedSourceIsXtreamNumericUrl_UsesDirectHttp()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/user/pass/123?ffmpegMode=relay-success&prefix=HEAD",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => CreateHttpResponse("DIRECT")),
            forceMpegTs: false);

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

        Assert.AreEqual(UpstreamRelayModes.Direct, connection.RelayMode);
        Assert.AreEqual(1, fixture.Handler.RequestCount);
        Assert.AreEqual("http://provider.test/user/pass/123?ffmpegMode=relay-success&prefix=HEAD", fixture.Handler.LastRequestUri?.ToString());

        var body = await ReadExactAsciiAsync(connection.Stream, 6, TimeSpan.FromSeconds(2));
        Assert.AreEqual("DIRECT", body);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenFfmpegStallsWithoutOutput_FallsBackToDirectHttp()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.ts?ffmpegMode=relay-stall",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => CreateHttpResponse("DIRECT")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, cts.Token);

        Assert.AreEqual(1, fixture.Handler.RequestCount);
        Assert.AreEqual("http://provider.test/channel.ts?ffmpegMode=relay-stall", fixture.Handler.LastRequestUri?.ToString());

        var body = await ReadExactAsciiAsync(connection.Stream, 6, TimeSpan.FromSeconds(2));
        Assert.AreEqual("DIRECT", body);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenFfmpegExitsWithoutOutput_FallsBackToDirectHttp()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.ts?ffmpegMode=relay-eof",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => CreateHttpResponse("DIRECT")));

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

        Assert.AreEqual(1, fixture.Handler.RequestCount);
        Assert.AreEqual("http://provider.test/channel.ts?ffmpegMode=relay-eof", fixture.Handler.LastRequestUri?.ToString());

        var body = await ReadExactAsciiAsync(connection.Stream, 6, TimeSpan.FromSeconds(2));
        Assert.AreEqual("DIRECT", body);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenCleanRelayOff_UsesDirectHttp()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.mpeg?ffmpegMode=relay-success",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => CreateHttpResponse("DIRECT")),
            cleanRelayMode: "off",
            forceMpegTs: false);

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

        Assert.AreEqual(UpstreamRelayModes.Direct, connection.RelayMode);
        Assert.AreEqual(1, fixture.Handler.RequestCount);

        var body = await ReadExactAsciiAsync(connection.Stream, 6, TimeSpan.FromSeconds(2));
        Assert.AreEqual("DIRECT", body);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenCleanRelayOn_ForDirectMpegTs_ReturnsFfmpegRemuxStream()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.mpeg?ffmpegMode=relay-success&prefix=HEAD&suffix=TAIL&delayMs=200",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => throw new AssertFailedException("Direct HTTP fallback should not be used when clean relay starts.")),
            cleanRelayMode: "remux",
            forceMpegTs: false);

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

        Assert.AreEqual("video/mp2t", connection.ContentType);
        Assert.AreEqual(UpstreamRelayModes.FfmpegCleanRemux, connection.RelayMode);
        Assert.AreEqual(0, fixture.Handler.RequestCount);

        var firstBytes = await ReadExactAsciiAsync(connection.Stream, 4, TimeSpan.FromSeconds(2));
        var secondBytes = await ReadExactAsciiAsync(connection.Stream, 4, TimeSpan.FromSeconds(2));

        Assert.AreEqual("HEAD", firstBytes);
        Assert.AreEqual("TAIL", secondBytes);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenCleanRelayStalls_FallsBackToDirectHttp()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.mpeg?ffmpegMode=relay-stall",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => CreateHttpResponse("DIRECT")),
            cleanRelayMode: "remux",
            cleanRelayOptions: new CleanRelayOptions { StartupTimeoutSeconds = 1, FallbackToDirect = true },
            forceMpegTs: false);

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

        Assert.AreEqual(UpstreamRelayModes.Direct, connection.RelayMode);
        Assert.AreEqual("clean_relay_startup_failed", connection.RelayFallbackReason);
        Assert.AreEqual(1, fixture.Handler.RequestCount);

        var body = await ReadExactAsciiAsync(connection.Stream, 6, TimeSpan.FromSeconds(2));
        Assert.AreEqual("DIRECT", body);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenCleanRelayStallsAndFallbackDisabled_Throws()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.mpeg?ffmpegMode=relay-stall",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => CreateHttpResponse("DIRECT")),
            cleanRelayMode: "remux",
            cleanRelayOptions: new CleanRelayOptions { StartupTimeoutSeconds = 1, FallbackToDirect = false },
            forceMpegTs: false);

        var ex = await AssertThrowsAsync<UpstreamConnectException>(
            () => fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None));

        Assert.AreEqual(UpstreamFailureKind.Transport, ex.FailureKind);
        Assert.AreEqual(0, fixture.Handler.RequestCount);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenCleanRelayExitsWithNoOutput_FallsBackToDirectHttp()
    {
        using var fixture = await ConnectorFixture.CreateAsync(
            streamUrl: "http://provider.test/channel.mpeg?ffmpegMode=relay-eof",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            handler: new RecordingHttpMessageHandler(_ => CreateHttpResponse("DIRECT")),
            cleanRelayMode: "remux",
            forceMpegTs: false);

        await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

        Assert.AreEqual(UpstreamRelayModes.Direct, connection.RelayMode);
        Assert.AreEqual("clean_relay_startup_failed", connection.RelayFallbackReason);
        Assert.AreEqual(1, fixture.Handler.RequestCount);

        var body = await ReadExactAsciiAsync(connection.Stream, 6, TimeSpan.FromSeconds(2));
        Assert.AreEqual("DIRECT", body);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenCleanRelayOn_PassesUserAgentAndHeadersToFfmpeg()
    {
        var argsFile = Path.GetTempFileName();
        try
        {
            // FakeFfmpeg writes all args to argsFile before producing stdout output,
            // so the file is guaranteed to exist once ConnectAsync returns.
            using var fixture = await ConnectorFixture.CreateAsync(
                streamUrl: $"http://provider.test/channel.mpeg?ffmpegMode=relay-success&prefix=HEAD&suffix=TAIL&delayMs=200&argsOut={Uri.EscapeDataString(argsFile)}",
                ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
                handler: new RecordingHttpMessageHandler(_ => throw new AssertFailedException("Direct HTTP must not be called when clean relay starts.")),
                cleanRelayMode: "remux",
                userAgent: "TestAgent/2.0",
                headersJson: """{"X-Auth-Token": "abc123"}""",
                forceMpegTs: false);

            await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

            Assert.AreEqual(UpstreamRelayModes.FfmpegCleanRemux, connection.RelayMode);

            var argsText = await File.ReadAllTextAsync(argsFile);
            StringAssert.Contains(argsText, "-user_agent");
            StringAssert.Contains(argsText, "TestAgent/2.0");
            StringAssert.Contains(argsText, "-headers");
            StringAssert.Contains(argsText, "X-Auth-Token: abc123");
            StringAssert.Contains(argsText, "-reconnect_streamed");
            StringAssert.Contains(argsText, "-reconnect_on_network_error");
            StringAssert.Contains(argsText, "-reconnect_delay_max");
            StringAssert.Contains(argsText, "30");
            StringAssert.Contains(argsText, "-avoid_negative_ts");
            StringAssert.Contains(argsText, "make_zero");
            Assert.IsFalse(
                argsText.Contains("-reconnect_at_eof", StringComparison.Ordinal),
                "FFmpeg must not reconnect across an EOF on its own: a provider that restarts the source " +
                "from byte zero on reconnect would replay content inside FFmpeg's single continuous muxer " +
                "session, where MPEG-TS's monotonic-DTS requirement makes the replay undetectable downstream. " +
                "Letting FFmpeg exit on EOF hands the reconnect to M3Undle's own outer reconnect path instead, " +
                "where the existing overlap-trim recovery can see and trim it.");
            Assert.IsFalse(
                argsText.Contains("-use_wallclock_as_timestamps", StringComparison.Ordinal),
                "Clean remux must not stamp live packets with wall-clock time; downstream HLS remuxers can preserve that as a large timeline jump.");
            Assert.IsFalse(
                argsText.Contains("-reconnect_on_http_error", StringComparison.Ordinal),
                "FFmpeg must not hide HTTP status failures from M3Undle's auth, rate-limit, Retry-After, and cooldown classifier.");
        }
        finally
        {
            File.Delete(argsFile);
        }
    }

    [TestMethod]
    public async Task ConnectAsync_WhenCleanRelayInputIsExplicitHls_DoesNotUseContinuousReconnectFlags()
    {
        var argsFile = Path.GetTempFileName();
        try
        {
            using var fixture = await ConnectorFixture.CreateAsync(
                streamUrl: $"http://provider.test/channel.m3u8?ffmpegMode=relay-success&prefix=HEAD&suffix=TAIL&delayMs=200&argsOut={Uri.EscapeDataString(argsFile)}",
                ffmpegPath: "",
                handler: new RecordingHttpMessageHandler(_ => throw new AssertFailedException("Direct HTTP must not be called when clean relay starts.")),
                cleanRelayMode: "remux",
                cleanRelayOptions: new CleanRelayOptions { FfmpegPath = FakeFfmpegBinary.LocateExecutable() },
                forceMpegTs: false);

            await using var connection = await fixture.Connector.ConnectAsync(fixture.Source, CancellationToken.None);

            Assert.AreEqual(UpstreamRelayModes.FfmpegCleanRemux, connection.RelayMode);

            var argsText = await File.ReadAllTextAsync(argsFile);
            Assert.IsFalse(argsText.Contains("-reconnect_streamed", StringComparison.Ordinal));
            Assert.IsFalse(argsText.Contains("-reconnect_at_eof", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(argsFile);
        }
    }

    private static HttpResponseMessage CreateHttpResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(body)),
        };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("video/MP2T");
        return response;
    }

    private static HttpResponseMessage CreateStatusResponse(HttpStatusCode statusCode)
        => new(statusCode);

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
            throw new InvalidOperationException("Assert.Fail should have thrown.");
        }
        catch (TException ex)
        {
            return ex;
        }
    }

    private static async Task<string> ReadExactAsciiAsync(System.IO.Stream stream, int length, TimeSpan timeout)
    {
        var buffer = new byte[length];
        var offset = 0;
        using var cts = new CancellationTokenSource(timeout);

        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cts.Token);
            if (read == 0)
                Assert.Fail($"Expected {length} bytes but reached EOF after {offset} bytes.");

            offset += read;
        }

        return System.Text.Encoding.ASCII.GetString(buffer);
    }

    private sealed class ConnectorFixture : IDisposable
    {
        private ConnectorFixture(SqliteConnection connection, ServiceProvider services, RecordingHttpMessageHandler handler, UpstreamStreamConnector connector, StreamSourceDescriptor source)
        {
            _connection = connection;
            _services = services;
            Handler = handler;
            Connector = connector;
            Source = source;
        }

        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        public RecordingHttpMessageHandler Handler { get; }

        public UpstreamStreamConnector Connector { get; }

        public StreamSourceDescriptor Source { get; }

        public static async Task<ConnectorFixture> CreateAsync(
            string streamUrl,
            string ffmpegPath,
            RecordingHttpMessageHandler handler,
            string cleanRelayMode = "off",
            CleanRelayOptions? cleanRelayOptions = null,
            string? userAgent = null,
            string? headersJson = null,
            bool forceMpegTs = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
            var serviceProvider = services.BuildServiceProvider();

            await using (var db = serviceProvider.GetRequiredService<ApplicationDbContext>())
            {
                await db.Database.EnsureCreatedAsync();
                db.Providers.Add(new Provider
                {
                    ProviderId = "provider-1",
                    Name = "Test Provider",
                    Enabled = true,
                    PlaylistUrl = "http://provider.test/playlist.m3u",
                    TimeoutSeconds = 30,
                    CleanRelayMode = cleanRelayMode,
                    UserAgent = userAgent,
                    HeadersJson = headersJson,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });
                db.FetchRuns.Add(new FetchRun
                {
                    FetchRunId = "run-1",
                    ProviderId = "provider-1",
                    StartedUtc = DateTime.UtcNow,
                    Status = "ok",
                    Type = "snapshot",
                });
                db.ProviderChannels.Add(new ProviderChannel
                {
                    ProviderChannelId = "channel-1",
                    ProviderId = "provider-1",
                    DisplayName = "Test Channel",
                    StreamUrl = streamUrl,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    Active = true,
                    ContentType = "live",
                    LastFetchRunId = "run-1",
                });
                await db.SaveChangesAsync();
            }

            var connector = new UpstreamStreamConnector(
                new FakeHttpClientFactory(handler),
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                NoopStreamChannelHealthProfileService.Instance,
                Options.Create(new ReconnectOptions
                {
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ReadStallTimeout = TimeSpan.FromSeconds(30),
                    OutageWindow = TimeSpan.FromSeconds(60),
                    FixedStepBackoffSeconds = [0],
                }),
                Options.Create(new GeneratedHlsOptions
                {
                    FfmpegPath = ffmpegPath,
                }),
                Options.Create(cleanRelayOptions ?? new CleanRelayOptions()),
                NullLogger<UpstreamStreamConnector>.Instance);

            var source = new StreamSourceDescriptor(
                ProfileId: "profile-1",
                ProviderId: "provider-1",
                ProviderChannelId: "channel-1",
                StreamUrl: streamUrl,
                DisplayName: "Test Channel",
                RequestedRoute: "/live/key-1",
                UserAgent: null,
                RemoteIp: null,
                ForceMpegTs: forceMpegTs);

            return new ConnectorFixture(connection, serviceProvider, handler, connector, source);
        }

        public void Dispose()
        {
            _services.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequestUri = request.RequestUri;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static class FakeFfmpegBinary
    {
        public static string LocateExecutable()
        {
            var exeName = OperatingSystem.IsWindows()
                ? "M3Undle.FakeFfmpeg.exe"
                : "M3Undle.FakeFfmpeg";

            var tfmDir = new DirectoryInfo(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var configDir = tfmDir.Parent!;
            var testsDir = configDir.Parent!.Parent!.Parent!;

            var path = Path.Combine(
                testsDir.FullName,
                "M3Undle.FakeFfmpeg",
                "bin",
                configDir.Name,
                tfmDir.Name,
                exeName);

            if (!File.Exists(path))
                throw new FileNotFoundException($"FakeFfmpeg executable not found at '{path}'.");

            return path;
        }
    }
}
