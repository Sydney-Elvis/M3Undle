using IOStream = System.IO.Stream;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Buffering;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Sessions;
using M3Undle.Web.Streaming.Subscribers;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class ChannelSessionIntegrationTests
{
    [TestMethod]
    public async Task Session_AttachSubscriber_ReceivesDataAndIdleShutdownFires()
    {
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            proxyOptions: new StreamProxyOptions { StreamingEnabled = true, IdleGrace = TimeSpan.FromMilliseconds(300) });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var requestCts = new CancellationTokenSource();
        var subscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), requestCts.Token);

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));
        Assert.IsGreaterThan(0L, subscriber.BytesSent);

        requestCts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => !fixture.Manager.TryGet(session.Key, out _), TimeSpan.FromSeconds(5));
        Assert.IsFalse(fixture.Manager.TryGet(session.Key, out _));
    }

    [TestMethod]
    public async Task Session_SlowSubscriber_IsEvictedWithoutAffectingOthers()
    {
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(handler);

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);

        var cts1 = new CancellationTokenSource();
        var normalSubscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), cts1.Token);
        var slowSubscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None);

        Assert.AreEqual(2, session.SubscriberCount);

        // Directly evict the slow subscriber (simulates the slow-client queue-full path)
        await slowSubscriber.CompleteAsync(SubscriberDisconnectReason.SlowClient);
        await WaitUntilAsync(() => session.SubscriberCount == 1, TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, session.SubscriberCount);

        // Normal subscriber keeps receiving data after the eviction
        await WaitUntilAsync(() => normalSubscriber.BytesSent > 0, TimeSpan.FromSeconds(5));
        Assert.IsGreaterThan(0L, normalSubscriber.BytesSent);

        // Only one upstream connection was made (shared session)
        Assert.AreEqual(1, handler.ConnectionCount);

        cts1.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_UpstreamStall_TriggersReconnect()
    {
        var chunk = new byte[188];
        var handler = FakeStreamingHandler.StreamForever(chunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 3, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var cts = new CancellationTokenSource();
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () =>
            {
                var snap = fixture.Registry.TryGetSession(session.SessionId);
                return snap is { State: SessionState.Live, ReconnectAttempts: > 0 };
            },
            TimeSpan.FromSeconds(5));

        var snapshot = fixture.Registry.TryGetSession(session.SessionId);
        Assert.IsNotNull(snapshot);
        Assert.IsGreaterThanOrEqualTo(1, snapshot.ReconnectAttempts);
        Assert.AreEqual(SessionState.Live, snapshot.State);

        cts.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_AuthFailure_FaultsSession()
    {
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.Unauthorized);
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(30),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        await AssertThrowsAsync<UpstreamConnectException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        await WaitUntilAsync(() => session.State == SessionState.Faulted, TimeSpan.FromSeconds(5));
        Assert.AreEqual(SessionState.Faulted, session.State);
    }

    [TestMethod]
    public async Task Session_OutageWindowExhausted_RecordsStrike()
    {
        // First connect streams 3 chunks then stalls (headers ready → subscriber can attach).
        // Subsequent reconnects return 503 so outageStartedUtc is never reset.
        var chunk = new byte[188];
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.ServiceUnavailable);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 3, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(100),
                OutageWindow = TimeSpan.FromMilliseconds(500),
                StrikeCooldown = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var subscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None);

        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(SessionState.Faulted, session.State);
        Assert.IsTrue(fixture.StrikeStore.IsCoolingDown(fixture.Source.SessionKey, out _));
    }

    [TestMethod]
    public async Task Session_MultipleSubscribers_ShareSingleUpstreamConnection()
    {
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(handler);

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();

        var sub1 = await session.AttachSubscriberAsync(new DefaultHttpContext(), cts1.Token);
        var sub2 = await session.AttachSubscriberAsync(new DefaultHttpContext(), cts2.Token);

        await WaitUntilAsync(() => sub1.BytesSent > 0 && sub2.BytesSent > 0, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, handler.ConnectionCount);
        Assert.IsGreaterThan(0L, sub1.BytesSent);
        Assert.IsGreaterThan(0L, sub2.BytesSent);
        Assert.AreEqual(2, session.SubscriberCount);

        cts1.Cancel();
        cts2.Cancel();
        await session.DisposeAsync();
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); Assert.Fail($"Expected {typeof(TException).Name} to be thrown."); }
        catch (TException) { /* Expected */ }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
    }

    // ---------------------------------------------------------------------------
    // Fake streaming handler
    // ---------------------------------------------------------------------------

    private sealed class FakeStreamingHandler : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> _behaviors = new();
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _defaultBehavior;
        private int _connectionCount;

        private FakeStreamingHandler(Func<CancellationToken, Task<HttpResponseMessage>> defaultBehavior)
            => _defaultBehavior = defaultBehavior;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public void QueueNext(Func<CancellationToken, Task<HttpResponseMessage>> behavior)
            => _behaviors.Enqueue(behavior);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _connectionCount);
            return _behaviors.TryDequeue(out var next) ? next(ct) : _defaultBehavior(ct);
        }

        public static FakeStreamingHandler StreamForever(byte[]? chunk = null)
        {
            var data = chunk ?? new byte[188];
            if (chunk is null) Array.Fill(data, (byte)0xAA);
            return new FakeStreamingHandler(ct => StreamForeverResponse(data, ct));
        }

        public static FakeStreamingHandler ReturnStatus(HttpStatusCode statusCode)
            => new FakeStreamingHandler(_ => Task.FromResult(new HttpResponseMessage(statusCode)));

        public static Task<HttpResponseMessage> StreamForeverResponse(byte[] chunk, CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var result = await pipe.Writer.WriteAsync(chunk, ct);
                        if (result.IsCompleted) break;
                        await Task.Delay(5, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                finally { pipe.Writer.Complete(); }
            });
            return Task.FromResult(CreateStreamingResponse(pipe.Reader.AsStream()));
        }

        public static Task<HttpResponseMessage> WriteNChunksThenStall(byte[] chunk, int n, CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        var result = await pipe.Writer.WriteAsync(chunk, ct);
                        if (result.IsCompleted) return;
                    }
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                finally { pipe.Writer.Complete(); }
            });
            return Task.FromResult(CreateStreamingResponse(pipe.Reader.AsStream()));
        }

        private static HttpResponseMessage CreateStreamingResponse(IOStream body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StreamContent(body);
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("video/MP2T");
            return response;
        }
    }

    // ---------------------------------------------------------------------------
    // Test fixture — wires up the full in-process stack
    // ---------------------------------------------------------------------------

    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        public FakeStreamingHandler Handler { get; }
        public UpstreamFailureStrikeStore StrikeStore { get; }
        public StreamingRegistry Registry { get; }
        public ChannelSessionManager Manager { get; }
        public StreamSourceDescriptor Source { get; }

        private SessionFixture(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            FakeStreamingHandler handler,
            UpstreamFailureStrikeStore strikeStore,
            StreamingRegistry registry,
            ChannelSessionManager manager,
            StreamSourceDescriptor source)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
            Handler = handler;
            StrikeStore = strikeStore;
            Registry = registry;
            Manager = manager;
            Source = source;
        }

        public static async Task<SessionFixture> CreateAsync(
            FakeStreamingHandler handler,
            BufferOptions? bufferOptions = null,
            StreamProxyOptions? proxyOptions = null,
            ReconnectOptions? reconnectOptions = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(opt => opt.UseSqlite(connection));
            var serviceProvider = services.BuildServiceProvider();

            var db = serviceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();

            var provider = new Provider
            {
                ProviderId = "provider-1",
                Name = "Test Provider",
                Enabled = true,
                IsActive = true,
                PlaylistUrl = "http://fake/playlist.m3u",
                TimeoutSeconds = 30,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            var fetchRun = new FetchRun
            {
                FetchRunId = "run-1",
                ProviderId = "provider-1",
                StartedUtc = DateTime.UtcNow,
                Status = "ok",
                Type = "snapshot",
            };
            var channel = new ProviderChannel
            {
                ProviderChannelId = "channel-1",
                ProviderId = "provider-1",
                DisplayName = "Test Channel",
                StreamUrl = "http://fake/stream",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                Active = true,
                ContentType = "live",
                LastFetchRunId = "run-1",
            };

            db.Providers.Add(provider);
            db.FetchRuns.Add(fetchRun);
            db.ProviderChannels.Add(channel);
            await db.SaveChangesAsync();
            await db.DisposeAsync();

            var bufOpts = Options.Create(bufferOptions ?? new BufferOptions
            {
                ReadChunkSizeBytes = 188,
                SubscriberQueueCapacity = 128,
                MaxBytesPerSession = 64 * 1024,
                MaxBytesHardCap = 4 * 1024 * 1024,
            });
            var proxyOpts = Options.Create(proxyOptions ?? new StreamProxyOptions
            {
                StreamingEnabled = true,
                IdleGrace = TimeSpan.FromMilliseconds(200),
            });
            var reconnectOpts = Options.Create(reconnectOptions ?? new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                OutageWindow = TimeSpan.FromSeconds(60),
                FixedStepBackoffSeconds = [0],
            });

            var httpClientFactory = new FakeHttpClientFactory(handler);
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var connector = new UpstreamStreamConnector(
                httpClientFactory, scopeFactory, reconnectOpts,
                NullLogger<UpstreamStreamConnector>.Instance);
            var strikeStore = new UpstreamFailureStrikeStore();
            var registry = new StreamingRegistry(proxyOpts);
            var manager = new ChannelSessionManager(
                bufOpts, proxyOpts, reconnectOpts, connector, strikeStore, registry,
                NullLoggerFactory.Instance);

            var source = new StreamSourceDescriptor(
                ProfileId: "profile-1",
                ProviderId: "provider-1",
                ProviderChannelId: "channel-1",
                StreamUrl: "http://fake/stream",
                DisplayName: "Test Channel",
                RequestedRoute: "/live/key-1",
                UserAgent: null,
                RemoteIp: null);

            return new SessionFixture(connection, serviceProvider, handler, strikeStore, registry, manager, source);
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
