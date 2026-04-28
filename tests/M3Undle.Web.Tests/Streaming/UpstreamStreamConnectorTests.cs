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

        public static async Task<ConnectorFixture> CreateAsync(string streamUrl, string ffmpegPath, RecordingHttpMessageHandler handler)
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
                ForceMpegTs: true);

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
