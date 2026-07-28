using IOStream = System.IO.Stream;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Buffering;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Sessions;
using M3Undle.Web.Streaming.Subscribers;
using M3Undle.Web.Streaming.Upstream;
using M3Undle.Web.Tests.Stubs;
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
    public async Task Session_SubscriberQueueFull_RecordsSlowClientDiagnostic()
    {
        var handler = FakeStreamingHandler.StreamForever(FakeStreamingHandler.ValidTsPacket());
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            bufferOptions: new BufferOptions
            {
                ReadChunkSizeBytes = 188,
                SubscriberQueueCapacity = 1,
                MaxBytesPerSession = 64 * 1024,
                MaxBytesHardCap = 4 * 1024 * 1024,
                // Short grace so the permanently-blocked consumer is evicted within the test
                // window; the production default tolerates transient pauses for much longer.
                SlowClientGracePeriod = TimeSpan.FromMilliseconds(50),
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var context = new DefaultHttpContext();
        await using var blockingBody = new BlockingWriteStream();
        context.Response.Body = blockingBody;

        var subscriber = await session.AttachSubscriberAsync(context, CancellationToken.None);

        // The blocked consumer keeps the queue full continuously, so once the grace window
        // elapses the overflow is reported with a SlowClient disconnect reason and the
        // subscriber is evicted.
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.SubscriberQueueFull)
                .Any(x => x.DisconnectReason == SubscriberDisconnectReason.SlowClient),
            TimeSpan.FromSeconds(5));

        var queueFull = fixture.DiagnosticsStore
            .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.SubscriberQueueFull)
            .First(x => x.DisconnectReason == SubscriberDisconnectReason.SlowClient);
        Assert.AreEqual(subscriber.ClientId, queueFull.ClientId);
        Assert.AreEqual(SubscriberDisconnectReason.SlowClient, queueFull.DisconnectReason);
        Assert.AreEqual(1, queueFull.QueueDepth);
        Assert.IsGreaterThan(0L, queueFull.TotalBytesRelayed.GetValueOrDefault());

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore
                .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.SubscriberRemoved)
                .Any(x => x.ClientId == subscriber.ClientId && x.DisconnectReason == SubscriberDisconnectReason.SlowClient),
            TimeSpan.FromSeconds(2));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_UpstreamStall_TriggersReconnect()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
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
                RecoverySafeStartSearchLimitBytes = 188, // one-packet limit so fallback triggers immediately
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
    public async Task Session_Reconnecting_NewMatchingSubscriberSupersedesOlderRequest()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.StreamForever(chunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 3, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [1],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var firstContext = CreateClientContext("192.168.1.190", "SparkleTV/2.3.1", "/live/user/pass/123.ts");
        var first = await session.AttachSubscriberAsync(firstContext, CancellationToken.None);

        await WaitUntilAsync(() => session.State == SessionState.Reconnecting, TimeSpan.FromSeconds(5));
        Assert.AreEqual(SessionState.Reconnecting, session.State);

        var replacementContext = CreateClientContext("192.168.1.190", "SparkleTV/2.3.1", "/live/user/pass/123.ts");
        var replacement = await session.AttachSubscriberAsync(replacementContext, CancellationToken.None);

        await WaitUntilAsync(() => first.IsCompleted, TimeSpan.FromSeconds(2));
        Assert.IsTrue(first.IsCompleted);
        Assert.IsFalse(replacement.IsCompleted);
        Assert.AreEqual(1, session.ExternalSubscriberCount);

        var removed = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.SubscriberRemoved);
        Assert.IsTrue(removed.Any(x =>
            x.ClientId == first.ClientId
            && x.DisconnectReason == SubscriberDisconnectReason.Superseded));

        await WaitUntilAsync(() => session.State == SessionState.Live, TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_Reconnecting_DifferentUserAgentDoesNotSupersedeExistingSubscriber()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.StreamForever(chunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 3, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [1],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var first = await session.AttachSubscriberAsync(
            CreateClientContext("192.168.1.190", "SparkleTV/2.3.1", "/live/user/pass/123.ts"),
            CancellationToken.None);

        await WaitUntilAsync(() => session.State == SessionState.Reconnecting, TimeSpan.FromSeconds(5));
        Assert.AreEqual(SessionState.Reconnecting, session.State);

        var second = await session.AttachSubscriberAsync(
            CreateClientContext("192.168.1.190", "OtherPlayer/1.0", "/live/user/pass/123.ts"),
            CancellationToken.None);

        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(2, session.ExternalSubscriberCount);

        await WaitUntilAsync(() => session.State == SessionState.Live, TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_UpstreamStall_EmitsFailureAndReconnectDiagnostics()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
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
        using var cts = new CancellationTokenSource();
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectRecovered).Count > 0,
            TimeSpan.FromSeconds(5));

        var failures = fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.UpstreamFailure);
        var reconnects = fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled);

        Assert.IsTrue(failures.Any(x => x.UpstreamFailureKind == UpstreamFailureKind.TimeoutOrStall));
        Assert.IsTrue(reconnects.Any(x => x.ReconnectAttempt >= 1));

        cts.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_Reconnect_ResetsBytesSinceReconnect()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.StreamForever(chunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 5, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
                // One-packet search limit so the fallback triggers immediately — avoids
                // hold-limit expiry races under slow CI timers (Task.Delay(5) ≈ 15ms on
                // Windows, 174 packets × 15ms ≈ 2.6 s is close to the default 3 s limit).
                RecoverySafeStartSearchLimitBytes = 188,
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        // Wait until reconnect completes and bytes are flowing on the second connection.
        await WaitUntilAsync(
            () =>
            {
                var snap = fixture.Registry.TryGetSession(session.SessionId);
                return snap is { ReconnectAttempts: > 0, BytesSinceReconnect: > 0 }
                    && fixture.DiagnosticsStore.Query(
                        sessionId: session.SessionId,
                        kind: StreamDiagnosticEventKind.ReconnectRecovered).Count > 0;
            },
            TimeSpan.FromSeconds(5));

        // Before the stall: bytes accumulated on connection 1.
        var failureEvent = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.UpstreamFailure).FirstOrDefault();
        Assert.IsNotNull(failureEvent);
        Assert.IsGreaterThan(0L, failureEvent.BytesSinceReconnect.GetValueOrDefault());

        // At the moment of recovery: BytesSinceReconnect is reset to 0 before any new reads.
        var recoveredEvent = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.ReconnectRecovered).FirstOrDefault();
        Assert.IsNotNull(recoveredEvent);
        Assert.AreEqual(0L, recoveredEvent.BytesSinceReconnect.GetValueOrDefault());

        // After recovery: bytes accumulate only from the new connection.
        var snapshot = fixture.Registry.TryGetSession(session.SessionId);
        Assert.IsNotNull(snapshot);
        Assert.IsGreaterThan(0L, snapshot.BytesSinceReconnect);

        cts.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_FirstByteLatency_IsRecordedAfterUpstreamConnect()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.StreamForever(chunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteOneChunkAfterDelay(chunk, TimeSpan.FromMilliseconds(120), ct));

        await using var fixture = await SessionFixture.CreateAsync(handler);
        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () => fixture.Registry.TryGetSession(session.SessionId)?.FirstByteLatencyMs > 0,
            TimeSpan.FromSeconds(5));

        var snapshot = fixture.Registry.TryGetSession(session.SessionId);
        Assert.IsNotNull(snapshot);
        Assert.IsGreaterThan(0, snapshot.FirstByteLatencyMs.GetValueOrDefault());
        Assert.IsGreaterThan(0L, snapshot.BytesSinceReconnect);

        var events = fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.FirstUpstreamByte);
        Assert.IsTrue(events.Any(x => x.FirstByteLatencyMs > 0));

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
        var chunk = FakeStreamingHandler.ValidTsPacket();
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
        var cooldownEvents = fixture.DiagnosticsStore.Query(kind: StreamDiagnosticEventKind.CooldownRecorded);
        Assert.IsTrue(cooldownEvents.Any(x =>
            x.ProviderId == fixture.Source.ProviderId
            && x.ProviderChannelId == fixture.Source.ProviderChannelId
            && x.RetryAfterSeconds is > 0));
    }

    [TestMethod]
    public async Task Session_ProviderProxyAuthRequired_RecordsCooldownAndRejectsInitialAttach()
    {
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.ProxyAuthenticationRequired);
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(30),
                StrikeCooldown = TimeSpan.FromSeconds(300),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        Assert.AreEqual(StreamAdmissionFailureKind.Cooldown, ex.FailureKind);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
        Assert.AreEqual(30, ex.RetryAfterSeconds);
        Assert.IsTrue(fixture.StrikeStore.IsCoolingDown(fixture.Source.SessionKey, out var remaining));
        Assert.IsGreaterThan(TimeSpan.Zero, remaining);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(30), remaining);
        Assert.AreEqual(1, handler.ConnectionCount);
    }

    [TestMethod]
    public async Task Session_RateLimitedWithoutRetryAfter_UsesRateLimitFallbackCooldown()
    {
        var handler = FakeStreamingHandler.ReturnStatus((HttpStatusCode)429);
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(30),
                StrikeCooldown = TimeSpan.FromSeconds(300),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        Assert.AreEqual(StreamAdmissionFailureKind.Cooldown, ex.FailureKind);
        Assert.AreEqual(30, ex.RetryAfterSeconds);
        Assert.IsTrue(fixture.StrikeStore.IsCoolingDown(fixture.Source.SessionKey, out var remaining));
        Assert.IsGreaterThan(TimeSpan.FromSeconds(30), remaining);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(60), remaining);
        Assert.AreEqual(1, handler.ConnectionCount);
    }

    [TestMethod]
    public async Task Session_RateLimited_UsesProviderRetryAfterForCooldown()
    {
        // Provider Retry-After should be honored directly instead of being raised to StrikeCooldown.
        var handler = FakeStreamingHandler.ReturnStatus((HttpStatusCode)429, response =>
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12)));
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(30),
                StrikeCooldown = TimeSpan.FromSeconds(300),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        Assert.AreEqual(StreamAdmissionFailureKind.Cooldown, ex.FailureKind);
        Assert.AreEqual(12, ex.RetryAfterSeconds);
        Assert.IsTrue(fixture.StrikeStore.IsCoolingDown(fixture.Source.SessionKey, out var remaining));
        Assert.IsGreaterThan(TimeSpan.Zero, remaining);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(12), remaining);
        Assert.AreEqual(1, handler.ConnectionCount);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(kind: StreamDiagnosticEventKind.CooldownRecorded).Any(),
            TimeSpan.FromSeconds(5));
        var cooldownEvents = fixture.DiagnosticsStore.Query(kind: StreamDiagnosticEventKind.CooldownRecorded);
        Assert.IsTrue(cooldownEvents.Any(x =>
            x.ProviderId == fixture.Source.ProviderId
            && x.ProviderChannelId == fixture.Source.ProviderChannelId
            && x.UpstreamFailureKind == UpstreamFailureKind.UpstreamRateLimited
            && x.RetryAfterSeconds == 12));
    }

    [TestMethod]
    public async Task Session_RateLimited_ProviderRetryAfterExceedsStrikeCooldown_UsesProviderRetryAfter()
    {
        // Provider says wait 30s; StrikeCooldown is only 10s.
        // Provider Retry-After is honored even when it exceeds the fallback cooldown cap.
        var handler = FakeStreamingHandler.ReturnStatus((HttpStatusCode)429, response =>
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(30),
                StrikeCooldown = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        Assert.AreEqual(StreamAdmissionFailureKind.Cooldown, ex.FailureKind);
        Assert.AreEqual(30, ex.RetryAfterSeconds);
        Assert.IsTrue(fixture.StrikeStore.IsCoolingDown(fixture.Source.SessionKey, out var remaining));
        Assert.IsGreaterThan(TimeSpan.FromSeconds(10), remaining);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(30), remaining);
        Assert.AreEqual(1, handler.ConnectionCount);
    }

    [TestMethod]
    public async Task Manager_ChannelInCooldown_RejectsWithoutOpeningUpstream()
    {
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.ProxyAuthenticationRequired);
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(30),
                StrikeCooldown = TimeSpan.FromSeconds(20),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        await AssertThrowsAsync<StreamAdmissionException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        await WaitUntilAsync(() => !fixture.Manager.TryGet(fixture.Source.SessionKey, out _), TimeSpan.FromSeconds(5));
        var connectionsAfterCooldown = handler.ConnectionCount;

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None).AsTask());

        Assert.AreEqual(StreamAdmissionFailureKind.Cooldown, ex.FailureKind);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
        Assert.IsNotNull(ex.RetryAfterSeconds);
        Assert.AreEqual(connectionsAfterCooldown, handler.ConnectionCount);
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

    [TestMethod]
    public async Task Session_LateJoiner_StartsAtLiveEdge()
    {
        // Late joiners attach at live position and receive only future chunks — no buffer replay.
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(handler);

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var firstSubscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), timeout.Token);
        await WaitUntilAsync(() => firstSubscriber.BytesSent >= 188 * 4, TimeSpan.FromSeconds(5));

        var firstSubscriberBytesBeforeLateJoin = firstSubscriber.BytesSent;
        var secondSubscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), timeout.Token);

        await WaitUntilAsync(
            () => secondSubscriber.BytesSent > 0 && firstSubscriber.BytesSent > firstSubscriberBytesBeforeLateJoin,
            TimeSpan.FromSeconds(5));

        Assert.IsGreaterThan(0L, secondSubscriber.BytesSent);
        Assert.IsGreaterThan(firstSubscriberBytesBeforeLateJoin, firstSubscriber.BytesSent);
        Assert.AreEqual(SessionState.Live, session.State);
        Assert.IsTrue(fixture.Manager.TryGet(session.Key, out _));

        timeout.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsInternalLateJoiner_StartsFromSafeSnapshot()
    {
        var handler = FakeStreamingHandler.StreamForeverSequence(MpegTsSafeStartupSequence());
        await using var fixture = await SessionFixture.CreateAsync(handler);

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var firstContext = CreateResponseCaptureContext();
        // Use CancellationToken.None for attaching so subscriber lifetime is not tied to
        // the polling wait below. Subscribers are torn down explicitly via CompleteAsync.
        var firstSubscriber = await session.AttachSubscriberAsync(firstContext.Context, CancellationToken.None);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count > 0,
            TimeSpan.FromSeconds(10));
        Assert.IsTrue(
            fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count > 0,
            "Timed out waiting for MpegTsSafeStartSelected — safe snapshot not yet available.");

        var lateContext = CreateResponseCaptureContext();
        var lateSubscriber = await session.AttachSubscriberAsync(lateContext.Context, CancellationToken.None, isInternal: true);
        await WaitUntilAsync(() => lateSubscriber.BytesSent >= 188 * 4, TimeSpan.FromSeconds(10));

        await lateSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await firstSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await lateSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await firstSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var data = lateContext.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188 * 4, data.Length);
        Assert.AreEqual(0, data.Length % 188);
        for (var offset = 0; offset < data.Length; offset += 188)
            Assert.AreEqual(0x47, data[offset]);

        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, [0x00, 0x00, 0x01, 0x67]), "Late snapshot should include H.264 SPS.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, [0x00, 0x00, 0x01, 0x68]), "Late snapshot should include H.264 PPS.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, [0x00, 0x00, 0x01, 0x65]), "Late snapshot should include H.264 IDR.");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_CleanRelay_StillSelectsMpegTsSafeStart()
    {
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.InternalServerError);
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            cleanRelayMode: "remux",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            streamUrl: "http://fake/stream?ffmpegMode=relay-ts-sequence&delayMs=1");

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var firstContext = CreateResponseCaptureContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var firstSubscriber = await session.AttachSubscriberAsync(firstContext.Context, timeout.Token);
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                    sessionId: session.SessionId,
                    kind: StreamDiagnosticEventKind.FfmpegRelayStarted)
                .Any(x => x.Message?.Contains(UpstreamRelayModes.FfmpegCleanRemux, StringComparison.Ordinal) == true),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count > 0,
            TimeSpan.FromSeconds(5));

        var snapshot = fixture.Registry.TryGetSession(session.SessionId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(UpstreamRelayModes.FfmpegCleanRemux, snapshot.RelayMode);
        Assert.AreEqual(0, handler.ConnectionCount);

        var lateContext = CreateResponseCaptureContext();
        var lateSubscriber = await session.AttachSubscriberAsync(lateContext.Context, timeout.Token);
        await WaitUntilAsync(() => lateSubscriber.BytesSent >= 188 * 4, TimeSpan.FromSeconds(5));

        timeout.Cancel();
        await lateSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await firstSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await lateSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await firstSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = lateContext.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188 * 4, data.Length);
        Assert.AreEqual(0, data.Length % 188);
        for (var offset = 0; offset < data.Length; offset += 188)
            Assert.AreEqual(0x47, data[offset]);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_AutoRelay_UnstableChannel_SelectsCleanRemuxAtStartup()
    {
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.InternalServerError);
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            cleanRelayMode: "auto",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            streamUrl: "http://fake/stream?ffmpegMode=relay-ts-sequence&delayMs=1");
        await fixture.SeedHealthEventsAsync(
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "MpegTsSyncLost",
                EventUtc = DateTime.UtcNow.AddMinutes(-5),
                TsSyncLoss = true,
            },
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "MpegTsSyncLost",
                EventUtc = DateTime.UtcNow.AddMinutes(-4),
                TsSyncLoss = true,
            },
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "RecoveryOutputResumed",
                EventUtc = DateTime.UtcNow.AddMinutes(-3),
                SafeStartKind = "H264Idr",
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), timeout.Token);

        await WaitUntilAsync(
            () => fixture.Registry.TryGetSession(session.SessionId)?.RelayMode == UpstreamRelayModes.FfmpegCleanRemux,
            TimeSpan.FromSeconds(5));

        var snapshot = fixture.Registry.TryGetSession(session.SessionId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(StreamChannelHealthProfile.Unstable, snapshot.HealthProfile);
        Assert.AreEqual("auto", snapshot.RelayPolicy);
        Assert.AreEqual(UpstreamRelayModes.FfmpegCleanRemux, snapshot.RelayMode);
        StringAssert.Contains(snapshot.RelayDecisionReason!, "Unstable");
        Assert.AreEqual(0, handler.ConnectionCount, "Direct HTTP must not be used for an unstable Auto channel when clean remux starts.");

        timeout.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_CleanRelayReconnect_ResetsSafeStartAndRecovers()
    {
        // relay-ts-sequence with cycles=1 emits one PAT+PMT+SPS+PPS+IDR sequence then exits.
        // Each reconnect spins up a new FFmpeg process that also exits after one cycle, so we
        // get repeated relay-start + safe-start events without an infinite stream.
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.InternalServerError);
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            cleanRelayMode: "remux",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            streamUrl: "http://fake/stream?ffmpegMode=relay-ts-sequence&cycles=1&delayMs=1",
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(60),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore
                .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.FfmpegRelayStarted)
                .Count(x => x.Message?.Contains(UpstreamRelayModes.FfmpegCleanRemux, StringComparison.Ordinal) == true) >= 2,
            TimeSpan.FromSeconds(15));

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore
                .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected)
                .Count >= 2,
            TimeSpan.FromSeconds(5));

        var relayCount = fixture.DiagnosticsStore
            .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.FfmpegRelayStarted)
            .Count(x => x.Message?.Contains(UpstreamRelayModes.FfmpegCleanRemux, StringComparison.Ordinal) == true);
        var safeStartCount = fixture.DiagnosticsStore
            .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count;
        var reconnectCount = fixture.DiagnosticsStore
            .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled).Count;

        Assert.IsGreaterThanOrEqualTo(2, relayCount);
        Assert.IsGreaterThanOrEqualTo(2, safeStartCount);
        Assert.IsGreaterThanOrEqualTo(1, reconnectCount);
        Assert.AreEqual(0, handler.ConnectionCount, "Direct HTTP must not be used when clean relay is active.");

        var lateContext = CreateResponseCaptureContext();
        var lateSubscriber = await session.AttachSubscriberAsync(lateContext.Context, cts.Token);
        await WaitUntilAsync(() => lateSubscriber.BytesSent >= 188, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await lateSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await lateSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = lateContext.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188, data.Length);
        Assert.AreEqual(0, data.Length % 188, "Late subscriber must receive whole TS packets only.");
        Assert.AreEqual(0x47, data[0], "First byte must be a TS sync byte.");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_CleanRelayFallback_DirectStreamStillWorks()
    {
        var handler = FakeStreamingHandler.StreamForever(FakeStreamingHandler.ValidTsPacket());
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            cleanRelayMode: "remux",
            ffmpegPath: FakeFfmpegBinary.LocateExecutable(),
            streamUrl: "http://fake/stream?ffmpegMode=relay-stall",
            cleanRelayOptions: new CleanRelayOptions { StartupTimeoutSeconds = 1, FallbackToDirect = true });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore
                .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.FfmpegRelayFallbackToDirect)
                .Count > 0,
            TimeSpan.FromSeconds(10));

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        var snapshot = fixture.Registry.TryGetSession(session.SessionId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(UpstreamRelayModes.Direct, snapshot.RelayMode);
        Assert.AreEqual("clean_relay_startup_failed", snapshot.LastRelayFallbackReason);
        Assert.IsGreaterThan(0, handler.ConnectionCount);
        Assert.IsGreaterThan(0L, subscriber.BytesSent);

        cts.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsSyncLoss_DropsUnsafeBytesAndEmitsDiagnostic()
    {
        var handler = FakeStreamingHandler.StreamForeverSequence(
        [
            [0x00, 0x01, 0x02, 0x03],
            FakeStreamingHandler.ValidTsPacket(),
        ]);
        await using var fixture = await SessionFixture.CreateAsync(handler);

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriber = await session.AttachSubscriberAsync(capture.Context, timeout.Token);
        await WaitUntilAsync(() => subscriber.BytesSent >= 188, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.MpegTsSyncLost).Count > 0,
            TimeSpan.FromSeconds(5));

        timeout.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188, data.Length);
        Assert.AreEqual(0x47, data[0]);
        Assert.AreEqual(0, data.Length % 188);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_InternalLateJoinerGetsSafeStartFromNewGeneration()
    {
        // First connection stalls after a few plain TS packets (no safe start established).
        // After reconnect, the handler streams a full safe-startup sequence.
        var safeSequence = MpegTsSafeStartupSequence();
        var handler = FakeStreamingHandler.StreamForeverSequence(safeSequence);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(FakeStreamingHandler.ValidTsPacket(), 3, ct));

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        // Wait for reconnect to happen and a safe start to be detected on the new connection.
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectRecovered).Count > 0
               && fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count > 0,
            TimeSpan.FromSeconds(12));

        var lateContext = CreateResponseCaptureContext();
        var lateSubscriber = await session.AttachSubscriberAsync(lateContext.Context, cts.Token, isInternal: true);
        await WaitUntilAsync(() => lateSubscriber.BytesSent >= 188, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await lateSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await lateSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = lateContext.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188, data.Length);
        Assert.AreEqual(0, data.Length % 188, "Late subscriber must receive whole TS packets only.");
        Assert.AreEqual(0x47, data[0], "First byte must be a TS sync byte.");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_HoldsExistingSubscriberUntilSafeStart()
    {
        var unsafeRecoveredPacket = FakeStreamingHandler.ValidTsPacket(0xDD);
        var recoveredSequence = new[] { unsafeRecoveredPacket }
            .Concat(MpegTsSafeStartupSequence())
            .ToArray();
        var safeChunk = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(safeChunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(FakeStreamingHandler.ValidTsPacket(0xA1), 3, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(recoveredSequence, safeChunk, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 8 * 188,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(8));
        await WaitUntilAsync(() => subscriber.BytesSent >= 188 * 7, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188 * 6, data.Length);
        Assert.AreEqual(-1, IndexOf(data, unsafeRecoveredPacket), "Unsafe post-reconnect packet must be suppressed.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, PatPacket(100)), "Recovered output should resume from PAT/PMT safe start.");

        var resumed = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.RecoveryOutputResumed).First();
        Assert.IsGreaterThan(0, resumed.BytesSuppressed.GetValueOrDefault());
        Assert.IsGreaterThan(0, resumed.OutputHeldMs.GetValueOrDefault());

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_RewoundReplay_TrimsToPreFailurePosition()
    {
        // Provider behavior observed on toontown-tv-srv1: a reconnected upstream fast-bursts
        // ~60 s of content from before the failure point. The overlap trim must suppress the
        // replayed span and resume at the first IDR at/after the last DTS relayed pre-failure.
        const long preFailureDts = 102L * 90000;
        var rewoundIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xA5], 42L * 90000);
        var caughtUpIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xC5], 104L * 90000);

        var preFailureSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x91], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x92], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x93], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x94], 101L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x95], preFailureDts),
        };

        // Rewound burst: 14 packets (> the 8-packet safe-start search limit below) before the
        // caught-up IDR — regression guard that an active trim bypasses the forced-retune limits.
        var recoveredSequence = new List<byte[]>
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 42L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 42L * 90000),
            rewoundIdr,
        };
        for (var i = 0; i < 6; i++)
            recoveredSequence.Add(TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, (byte)(0xA6 + i)], (43 + i) * 90000L));
        recoveredSequence.Add(TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xB5], 72L * 90000));
        recoveredSequence.Add(PatPacket(100));
        recoveredSequence.Add(PmtPacket(100, 256));
        recoveredSequence.Add(caughtUpIdr);

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(filler);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(preFailureSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(recoveredSequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 8 * 188,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), caughtUpIdr) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(-1, IndexOf(data, rewoundIdr), "Replayed pre-failure content must be trimmed, not relayed.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, caughtUpIdr), "Output must resume at the caught-up IDR.");

        Assert.IsNotEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryFailedUnsafe));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryForcedRetune));

        var trimmed = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed).First();
        Assert.IsGreaterThan(0, trimmed.BytesSuppressed.GetValueOrDefault());

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ContinuousRelayTimelineRewind_DoesNotPublishReplayedSpan()
    {
        const long preFailureDts = 102L * 90000;
        var originalIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x91], 100L * 90000);
        var replayedIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xA5], 42L * 90000);
        var caughtUpIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xC5], 104L * 90000);

        var initialSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x81], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x82], 100L * 90000),
            originalIdr,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x92], 101L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x93], preFailureDts),
        };
        var replaySequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 42L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 42L * 90000),
            replayedIdr,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA6], 72L * 90000),
            PatPacket(100),
            PmtPacket(100, 256),
            caughtUpIdr,
        };

        var filler = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xD1], 105L * 90000);
        var handler = new FakeStreamingHandler(ct => FakeStreamingHandler.WritePhasedSequenceThenForever(
            initialSequence,
            replaySequence,
            filler,
            TimeSpan.FromMilliseconds(150),
            ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(2),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 8 * 188,
                RecoveryOverlapTrimHoldLimit = TimeSpan.FromSeconds(2),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.InProcessRelayTimelineRewind).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), caughtUpIdr) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(1, handler.ConnectionCount, "The rewind must occur inside one uninterrupted upstream connection.");
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectRecovered));
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, originalIdr));
        Assert.AreEqual(-1, IndexOf(data, replayedIdr), "Replayed content from the hidden FFmpeg reconnect must be suppressed.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, caughtUpIdr), "Output must resume at the first caught-up IDR.");
        Assert.IsNotEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ContinuousRelayTimelineRewind_WithinSingleBatch_DoesNotPublishReplayedSpan()
    {
        // Variant of the test above where the rewind lands entirely inside one read
        // chunk: the batch opens with a healthy pre-jump frame, so the batch-boundary
        // crossing delta looks fine and only the backward first-to-last delta within
        // the batch reveals the jump. Without the within-batch rewind check, that span
        // counted as one unit of clamped evidence (any negative delta satisfies
        // "<= max ticks"), the replay's healthy pacing then reset the counter, and the
        // replayed content flooded through undetected.
        const long preFailureDts = 102L * 90000;
        var originalIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x91], 100L * 90000);
        var replayedIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xA5], 42L * 90000);
        var caughtUpIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xC5], 104L * 90000);

        var initialSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x81], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x82], 100L * 90000),
            originalIdr,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x92], 101L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x93], preFailureDts),
        };
        var midBatchChunk = new[]
        {
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x94], 103L * 90000),
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 42L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 42L * 90000),
            replayedIdr,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA6], 72L * 90000),
        }.SelectMany(packet => packet).ToArray();
        var caughtUpSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            caughtUpIdr,
        };

        var filler = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xD1], 105L * 90000);
        var handler = new FakeStreamingHandler(ct => FakeStreamingHandler.WritePhasedSequencesThenForever(
            [initialSequence, [midBatchChunk], caughtUpSequence],
            filler,
            TimeSpan.FromMilliseconds(150),
            ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            // The fixture's default 188-byte read chunk makes every batch a single TS
            // packet, which can never contain a jump; a larger read chunk lets the
            // whole mid-batch write arrive as one batch.
            bufferOptions: new BufferOptions
            {
                ReadChunkSizeBytes = 4096,
                SubscriberQueueCapacity = 128,
                MaxBytesPerSession = 64 * 1024,
                MaxBytesHardCap = 4 * 1024 * 1024,
            },
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(2),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 8 * 188,
                RecoveryOverlapTrimHoldLimit = TimeSpan.FromSeconds(2),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.InProcessRelayTimelineRewind).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), caughtUpIdr) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(1, handler.ConnectionCount, "The rewind must occur inside one uninterrupted upstream connection.");
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectRecovered));
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, originalIdr));
        Assert.AreEqual(-1, IndexOf(data, replayedIdr), "A rewind hidden inside a single batch must be suppressed.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, caughtUpIdr), "Output must resume at the first caught-up IDR.");
        Assert.IsNotEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ContinuousRelayTimelineRewind_ThatCannotCatchUpWithinBudget_ResumesOnlyOnceFreshDtsArrives()
    {
        // Continuous-relay (in-process, no reconnect) counterpart of the
        // MpegTsReconnect_SlowRewoundReplay test above: the trim gives up at its own
        // (short) hold limit, but must keep withholding output until the replay actually
        // reaches the pre-failure DTS rather than trusting whatever IDR shows up right
        // after the abandon. A stream that never reaches the target at all is expected to
        // eventually force a retune via the generic byte ceiling -- this test instead lets
        // fresh content legitimately arrive after the abandon, so no forced retune should
        // occur, and none of the still-stale content in between may leak.
        const long preFailureDts = 102L * 90000;
        var staleIdr1 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xD1], 42L * 90000);
        var staleIdr2 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xD2], 90L * 90000);
        var freshIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xD5], preFailureDts);
        var postFreshFrame = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xD6], preFailureDts + 1500);

        var initialSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x81], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x82], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x83], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x84], preFailureDts),
        };
        var replayStart = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 42L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 42L * 90000),
            staleIdr1,
        };
        // Real-time gap so the trim's own 100ms budget genuinely elapses (and abandons)
        // before the replay resumes toward the pre-failure position.
        var afterFirstPause = new[] { staleIdr2 };
        // Re-sending PAT/PMT here, as a real repeating provider would, keeps
        // MarkMpegTsSafeStartIfReady's rolling PAT/PMT splice candidate from staying
        // pinned to the single PAT/PMT sent at replayStart -- see the sibling
        // MpegTsReconnect test above for why that candidate must be kept fresh.
        var afterSecondPause = new[] { PatPacket(100), PmtPacket(100, 256), freshIdr };
        var afterThirdPause = new[] { postFreshFrame };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = new FakeStreamingHandler(ct => FakeStreamingHandler.WritePhasedSequencesThenForever(
            [initialSequence, replayStart, afterFirstPause, afterSecondPause, afterThirdPause],
            filler,
            TimeSpan.FromMilliseconds(300),
            ct));
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(2),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(1),
                RecoverySafeStartSearchLimitBytes = 64 * 1024,
                RecoveryOverlapTrimHoldLimit = TimeSpan.FromMilliseconds(100),
                RecoveryOverlapTrimMaxBytes = 1024 * 1024,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), postFreshFrame) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(1, handler.ConnectionCount);
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryForcedRetune));
        Assert.IsTrue(session.State is SessionState.Live or SessionState.HoldingOutput);
        Assert.AreEqual(
            -1,
            IndexOf(data, staleIdr1),
            "Stale, still-behind-target content must not resume output just because the trim gave up on precise scanning.");
        Assert.AreEqual(
            -1,
            IndexOf(data, staleIdr2),
            "Stale, still-behind-target content must not resume output just because the trim gave up on precise scanning.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, freshIdr), "Output must resume once the replay actually reaches the pre-failure DTS.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, postFreshFrame));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ContinuousRelayClampedDtsRamp_HoldsThroughRampAndResumesOnlyAfterHealthyDeltas()
    {
        // Regression test for the clamped-DTS-ramp detector (DetectInProcessTimelineRewind /
        // ShouldSuppressSafeStartForClampedRampRecovery). Unlike the two tests above, this
        // never puts a genuine backward DTS jump on the wire: FFmpeg's mpegts muxer enforces
        // non-decreasing output DTS, so a provider's in-process restart never reaches the
        // scanner as a negative delta - it gets clamped forward to last+1, last+2, ... instead.
        // This drives that exact near-zero-tick ramp signature directly, including an IDR that
        // arrives while the ramp is still active: pre-b93d4bd, resume was gated on "next IDR"
        // and would have let that content through immediately; it must stay suppressed until
        // ClampedDtsRampMinEvidence consecutive healthy (frame-paced) deltas are observed.
        //
        // It also covers a second, more subtle leak: ClampedDtsRampMinEvidence requires several
        // consecutive suspicious samples before the ramp is *confirmed*, but every one of those
        // evidence-gathering batches is itself replayed provider content — not just the batch
        // that finally crosses the threshold. Before the fix, only the batch that crossed
        // ClampedDtsRampMinEvidence (and everything after) was suppressed; the batches used to
        // accumulate evidence toward that threshold were published normally, because
        // _recoveryOutputHoldActive only flips true once the detector actually fires. The
        // evidencePacket1/evidencePacket2 assertions below cover exactly that gap.
        const long baseDts = 100L * 90000;
        const long frameSpacing = 1500L; // ~60fps decode pacing, far above ClampedDtsRampMaxDeltaTicks (180).

        var stillRampingIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xF1], baseDts + 4);
        var resumingIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xC2], baseDts + 6 + 3 * frameSpacing);
        var postResumeFrame = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xD3], baseDts + 6 + 4 * frameSpacing);

        // The first two evidence-gathering samples of the clamped ramp: neither individually
        // reaches ClampedDtsRampMinEvidence (3), so before the fix the detector had not yet
        // fired when these were processed and they were published like ordinary live content.
        var evidencePacket1 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA1], baseDts + 1);
        var evidencePacket2 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA2], baseDts + 2);

        var sequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x81], baseDts - 3 * frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x82], baseDts - 2 * frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x91], baseDts - frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x92], baseDts),
            // Clamped ramp: three consecutive near-zero-tick crossings reach
            // ClampedDtsRampMinEvidence on the third and fire the detector.
            evidencePacket1,
            evidencePacket2,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA3], baseDts + 3),
            stillRampingIdr,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA5], baseDts + 5),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA6], baseDts + 6),
            // Healthy, frame-paced deltas resume: three consecutive crossings above
            // ClampedDtsRampMaxDeltaTicks are required before suppression lifts.
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB1], baseDts + 6 + frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB2], baseDts + 6 + 2 * frameSpacing),
            resumingIdr,
            postResumeFrame,
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = new FakeStreamingHandler(ct => FakeStreamingHandler.WriteSequenceThenForever(sequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(2),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 64 * 1024,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.InProcessRelayTimelineRewind).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), postResumeFrame) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(1, handler.ConnectionCount, "The clamped ramp must be handled inside one uninterrupted upstream connection.");
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectRecovered));
        Assert.AreEqual(
            -1,
            IndexOf(data, stillRampingIdr),
            "An IDR arriving mid-ramp must not resume output while deltas are still clamped (b93d4bd regression).");
        Assert.AreEqual(
            -1,
            IndexOf(data, evidencePacket1),
            "A batch still accumulating clamped-ramp evidence is itself replayed content and must not be published before the ramp is confirmed.");
        Assert.AreEqual(
            -1,
            IndexOf(data, evidencePacket2),
            "A batch still accumulating clamped-ramp evidence is itself replayed content and must not be published before the ramp is confirmed.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, resumingIdr), "Output must resume at the IDR that follows enough healthy deltas.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, postResumeFrame));

        var safeStarts = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected);
        Assert.IsGreaterThanOrEqualTo(2, safeStarts.Count);
        Assert.IsTrue(safeStarts.Skip(1).Any(x => string.Equals(x.SafeStartKind, "H264Idr", StringComparison.Ordinal)));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ContinuousRelayClampedDtsRamp_SlowToStabilize_ResumesGracefullyInsteadOfForcedRetune()
    {
        // Regression test: ClampedDtsRampMinEvidence is evidence-based (consecutive
        // healthy deltas), not time-based, so a ramp can legitimately take longer than
        // the generic RecoveryOutputHoldLimit to accumulate enough evidence. Before the
        // fix, IsRecoveryHoldLimitExceeded() had no exemption for a clamped-ramp hold
        // (only _recoveryTrimActive was exempted), so a ramp still waiting past the
        // generic limit was torn down via RecoveryForcedRetune / UpstreamConnectException
        // instead of being allowed to resume once the healthy deltas actually arrived
        // (reproduced live via the CLEAN-RELAY-06 lab scenario). The generic
        // RecoveryOutputHoldLimit here is deliberately shorter than the real-time gap
        // before the healthy deltas arrive, while the dedicated ClampedDtsRampHoldLimit
        // is long enough to cover it.
        const long baseDts = 100L * 90000;
        const long frameSpacing = 1500L; // ~60fps decode pacing, far above ClampedDtsRampMaxDeltaTicks (180).

        var resumingIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xC2], baseDts + 6 + 3 * frameSpacing);
        var postResumeFrame = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xD3], baseDts + 6 + 4 * frameSpacing);

        var beforePause = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x81], baseDts - 3 * frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x82], baseDts - 2 * frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x91], baseDts - frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x92], baseDts),
            // Clamped ramp: three consecutive near-zero-tick crossings reach
            // ClampedDtsRampMinEvidence and fire the detector, entering the hold.
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA1], baseDts + 1),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA2], baseDts + 2),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA3], baseDts + 3),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA5], baseDts + 5),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA6], baseDts + 6),
        };

        // A real-time gap before the stream settles onto healthy, frame-paced deltas —
        // longer than RecoveryOutputHoldLimit but well inside ClampedDtsRampHoldLimit.
        var afterPause = new[]
        {
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB1], baseDts + 6 + frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB2], baseDts + 6 + 2 * frameSpacing),
            resumingIdr,
            postResumeFrame,
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = new FakeStreamingHandler(ct => FakeStreamingHandler.WritePhasedSequencesThenForever(
            [beforePause, afterPause],
            filler,
            TimeSpan.FromSeconds(2),
            ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(10),
                ContentStallTimeout = TimeSpan.FromSeconds(10),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(1),
                ClampedDtsRampHoldLimit = TimeSpan.FromSeconds(8),
                RecoverySafeStartSearchLimitBytes = 64 * 1024,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.InProcessRelayTimelineRewind).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), postResumeFrame) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(1, handler.ConnectionCount, "The slow-to-stabilize ramp must be handled inside one uninterrupted upstream connection.");
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryHoldLimitExceeded),
            "The generic RecoveryOutputHoldLimit must not apply while a clamped-ramp hold is still waiting for evidence.");
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryForcedRetune));
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, resumingIdr), "Output must resume at the IDR that follows enough healthy deltas.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, postResumeFrame));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ContinuousRelayClampedDtsRamp_NoDtsBatchMidStreak_DoesNotPrematurelyFlushPendingCandidates()
    {
        // Regression test for a gap in the candidate-batch withholding added alongside the
        // clamped-DTS-ramp detector: DetectInProcessTimelineRewind returned
        // TimelineRewindSignal.None whenever a batch carried no video timestamp at all
        // (batch.EarliestVideoDts90k is null) — indistinguishable, at that call site, from a
        // batch that was genuinely resolved as healthy. The caller treats None as "safe to
        // flush", so any batch with no video PES header (audio-only, PAT/PMT-only, or a
        // continuation chunk — routine on a real socket, but absent from the fully-timestamped
        // packets every other test in this class feeds the scanner) arriving mid-streak
        // flushed still-unresolved, possibly-replayed evidence batches to subscribers before
        // the ramp was confirmed or cleared. This is exactly what the CLEAN-RELAY-05/06 lab
        // scenarios caught live (real TS chunking off a socket hits this constantly) despite
        // the synthetic unit tests above never exercising it.
        const long baseDts = 100L * 90000;
        const long frameSpacing = 1500L;

        var evidencePacket1 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA1], baseDts + 1);
        var noDtsPacketMidStreak = FakeStreamingHandler.ValidTsPacket(0xEE);
        var evidencePacket2 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA2], baseDts + 2);
        var evidencePacket3 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA3], baseDts + 3);
        var resumingIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xC2], baseDts + 3 + 3 * frameSpacing);
        var postResumeFrame = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xD3], baseDts + 3 + 4 * frameSpacing);

        var sequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x81], baseDts - 3 * frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x82], baseDts - 2 * frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x91], baseDts - frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0x92], baseDts),
            // Clamped-ramp evidence accumulating (1 of 3), then a no-video-DTS batch lands
            // mid-streak, then evidence continues to the confirming 3rd sample.
            evidencePacket1,
            noDtsPacketMidStreak,
            evidencePacket2,
            evidencePacket3,
            // Healthy, frame-paced deltas resume.
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB1], baseDts + 3 + frameSpacing),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB2], baseDts + 3 + 2 * frameSpacing),
            resumingIdr,
            postResumeFrame,
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = new FakeStreamingHandler(ct => FakeStreamingHandler.WriteSequenceThenForever(sequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(2),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 64 * 1024,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.InProcessRelayTimelineRewind).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), postResumeFrame) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(1, handler.ConnectionCount);
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectScheduled));
        Assert.AreEqual(
            -1,
            IndexOf(data, evidencePacket1),
            "A candidate batch withheld while ramp evidence accumulates must not leak just because a later no-video-DTS batch arrived before the ramp resolved.");
        Assert.AreEqual(
            -1,
            IndexOf(data, evidencePacket2),
            "A candidate batch withheld while ramp evidence accumulates must not leak just because a later no-video-DTS batch arrived before the ramp resolved.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, resumingIdr), "Output must resume at the IDR that follows enough healthy deltas.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, postResumeFrame));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_SlowRewoundReplay_TrimAbandonedThenResumesOnlyOnceContentIsFresh()
    {
        // A provider that replays rewound content slower than real-time would never catch
        // up to the pre-failure DTS within the trim's own (short) budget; the trim must
        // give up at its hold limit rather than block forever. But giving up on precisely
        // scanning must not mean trusting whatever IDR happens to show up next: everything
        // still short of the pre-failure DTS is positively known to be replayed content.
        // Regression test for exactly that gap: prior to the fix, ShouldSuppressSafeStartForOverlapTrim
        // stopped checking DTS entirely once the trim was abandoned, so staleIdr1/staleIdr2
        // (both still well behind preFailureDts) resumed output immediately on whichever one
        // arrived right after the abandon — publishing stale, already-seen content as if it
        // were live (exactly the "replayed content published despite the abandoned trim"
        // failure the CLEAN-RELAY-07 lab scenario caught). Output must stay held until the
        // replay actually reaches the pre-failure position, and no forced retune should occur
        // since the content does legitimately catch up, just not inside the trim's own budget.
        const long preFailureDts = 102L * 90000;
        var staleIdr1 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xD1], 43L * 90000);
        var staleIdr2 = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xD2], 90L * 90000);
        var freshIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xD5], preFailureDts);
        var postFreshFrame = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xD6], preFailureDts + 1500);

        var preFailureSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x91], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x92], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x93], preFailureDts),
        };

        var beforePause = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 42L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 42L * 90000),
            staleIdr1,
        };
        // Real-time gap so the trim's own 100ms budget genuinely elapses (and abandons)
        // before the replay resumes toward the pre-failure position.
        var afterPause = new[] { staleIdr2 };
        // A real MPEG-TS provider repeats PAT/PMT every ~100-500ms (a broadcast-spec
        // requirement most encoders honor); MarkMpegTsSafeStartIfReady deliberately snaps
        // an IDR-based resume to the most recently seen PAT/PMT packet boundary rather than
        // the IDR's own lease (so a newly tuned client gets fresh program info up front).
        // Re-sending PAT/PMT here, immediately before freshIdr, keeps that candidate rolled
        // forward the way a real repeating provider would -- without it, the candidate would
        // still point at the single PAT/PMT sent at the very start of the replay, and the
        // eventual resume would splice in there instead of at freshIdr, republishing every
        // stale packet in between.
        var afterSecondPause = new[] { PatPacket(100), PmtPacket(100, 256), freshIdr };
        var afterThirdPause = new[] { postFreshFrame };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(filler);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(preFailureSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WritePhasedSequencesThenForever(
            [beforePause, afterPause, afterSecondPause, afterThirdPause],
            filler,
            TimeSpan.FromMilliseconds(300),
            ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                // Must comfortably exceed the 300ms inter-phase pause below, otherwise
                // M3Undle's own stall detector reconnects a second time mid-replay before
                // the trim gets a chance to abandon and chase the target DTS as intended.
                ReadStallTimeout = TimeSpan.FromSeconds(2),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoveryOverlapTrimHoldLimit = TimeSpan.FromMilliseconds(100),
                RecoverySafeStartSearchLimitBytes = 64 * 1024,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned).Count > 0,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), postFreshFrame) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.IsNotEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryFailedUnsafe));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryForcedRetune));
        Assert.AreEqual(
            -1,
            IndexOf(data, staleIdr1),
            "Stale, still-behind-target content must not resume output just because the trim gave up on precise scanning.");
        Assert.AreEqual(
            -1,
            IndexOf(data, staleIdr2),
            "Stale, still-behind-target content must not resume output just because the trim gave up on precise scanning.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, freshIdr), "Output must resume once the replay actually reaches the pre-failure DTS.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, postFreshFrame));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_UpstreamFailsDuringTrim_AbandonsTrimAndResumesOnNextRecovery()
    {
        // Wedge shape observed on toontown-int-srv1 (CLEAN-RELAY-02 / XTR-WEB-01): the
        // upstream fails again while a trim is still holding output, so the batch-driven
        // trim budgets never fire. The failure path must resolve the trim as abandoned and
        // the next recovery must resume plainly instead of re-entering a fresh trim.
        const long preFailureDts = 102L * 90000;
        var rewoundIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xE1], 43L * 90000);
        var freshIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xE2], 200L * 90000);

        var preFailureSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x91], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x92], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x93], preFailureDts),
        };

        // Rewound replay that stalls before ever reaching the pre-failure position.
        var rewoundThenStallSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 42L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 42L * 90000),
            rewoundIdr,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xA3], 44L * 90000),
        };

        var secondRecoverySequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xC1], 200L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xC2], 200L * 90000),
            freshIdr,
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(filler);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(preFailureSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(rewoundThenStallSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(secondRecoverySequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), freshIdr) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.AreEqual(-1, IndexOf(data, rewoundIdr), "Content suppressed by the wedged trim must not be relayed.");
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, freshIdr), "The recovery after the abandoned trim must resume output.");

        Assert.IsNotEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryFailedUnsafe));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryForcedRetune));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_FlappingSourceAfterAbandonedTrim_SkipsTrimDuringCooldown()
    {
        // After a trim is abandoned because the upstream failed mid-replay, a source that
        // keeps flapping with a rewound-looking timeline (an FFmpeg relay restart produces
        // one on every reconnect) must not re-arm the trim during the retry cooldown:
        // recovery degrades to the plain first-IDR resume so output keeps flowing.
        const long preFailureDts = 102L * 90000;
        var rewoundIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xF1], 43L * 90000);
        var freshIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xF2], 200L * 90000);
        var secondRewoundIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xF3], 150L * 90000);

        var preFailureSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x91], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x92], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x93], preFailureDts),
        };

        var rewoundThenStallSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 42L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 42L * 90000),
            rewoundIdr,
        };

        var secondRecoverySequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xC1], 200L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xC2], 200L * 90000),
            freshIdr,
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB1], 201L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x41, 0xB2], 202L * 90000),
        };

        // Third recovery rewinds again (50 s behind the fresh position) — within the trim
        // window, but the cooldown from the abandoned trim must keep the trim disarmed.
        var thirdRecoverySequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xC3], 150L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xC4], 150L * 90000),
            secondRewoundIdr,
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(filler);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(preFailureSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(rewoundThenStallSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(secondRecoverySequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(thirdRecoverySequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), secondRewoundIdr) >= 0, TimeSpan.FromSeconds(10));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(0, IndexOf(data, freshIdr), "The recovery after the abandoned trim must resume output.");
        Assert.IsGreaterThanOrEqualTo(
            0,
            IndexOf(data, secondRewoundIdr),
            "A rewound recovery during the trim cooldown must resume plainly and relay its content.");

        Assert.HasCount(1, fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryFailedUnsafe));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryForcedRetune));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_ForwardJump_ResumesAtFirstIdrWithoutTrim()
    {
        // Reconnected stream continues ahead of the pre-failure position (no replay):
        // the standard first-IDR resume applies and no trim events are recorded.
        var forwardIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xE5], 107L * 90000);

        var preFailureSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x91], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x92], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x93], 102L * 90000),
        };

        var recoveredSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 107L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 107L * 90000),
            forwardIdr,
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(filler);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(preFailureSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(recoveredSequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), forwardIdr) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsGreaterThanOrEqualTo(0, IndexOf(capture.Body.ToArray(), forwardIdr));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_UnrelatedTimeline_ResumesAtFirstIdrWithoutTrim()
    {
        // A rewind beyond RecoveryOverlapTrimMaxRewindSeconds means the provider restarted
        // its timestamp timeline; treat it like today's recovery (first-IDR resume).
        const long preFailureDts = 1000L * 90000;
        var unrelatedIdr = TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0xF5], 90000);

        var preFailureSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x91], preFailureDts),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x92], preFailureDts),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x93], preFailureDts),
        };

        var recoveredSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0xA1], 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0xA2], 90000),
            unrelatedIdr,
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(filler);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(preFailureSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(recoveredSequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => IndexOf(capture.Body.ToArray(), unrelatedIdr) >= 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsGreaterThanOrEqualTo(0, IndexOf(capture.Body.ToArray(), unrelatedIdr),
            "An unrelated timeline must resume at the first IDR like today's recovery.");
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_AudioOnlyRecoveredStream_ResumesWithoutTrim()
    {
        // A recovered stream with no H.264 video cannot be DTS-compared; the PAT/PMT
        // safe-start path applies untouched.
        var preFailureSequence = new[]
        {
            PatPacket(100),
            PmtPacket(100, 256),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x91], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x92], 100L * 90000),
            TimestampedVideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x93], 102L * 90000),
        };

        var recoveredSequence = new[]
        {
            PatPacket(100),
            AudioOnlyPmtPacket(100),
        };

        var filler = FakeStreamingHandler.ValidTsPacket(0xCC);
        var handler = FakeStreamingHandler.StreamForever(filler);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(preFailureSequence, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(recoveredSequence, filler, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(10));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimmed));
        Assert.IsEmpty(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId, kind: StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_ZeroSubscribersAfterFirstSafeStart_SessionSurvivesAndEmitsSecondSafeStart()
    {
        // Reproduces the exact Bug 1 / TS-SAFE-02 trigger:
        //   1. First connection streams the safe-start sequence then stalls.
        //   2. Initial subscriber attaches, receives bytes until safe-start fires, then detaches
        //      — leaving the session with zero external subscribers (idle-grace clock starts).
        //   3. Stall is detected; session reconnects (0 s backoff) while idle-grace is still counting.
        //   4. Second connection delivers a fresh safe-start sequence.
        //   5. Session must survive until the second safe-start fires (idle-grace must NOT win).
        //   6. A late subscriber attaching after reconnect must receive 0x47-aligned bytes.
        //
        // Previously this scenario killed the session during the FFmpeg reconnect path because
        // Bug 2B caused Cautious→FfmpegCleanRemux, which blocked ConnectAsync for up to 10 s
        // while idle-grace (15 s) expired. With Cautious→Direct the reconnect is near-instant
        // and the session survives well within the idle-grace window.
        var safeSequence = MpegTsSafeStartupSequence();

        // Connection 2+ (default): cycle through the safe-start sequence forever so the
        // second connection establishes a safe start immediately.
        // Connection 1 (queued): emit the safe-start sequence once then stall so the
        // stall timer fires and the session reconnects.
        var handler = FakeStreamingHandler.StreamForeverSequence(safeSequence);
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenStall(safeSequence, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                // Long enough that the near-instant direct reconnect wins comfortably.
                IdleGrace = TimeSpan.FromSeconds(4),
            },
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(300),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initialSubscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), initCts.Token);

        // Wait for the first safe-start, then drop the subscriber — zero external subscribers,
        // idle-grace begins.
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count > 0,
            TimeSpan.FromSeconds(8));

        initCts.Cancel();
        await initialSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await initialSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        // Wait for the reconnect to recover AND a second safe-start to be selected.
        // If idle-grace fires first this will time out — that is the regression.
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.ReconnectRecovered).Count > 0
               && fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count >= 2,
            TimeSpan.FromSeconds(8));

        var safeStartCount = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count;
        var reconnectCount = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.ReconnectRecovered).Count;

        Assert.IsGreaterThanOrEqualTo(2, safeStartCount,
            "A second MpegTsSafeStartSelected must fire after the reconnect — idle-grace must not kill the session first.");
        Assert.IsGreaterThanOrEqualTo(1, reconnectCount,
            "ReconnectRecovered must be emitted after the stall.");

        // Late subscriber must receive TS-aligned data from the current generation.
        var lateContext = CreateResponseCaptureContext();
        using var lateCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lateSubscriber = await session.AttachSubscriberAsync(lateContext.Context, lateCts.Token);
        await WaitUntilAsync(() => lateSubscriber.BytesSent >= 188, TimeSpan.FromSeconds(5));

        lateCts.Cancel();
        await lateSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await lateSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = lateContext.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188, data.Length,
            "Late subscriber must receive at least one full TS packet after reconnect.");
        Assert.AreEqual(0x47, data[0],
            "First byte must be a TS sync byte — late subscriber must attach from a safe-start point.");
        Assert.AreEqual(0, data.Length % 188,
            "Late subscriber must receive whole TS packets only.");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_AutoRelayWithCautiousEscalation_UsesDirectNotCleanRemux()
    {
        // Pins the load-bearing relay-mode decision that fixed Bug 1 / TS-SAFE-02:
        // when a session's first upstream fails (in-session escalation: Stable→Cautious),
        // Auto relay must select Direct, not FfmpegCleanRemux.
        //
        // If Cautious incorrectly maps to FfmpegCleanRemux (the pre-fix Bug 2B state),
        // ConnectAsync blocks waiting for FFmpeg startup output. With no FFmpeg configured
        // this falls back to direct but records FfmpegRelayFallbackToDirect. With a real
        // FFmpeg path on a stalling provider the block can outlast idle-grace, killing the
        // session before the reconnect delivers any data — the Bug 1 root cause.
        //
        // After the fix Cautious→Direct is selected by policy: no FFmpeg is attempted,
        // no FfmpegRelayFallbackToDirect event is emitted, and RelayMode is Direct.
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.StreamForever(chunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 3, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            cleanRelayMode: "auto",
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(300),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        // Wait for the reconnect to complete (upstream failure → reconnect → recovered).
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.ReconnectRecovered).Count > 0,
            TimeSpan.FromSeconds(8));

        // Cautious in-session escalation must not route the reconnect through FFmpeg clean remux.
        var fallbackEvents = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.FfmpegRelayFallbackToDirect);
        var ffmpegStartedEvents = fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.FfmpegRelayStarted);

        Assert.IsFalse(fallbackEvents.Any(),
            "FfmpegRelayFallbackToDirect must not be emitted: Cautious Auto must select Direct by policy, not by FFmpeg fallback.");
        Assert.IsFalse(ffmpegStartedEvents.Any(),
            "FfmpegRelayStarted must not be emitted for a Cautious Auto channel — only Unstable triggers clean remux.");

        // Direct HTTP must be confirmed as the relay path after reconnect.
        var snapshot = fixture.Registry.TryGetSession(session.SessionId);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(UpstreamRelayModes.Direct, snapshot.RelayMode,
            "Relay mode after reconnect must be Direct for a Cautious Auto channel.");
        Assert.IsGreaterThan(0, handler.ConnectionCount,
            "Direct HTTP upstream must have been opened.");

        cts.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_UnsafeRecoveryForcesControlledClose()
    {
        var handler = FakeStreamingHandler.StreamForever(FakeStreamingHandler.ValidTsPacket(0xDD));
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(FakeStreamingHandler.ValidTsPacket(0xA1), 3, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 4 * 188,
                AllowPacketBoundaryRecoveryFallback = false,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryForcedRetune).Count > 0,
            TimeSpan.FromSeconds(8));
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(SessionState.Faulted, session.State);
        Assert.IsTrue(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.RecoveryFailedUnsafe).Any());
        Assert.IsTrue(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.SubscriberRemoved).Any(x =>
                x.DisconnectReason == SubscriberDisconnectReason.SessionClosed));

        var data = capture.Body.ToArray();
        Assert.AreEqual(188 * 3, data.Length, "Only pre-reconnect bytes should reach the existing subscriber.");
        Assert.AreEqual(-1, IndexOf(data, FakeStreamingHandler.ValidTsPacket(0xDD)));
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_UnstableChannelNoSafeStart_FailsRecoveryInsteadOfPacketBoundaryFallback()
    {
        // Issue #127: the eager ControlledDownstreamRetune mechanism was removed — it fired
        // unconditionally on Unstable classification alone, before any safe-start scan even
        // ran, and was structurally dead anyway (Unstable always selects clean remux, which
        // always has an internal subscriber, which always suppressed it). The reactive,
        // evidence-based path (FailRecoveryOutputAsync) already covers this: packet-boundary
        // fallback disallowed + no valid safe start ever found -> recovery genuinely fails.
        var handler = FakeStreamingHandler.StreamForever(FakeStreamingHandler.ValidTsPacket(0xDD));
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(FakeStreamingHandler.ValidTsPacket(0xA1), 3, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            bufferOptions: new BufferOptions
            {
                ReadChunkSizeBytes = 188,
                SubscriberQueueCapacity = 128,
                MaxBytesPerSession = 16 * 188,
                MaxBytesHardCap = 4 * 1024 * 1024,
            },
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 4 * 188,
                AllowPacketBoundaryRecoveryFallback = true,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });
        await fixture.SeedHealthEventsAsync(
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "MpegTsSyncLost",
                EventUtc = DateTime.UtcNow.AddMinutes(-10),
                TsSyncLoss = true,
            },
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "MpegTsSyncLost",
                EventUtc = DateTime.UtcNow.AddMinutes(-5),
                TsSyncLoss = true,
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryForcedRetune).Count > 0,
            TimeSpan.FromSeconds(8));

        Assert.AreEqual(SessionState.Faulted, session.State);
        Assert.IsFalse(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Any(x =>
                string.Equals(x.SafeStartKind, "FallbackPacketBoundary", StringComparison.Ordinal)));
        Assert.IsTrue(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.RecoveryForcedRetune).Any());
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_UnstableChannelWithValidRecovery_ResumesWithoutDisconnectingSubscribers()
    {
        // Issue #127: an Unstable channel whose reconnect finds a genuine safe start must
        // resume normally, not force every subscriber off. This holds regardless of whether
        // an internal FFmpeg-relay subscriber is also attached — there is no suppression
        // logic left to depend on, since the eager retune mechanism was removed entirely.
        var preReconnectPacket = FakeStreamingHandler.ValidTsPacket(0xA1);
        var safeChunk = FakeStreamingHandler.ValidTsPacket(0xCC);
        var recoveredSequence = MpegTsSafeStartupSequence();
        var handler = FakeStreamingHandler.StreamForever(safeChunk);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(preReconnectPacket, 3, ct));
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(recoveredSequence, safeChunk, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            bufferOptions: new BufferOptions
            {
                ReadChunkSizeBytes = 188,
                SubscriberQueueCapacity = 128,
                MaxBytesPerSession = 64 * 188,
                MaxBytesHardCap = 4 * 1024 * 1024,
            },
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(2),
                RecoverySafeStartSearchLimitBytes = 64 * 188,
                AllowPacketBoundaryRecoveryFallback = true,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });
        await fixture.SeedHealthEventsAsync(
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "RecoveryOutputResumed",
                EventUtc = DateTime.UtcNow.AddMinutes(-15),
                SafeStartKind = "H264Idr",
            },
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "MpegTsSyncLost",
                EventUtc = DateTime.UtcNow.AddMinutes(-10),
                TsSyncLoss = true,
            },
            new StreamChannelHealthEvent
            {
                StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
                ProviderId = "provider-1",
                ProviderChannelId = "channel-1",
                DisplayName = "Test Channel",
                EventKind = "MpegTsSyncLost",
                EventUtc = DateTime.UtcNow.AddMinutes(-5),
                TsSyncLoss = true,
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var externalCapture = CreateResponseCaptureContext();
        var internalCapture = CreateResponseCaptureContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var externalSubscriber = await session.AttachSubscriberAsync(externalCapture.Context, cts.Token);
        var internalSubscriber = await session.AttachSubscriberAsync(internalCapture.Context, cts.Token, isInternal: true);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(8));

        Assert.AreEqual(SessionState.Live, session.State);
        Assert.IsTrue(fixture.Manager.TryGet(session.Key, out var activeSession));
        Assert.AreSame(session, activeSession);
        Assert.IsFalse(externalSubscriber.IsCompleted);
        Assert.IsFalse(internalSubscriber.IsCompleted);
        Assert.IsFalse(fixture.DiagnosticsStore.Query(
            sessionId: session.SessionId,
            kind: StreamDiagnosticEventKind.ControlledDownstreamRetune).Any());

        await externalSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await internalSubscriber.CompleteAsync(SubscriberDisconnectReason.SessionClosed);
    }

    [TestMethod]
    public async Task Session_MpegTsReconnect_NewExternalSubscriberDuringRecovery_DoesNotReceiveStalePreReconnectData()
    {
        // A new external subscriber that attaches while the session is holding output
        // must receive data from the live edge (post-reconnect safe start), not stale
        // pre-reconnect bytes still in the ring buffer.
        var preReconnectPacket = FakeStreamingHandler.ValidTsPacket(0xA1);
        var unsafeRecoveredPacket = FakeStreamingHandler.ValidTsPacket(0xDD);
        var safeChunk = FakeStreamingHandler.ValidTsPacket(0xCC);
        // Recovery sequence: one unsafe prefix packet followed by the full safe-start
        // sequence.  After the safe-start is confirmed the session streams safeChunk
        // forever — 0xDD never reappears so the assertion below is deterministic.
        var recoveredSequence = new[] { unsafeRecoveredPacket }
            .Concat(MpegTsSafeStartupSequence())
            .ToArray();
        var handler = FakeStreamingHandler.StreamForever(safeChunk); // default (connection 3+)
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(preReconnectPacket, 3, ct));           // connection 1
        handler.QueueNext(ct => FakeStreamingHandler.WriteSequenceThenForever(recoveredSequence, safeChunk, ct)); // connection 2

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromMilliseconds(200),
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(3),
                RecoverySafeStartSearchLimitBytes = 16 * 188,
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);

        // First subscriber attaches before the stall.
        var firstCapture = CreateResponseCaptureContext();
        using var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var firstSubscriber = await session.AttachSubscriberAsync(firstCapture.Context, firstCts.Token);
        await WaitUntilAsync(() => firstSubscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        // Wait for the session to enter HoldingOutput state (reconnect + hold active).
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryStarted).Count > 0,
            TimeSpan.FromSeconds(8));

        // Late external subscriber attaches during HoldingOutput.
        var lateCapture = CreateResponseCaptureContext();
        using var lateCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var lateSubscriber = await session.AttachSubscriberAsync(lateCapture.Context, lateCts.Token);

        // Wait for recovery to resume so both subscribers receive data.
        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.RecoveryOutputResumed).Count > 0,
            TimeSpan.FromSeconds(8));
        await WaitUntilAsync(() => lateSubscriber.BytesSent >= 188, TimeSpan.FromSeconds(5));

        firstCts.Cancel();
        lateCts.Cancel();
        await firstSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await lateSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await firstSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await lateSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var lateData = lateCapture.Body.ToArray();

        Assert.IsGreaterThanOrEqualTo(188, lateData.Length, "Late subscriber must have received at least one packet.");
        Assert.AreEqual(0x47, lateData[0], "Late subscriber data must begin with a TS sync byte.");
        Assert.AreEqual(-1, IndexOf(lateData, preReconnectPacket),
            "Late subscriber must not receive pre-reconnect bytes.");
        Assert.AreEqual(-1, IndexOf(lateData, unsafeRecoveredPacket),
            "Late subscriber must not receive unsafe post-reconnect bytes suppressed during hold.");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTs_PatPmtOnlyStream_SelectsPatPmtSafeStart()
    {
        // Stream has PAT + PMT but no H.264 video (audio-only). Safe start should be
        // selected at the PatPmt boundary without waiting for an IDR.
        var patPmtSequence = new[]
        {
            PatPacket(100),
            AudioOnlyPmtPacket(100),
            FakeStreamingHandler.ValidTsPacket(0xBB),
            FakeStreamingHandler.ValidTsPacket(0xCC),
        };
        var handler = FakeStreamingHandler.StreamForeverSequence(patPmtSequence);

        await using var fixture = await SessionFixture.CreateAsync(handler);
        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected).Count > 0,
            TimeSpan.FromSeconds(5));

        var safeStartEvent = fixture.DiagnosticsStore
            .Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.MpegTsSafeStartSelected)
            .First();
        Assert.IsNotNull(safeStartEvent.Message);
        StringAssert.Contains(safeStartEvent.Message, "PatPmt");

        var lateContext = CreateResponseCaptureContext();
        var lateSubscriber = await session.AttachSubscriberAsync(lateContext.Context, cts.Token, isInternal: true);
        await WaitUntilAsync(() => lateSubscriber.BytesSent >= 188, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await lateSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await lateSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = lateContext.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188, data.Length);
        Assert.AreEqual(0, data.Length % 188, "Late subscriber must receive whole TS packets only.");
        Assert.AreEqual(0x47, data[0], "First byte must be a TS sync byte.");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsPacketizerDisabled_FallsBackToPassThrough()
    {
        // Content type claims video/MP2T but the data contains no 0x47 sync bytes.
        // After the probe window the packetizer should disable itself and relay the raw bytes.
        var nonTsChunk = new byte[100];
        Array.Fill(nonTsChunk, (byte)0x11);
        var handler = FakeStreamingHandler.StreamForever(nonTsChunk);

        await using var fixture = await SessionFixture.CreateAsync(handler);
        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var capture = CreateResponseCaptureContext();
        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(
                sessionId: session.SessionId,
                kind: StreamDiagnosticEventKind.MpegTsPacketizerDisabled).Count > 0,
            TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.IsGreaterThan(0, data.Length, "Pass-through data should reach subscriber after packetizer is disabled.");
        Assert.IsTrue(data.All(b => b == 0x11), "Pass-through bytes must be exactly the raw upstream content.");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsKeepalive_ExternalNonHdhrSubscriberReceivesNullPacketDuringStall()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.ServiceUnavailable);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 1, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(10),
                ContentStallTimeout = TimeSpan.FromSeconds(3),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        capture.Context.Request.Path = "/live/key-1";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);

        await WaitUntilAsync(() => subscriber.BytesSent >= 188 * 2, TimeSpan.FromSeconds(2));

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = capture.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188 * 2, data.Length);
        Assert.AreEqual(1, handler.ConnectionCount, "Keepalive must not reconnect the upstream socket.");
        Assert.AreEqual(0x47, data[188]);
        Assert.AreEqual(0x1f, data[189]);
        Assert.AreEqual(0xff, data[190]);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsKeepalive_HdhrOnlySubscriberDoesNotReceiveNullPackets()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.ServiceUnavailable);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 1, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(10),
                ContentStallTimeout = TimeSpan.FromSeconds(3),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var source = fixture.Source with { RequestedRoute = "/hdhr/tune/key-1" };
        var session = await fixture.Manager.GetOrCreateAsync(source, CancellationToken.None);
        var capture = CreateResponseCaptureContext();
        capture.Context.Request.Path = "/hdhr/tune/key-1";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriber = await session.AttachSubscriberAsync(capture.Context, cts.Token);
        await WaitUntilAsync(() => subscriber.BytesSent >= 188, TimeSpan.FromSeconds(5));
        await Task.Delay(1100, CancellationToken.None);

        Assert.AreEqual(188, subscriber.BytesSent);
        Assert.AreEqual(1, handler.ConnectionCount);

        cts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsKeepalive_NonHdhrJoinerOnHdhrOpenedSessionReceivesNullPacket()
    {
        var chunk = FakeStreamingHandler.ValidTsPacket();
        var handler = FakeStreamingHandler.ReturnStatus(HttpStatusCode.ServiceUnavailable);
        handler.QueueNext(ct => FakeStreamingHandler.WriteNChunksThenStall(chunk, 1, ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(10),
                ContentStallTimeout = TimeSpan.FromSeconds(3),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var source = fixture.Source with { RequestedRoute = "/hdhr/tune/key-1" };
        var session = await fixture.Manager.GetOrCreateAsync(source, CancellationToken.None);
        var hdhrCapture = CreateResponseCaptureContext();
        hdhrCapture.Context.Request.Path = "/hdhr/tune/key-1";
        var smartersCapture = CreateResponseCaptureContext();
        smartersCapture.Context.Request.Path = "/live/key-1";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var hdhrSubscriber = await session.AttachSubscriberAsync(hdhrCapture.Context, cts.Token);
        await WaitUntilAsync(() => hdhrSubscriber.BytesSent >= 188, TimeSpan.FromSeconds(2));

        var smartersSubscriber = await session.AttachSubscriberAsync(smartersCapture.Context, cts.Token);
        await WaitUntilAsync(() => smartersSubscriber.BytesSent >= 188 * 2, TimeSpan.FromSeconds(3));

        cts.Cancel();
        await smartersSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await hdhrSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await smartersSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await hdhrSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var data = smartersCapture.Body.ToArray();
        Assert.IsGreaterThanOrEqualTo(188 * 2, data.Length);
        Assert.IsTrue(ContainsNullTsPacket(data), "Smarters subscriber must receive at least one MPEG-TS null packet during upstream stall.");
        Assert.AreEqual(1, handler.ConnectionCount);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MpegTsNullOnlyUpstreamData_DoesNotResetContentStallTimer()
    {
        var handler = FakeStreamingHandler.StreamForever(FakeStreamingHandler.ValidTsPacket());
        handler.QueueNext(ct => FakeStreamingHandler.StreamForeverResponse(NullTsPacket(), ct));

        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(10),
                ContentStallTimeout = TimeSpan.FromMilliseconds(500),
                OutageWindow = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.ReconnectRecovered).Count > 0,
            TimeSpan.FromSeconds(5));

        Assert.IsGreaterThanOrEqualTo(2, handler.ConnectionCount);

        cts.Cancel();
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_InternalSubscriber_DoesNotBlockIdleGrace()
    {
        // When only an internal subscriber remains, idle grace fires as if there are no subscribers.
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            proxyOptions: new StreamProxyOptions { StreamingEnabled = true, IdleGrace = TimeSpan.FromMilliseconds(300) });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var requestCts = new CancellationTokenSource();

        var internalSubscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), requestCts.Token, isInternal: true);
        await WaitUntilAsync(() => internalSubscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        // Session must NOT have shut down yet — idle grace only starts once there are zero external subscribers.
        Assert.IsTrue(fixture.Manager.TryGet(session.Key, out _));

        // Disconnect the internal subscriber — idle grace should now fire and close the session.
        requestCts.Cancel();
        await internalSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await internalSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => !fixture.Manager.TryGet(session.Key, out _), TimeSpan.FromSeconds(5));
        Assert.IsFalse(fixture.Manager.TryGet(session.Key, out _));
    }

    [TestMethod]
    public async Task Session_SubscriberDisconnect_EmitsDisconnectReason()
    {
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(handler);

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var requestCts = new CancellationTokenSource();
        var subscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), requestCts.Token);

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));
        requestCts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);

        await WaitUntilAsync(
            () => fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.SubscriberRemoved).Count > 0,
            TimeSpan.FromSeconds(2));

        var removed = fixture.DiagnosticsStore.Query(sessionId: session.SessionId, kind: StreamDiagnosticEventKind.SubscriberRemoved);
        Assert.IsTrue(removed.Any(x => x.DisconnectReason == SubscriberDisconnectReason.ClientAborted));

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ExternalAndInternalSubscribers_ExternalCountCorrect()
    {
        // ExternalSubscriberCount excludes internal subscribers; idle grace waits for external count to reach zero.
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            proxyOptions: new StreamProxyOptions { StreamingEnabled = true, IdleGrace = TimeSpan.FromMilliseconds(300) });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var externalCts = new CancellationTokenSource();
        var internalCts = new CancellationTokenSource();

        var externalSub = await session.AttachSubscriberAsync(new DefaultHttpContext(), externalCts.Token);
        var internalSub = await session.AttachSubscriberAsync(new DefaultHttpContext(), internalCts.Token, isInternal: true);

        Assert.AreEqual(2, session.SubscriberCount);
        Assert.AreEqual(1, session.ExternalSubscriberCount);

        // Drop the external subscriber — idle grace should eventually fire because ExternalSubscriberCount drops to 0.
        externalCts.Cancel();
        await externalSub.CompleteAsync(SubscriberDisconnectReason.ClientAborted);

        await WaitUntilAsync(() => !fixture.Manager.TryGet(session.Key, out _), TimeSpan.FromSeconds(5));
        Assert.IsFalse(fixture.Manager.TryGet(session.Key, out _));

        internalCts.Cancel();
    }

    [TestMethod]
    public async Task GeneratedHlsRelaySession_KeepsParentAliveAfterExternalSubscriberDisconnects()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "write.flag"), string.Empty);

        try
        {
            var handler = FakeStreamingHandler.StreamForever();
            await using var fixture = await SessionFixture.CreateAsync(
                handler,
                proxyOptions: new StreamProxyOptions { StreamingEnabled = true, IdleGrace = TimeSpan.FromMilliseconds(200) });
            await using var generatedHlsManager = CreateGeneratedHlsManager(root, fixture.Manager, fixture.Registry);

            await generatedHlsManager.StartAsync(CancellationToken.None);
            Assert.IsTrue(generatedHlsManager.IsEffectivelyEnabled);

            var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
            using var externalCts = new CancellationTokenSource();
            using var internalCts = new CancellationTokenSource();
            var externalSub = await session.AttachSubscriberAsync(new DefaultHttpContext(), externalCts.Token);
            var internalSub = await session.AttachSubscriberAsync(new DefaultHttpContext(), internalCts.Token, isInternal: true);

            await WaitUntilAsync(
                () => externalSub.BytesSent > 0 && internalSub.BytesSent > 0,
                TimeSpan.FromSeconds(5));

            var handle = await generatedHlsManager.CreateSessionAsync(
                new GeneratedHlsSessionRequest(
                    StreamUrl: "http://127.0.0.1/internal/relay/provider-1/channel-1",
                    DisplayName: fixture.Source.DisplayName,
                    AdmissionKey: fixture.Source.SessionKey,
                    InternalRelaySecret: "test-secret",
                    ParentStreamSessionId: session.SessionId,
                    RequestedRoute: fixture.Source.RequestedRoute),
                CancellationToken.None);

            Assert.IsNotNull(handle);

            externalCts.Cancel();
            await externalSub.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
            await externalSub.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            await Task.Delay(350);
            Assert.IsTrue(
                fixture.Manager.TryGet(session.Key, out _),
                "Relay-backed generated HLS must retain the parent shared stream while its HLS session is active.");

            await generatedHlsManager.StopAsync(CancellationToken.None);

            await WaitUntilAsync(() => !fixture.Manager.TryGet(session.Key, out _), TimeSpan.FromSeconds(5));
            Assert.IsFalse(fixture.Manager.TryGet(session.Key, out _));

            internalCts.Cancel();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Manager_GeneratedHlsParentResolution_CreatesSharedParentWhenMissing()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var session = await fixture.Manager.TryGetOrCreateForGeneratedHlsAsync(
            fixture.Source,
            useSharedSession: true,
            CancellationToken.None);

        Assert.IsNotNull(session);
        Assert.AreEqual(fixture.Source.SessionKey, session.Key);
        Assert.IsTrue(fixture.Manager.TryGet(fixture.Source.SessionKey, out var activeSession));
        Assert.AreSame(session, activeSession);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task Manager_GeneratedHlsParentResolution_NonSharedRequestDoesNotCreateParent()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var session = await fixture.Manager.TryGetOrCreateForGeneratedHlsAsync(
            fixture.Source,
            useSharedSession: false,
            CancellationToken.None);

        Assert.IsNull(session);
        Assert.IsFalse(fixture.Manager.TryGet(fixture.Source.SessionKey, out _));
    }

    [TestMethod]
    public async Task Session_ProviderTunerLimit_RejectsAdditionalProviderSession()
    {
        // Checklist: Provider stream limits still reject new sessions correctly (per-provider TunerLimit path).
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        // First session — no TunerLimit set, creates fine
        var firstSession = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        Assert.IsNotNull(firstSession);

        // Second source: same provider, different channel, TunerLimit = 1
        // The manager sees 1 existing session for provider-1, cap is 1 → rejected
        var secondSource = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 1,
        };

        try
        {
            await fixture.Manager.GetOrCreateAsync(secondSource, CancellationToken.None);
            Assert.Fail("Expected StreamAdmissionException when provider tuner limit is reached.");
        }
        catch (StreamAdmissionException ex)
        {
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
            StringAssert.Contains(ex.Message, "Provider upstream limit");
        }

        await firstSession.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ProviderTunerLimit_ReusesRetryWindowAcrossRepeatedRejections()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 03, 31, 12, 00, 00, TimeSpan.Zero));
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            timeProvider: timeProvider);

        var firstSession = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var blockedSource = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 1,
        };

        var firstReject = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(blockedSource, CancellationToken.None).AsTask());
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, firstReject.FailureKind);
        Assert.AreEqual(30, firstReject.RetryAfterSeconds);

        timeProvider.Advance(TimeSpan.FromSeconds(12));

        var secondReject = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(blockedSource, CancellationToken.None).AsTask());
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, secondReject.FailureKind);
        Assert.AreEqual(18, secondReject.RetryAfterSeconds);

        timeProvider.Advance(TimeSpan.FromSeconds(18));

        var thirdReject = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(blockedSource, CancellationToken.None).AsTask());
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, thirdReject.FailureKind);
        Assert.AreEqual(30, thirdReject.RetryAfterSeconds);

        await firstSession.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ProviderTunerLimit_PreemptsSameIpIdleGraceSessionBeforeRetune()
    {
        // A zero-subscriber idle-grace session must not count against the provider cap.
        // Same-IP retuning to a different channel is admitted after the tracked idle session is preempted.
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                IdleGrace = TimeSpan.FromSeconds(10),
                ProviderMaxConcurrentUpstreams = 1,
            });

        var source1 = fixture.Source with
        {
            TunerLimit = 1,
            RemoteIp = "10.0.0.10",
            UserAgent = "test-client",
        };
        var source2 = source1 with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
        };

        var session1 = await fixture.Manager.GetOrCreateAsync(source1, CancellationToken.None);
        using var requestCts = new CancellationTokenSource();
        var subscriber = await session1.AttachSubscriberAsync(CreateHttpContext(source1.RemoteIp), requestCts.Token);

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        requestCts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.Retuned);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        // session1 is idle-grace with zero consumers — must not block the same IP from retuning.
        var session2 = await fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None);

        Assert.AreEqual(source2.SessionKey, session2.Key);
        Assert.IsFalse(fixture.Manager.TryGet(source1.SessionKey, out _));

        await session1.DisposeAsync();
        await session2.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ProviderTunerLimit_PreemptsDifferentIpIdleGraceSessionBeforeAdmittingNewTune()
    {
        // A zero-subscriber idle-grace session must not count against the provider cap.
        // A different remote IP requesting a different channel is admitted after provider-scoped idle preemption.
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                IdleGrace = TimeSpan.FromSeconds(10),
                ProviderMaxConcurrentUpstreams = 1,
            });

        var source1 = fixture.Source with
        {
            TunerLimit = 1,
            RemoteIp = "10.0.0.10",
            UserAgent = "test-client",
        };
        var source2 = source1 with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            RemoteIp = "10.0.0.11",
        };

        var session1 = await fixture.Manager.GetOrCreateAsync(source1, CancellationToken.None);
        using var requestCts = new CancellationTokenSource();
        var subscriber = await session1.AttachSubscriberAsync(CreateHttpContext(source1.RemoteIp), requestCts.Token);

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        requestCts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.Retuned);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        // session1 is now idle-grace with zero consumers — must not block source2 admission.
        var session2 = await fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None);

        Assert.AreEqual(source2.SessionKey, session2.Key);
        Assert.IsFalse(fixture.Manager.TryGet(source1.SessionKey, out _));

        await session1.DisposeAsync();
        await session2.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ProviderTunerLimit_IdleGraceWithSubscribersStillBlocksAdmission()
    {
        // A session with an active internal subscriber in idle-grace should still count
        // against the provider cap — only zero-consumer sessions are exempt.
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                IdleGrace = TimeSpan.FromSeconds(10),
                ProviderMaxConcurrentUpstreams = 1,
            });

        var source1 = fixture.Source with
        {
            TunerLimit = 1,
            RemoteIp = "10.0.0.10",
            UserAgent = "test-client",
        };
        var source2 = source1 with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            RemoteIp = "10.0.0.11",
        };

        var session1 = await fixture.Manager.GetOrCreateAsync(source1, CancellationToken.None);
        using var requestCts = new CancellationTokenSource();
        var subscriber = await session1.AttachSubscriberAsync(CreateHttpContext(source1.RemoteIp), requestCts.Token);

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        // source2 is blocked because source1 still has an active external subscriber.
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None).AsTask());

        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);

        requestCts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.Retuned);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await session1.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ProviderTunerLimit_AllowsThirdChannelWhileSecondIsIdleGraceWithNoConsumers()
    {
        // Regression: provider max=2, channel-1 active (1 real upstream), channel-2 enters
        // zero-subscriber idle grace (was incorrectly counted as the 2nd upstream), channel-3
        // must be admitted — not rejected — during channel-2's idle grace window.
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                IdleGrace = TimeSpan.FromSeconds(15),
                ProviderMaxConcurrentUpstreams = 2,
            });

        var sourceAbc = fixture.Source with
        {
            TunerLimit = 2,
            RemoteIp = "10.0.0.10",
            ProviderChannelId = "abc",
            StreamUrl = "http://fake/abc",
            DisplayName = "ABC",
            RequestedRoute = "/live/abc",
        };
        var sourceCbs = sourceAbc with
        {
            ProviderChannelId = "cbs",
            StreamUrl = "http://fake/cbs",
            DisplayName = "CBS",
            RequestedRoute = "/live/cbs",
            RemoteIp = "10.0.0.20",
        };
        var sourceFox = sourceAbc with
        {
            ProviderChannelId = "fox",
            StreamUrl = "http://fake/fox",
            DisplayName = "FOX",
            RequestedRoute = "/live/fox",
            RemoteIp = "10.0.0.30",
        };

        // ABC: active with a real subscriber (counts as upstream 1).
        var abcSession = await fixture.Manager.GetOrCreateAsync(sourceAbc, CancellationToken.None);
        using var abcCts = new CancellationTokenSource();
        var abcSubscriber = await abcSession.AttachSubscriberAsync(CreateHttpContext(sourceAbc.RemoteIp), abcCts.Token);
        await WaitUntilAsync(() => abcSubscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        // CBS: open then immediately abandon — enters idle grace with zero subscribers.
        var cbsSession = await fixture.Manager.GetOrCreateAsync(sourceCbs, CancellationToken.None);
        using var cbsCts = new CancellationTokenSource();
        var cbsSubscriber = await cbsSession.AttachSubscriberAsync(CreateHttpContext(sourceCbs.RemoteIp), cbsCts.Token);
        await WaitUntilAsync(() => cbsSubscriber.BytesSent > 0, TimeSpan.FromSeconds(5));
        cbsCts.Cancel();
        await cbsSubscriber.CompleteAsync(SubscriberDisconnectReason.Retuned);
        await cbsSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        // CBS is now idle-grace with zero consumers — must not count as the 2nd upstream.
        // FOX must be admitted as the real 2nd upstream after CBS is preempted from tracked capacity.
        var foxSession = await fixture.Manager.GetOrCreateAsync(sourceFox, CancellationToken.None);
        Assert.AreEqual(sourceFox.SessionKey, foxSession.Key);
        Assert.IsFalse(fixture.Manager.TryGet(sourceCbs.SessionKey, out _));

        abcCts.Cancel();
        await abcSubscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await abcSubscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await abcSession.DisposeAsync();
        await cbsSession.DisposeAsync();
        await foxSession.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MaxConcurrentSessions_PreemptsIdleGraceSessionBeforeAdmittingReplacement()
    {
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                MaxConcurrentSessions = 1,
                IdleGrace = TimeSpan.FromSeconds(10),
            });

        var source1 = fixture.Source with
        {
            RemoteIp = "10.0.0.10",
            ProviderChannelId = "channel-1",
            StreamUrl = "http://fake/channel-1",
            RequestedRoute = "/live/key-1",
        };
        var source2 = source1 with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/channel-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
        };

        var session1 = await fixture.Manager.GetOrCreateAsync(source1, CancellationToken.None);
        using var requestCts = new CancellationTokenSource();
        var subscriber = await session1.AttachSubscriberAsync(CreateHttpContext(source1.RemoteIp), requestCts.Token);
        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        requestCts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.Retuned);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var session2 = await fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None);

        Assert.AreEqual(source2.SessionKey, session2.Key);
        Assert.IsFalse(fixture.Manager.TryGet(source1.SessionKey, out _));

        await session1.DisposeAsync();
        await session2.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_MaxConcurrentSessions_RejectsAdditionalSession()
    {
        // Checklist: Provider stream limits still reject new sessions correctly.
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                MaxConcurrentSessions = 1,
                IdleGrace = TimeSpan.FromSeconds(10),
            });

        var firstSession = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        Assert.IsNotNull(firstSession);

        var secondSource = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
        };

        try
        {
            await fixture.Manager.GetOrCreateAsync(secondSource, CancellationToken.None);
            Assert.Fail("Expected StreamAdmissionException when max concurrent sessions is reached.");
        }
        catch (StreamAdmissionException ex)
        {
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
            StringAssert.Contains(ex.Message, "Max concurrent sessions");
        }
    }

    [TestMethod]
    public async Task CheckAdmission_ThirdUniqueChannel_RejectedWhenProviderCapReached()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        // Fill the provider cap with two active sessions (TunerLimit = 2).
        var source1 = fixture.Source with { TunerLimit = 2 };
        var source2 = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 2,
        };

        await fixture.Manager.GetOrCreateAsync(source1, CancellationToken.None);
        await fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None);

        // A third unique channel from the same provider must be rejected.
        var source3 = fixture.Source with
        {
            ProviderChannelId = "channel-3",
            StreamUrl = "http://fake/stream-3",
            DisplayName = "Test Channel 3",
            RequestedRoute = "/live/key-3",
            TunerLimit = 2,
        };

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.CheckAdmission(source3)));

        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
    }

    [TestMethod]
    public async Task CheckAdmission_ExistingActiveChannel_AllowedRegardlessOfCap()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        // Fill the provider cap with two active sessions (TunerLimit = 2).
        var source1 = fixture.Source with { TunerLimit = 2 };
        var source2 = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 2,
        };

        await fixture.Manager.GetOrCreateAsync(source1, CancellationToken.None);
        await fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None);

        // Checking admission for a channel that is already active must not throw —
        // it would join the existing session rather than open a new upstream.
        fixture.Manager.CheckAdmission(source1);
    }

    [TestMethod]
    public async Task ReserveHlsSlot_CountsTowardProviderCap()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source1 = fixture.Source with { TunerLimit = 2 };
        var source2 = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 2,
        };
        var source3 = fixture.Source with
        {
            ProviderChannelId = "channel-3",
            StreamUrl = "http://fake/stream-3",
            DisplayName = "Test Channel 3",
            RequestedRoute = "/live/key-3",
            TunerLimit = 2,
        };

        using var slot1 = fixture.Manager.ReserveHlsSlot(source1);
        using var slot2 = fixture.Manager.ReserveHlsSlot(source2);

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.ReserveHlsSlot(source3)));
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task ReserveHlsSlot_CountsTowardMaxConcurrentSessions()
    {
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                MaxConcurrentSessions = 1,
                IdleGrace = TimeSpan.FromSeconds(10),
            });

        using var slot1 = fixture.Manager.ReserveHlsSlot(fixture.Source);

        var secondSource = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
        };

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.ReserveHlsSlot(secondSource)));
        Assert.AreEqual(StreamAdmissionFailureKind.MaxConcurrentSessions, ex.FailureKind);
    }

    [TestMethod]
    public async Task ReserveHlsSlot_SameChannelAsActiveSession_Allowed()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source = fixture.Source with { TunerLimit = 1 };
        await fixture.Manager.GetOrCreateAsync(source, CancellationToken.None);

        // Reserving an HLS slot for the same channel joins the existing upstream — allowed.
        using var slot = fixture.Manager.ReserveHlsSlot(source);
        Assert.IsNotNull(slot);
    }

    [TestMethod]
    public async Task ReserveHlsSlot_SameChannelAsExistingSlot_Allowed()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source = fixture.Source with { TunerLimit = 1 };
        using var slot1 = fixture.Manager.ReserveHlsSlot(source);
        using var slot2 = fixture.Manager.ReserveHlsSlot(source);
        Assert.IsNotNull(slot2);
    }

    [TestMethod]
    public async Task ReserveHlsSlot_ReleasedSlot_FreesCapacity()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source1 = fixture.Source with { TunerLimit = 1 };
        var source2 = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 1,
        };

        var slot = fixture.Manager.ReserveHlsSlot(source1);
        slot.Dispose();

        // After release, the slot should be free.
        using var slot2 = fixture.Manager.ReserveHlsSlot(source2);
        Assert.IsNotNull(slot2);
    }

    [TestMethod]
    public async Task MixedTsAndHls_ProviderCapEnforcedAcrossBoth()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source1 = fixture.Source with { TunerLimit = 2 };
        await fixture.Manager.GetOrCreateAsync(source1, CancellationToken.None);

        var source2 = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 2,
        };
        using var hlsSlot = fixture.Manager.ReserveHlsSlot(source2);

        // Third unique channel should be rejected even though cap was split TS + HLS.
        var source3 = fixture.Source with
        {
            ProviderChannelId = "channel-3",
            StreamUrl = "http://fake/stream-3",
            DisplayName = "Test Channel 3",
            RequestedRoute = "/live/key-3",
            TunerLimit = 2,
        };

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(source3, CancellationToken.None).AsTask());
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task MixedTsAndHls_HlsSlotBlocksTsSession()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source1 = fixture.Source with { TunerLimit = 1 };
        using var hlsSlot = fixture.Manager.ReserveHlsSlot(source1);

        var source2 = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
            TunerLimit = 1,
        };

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None).AsTask());
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task CheckAdmission_SameChannelAsHlsSlot_Allowed()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source1 = fixture.Source with { TunerLimit = 1 };
        using var hlsSlot = fixture.Manager.ReserveHlsSlot(source1);

        // CheckAdmission for same channel as active HLS slot — should pass (join).
        fixture.Manager.CheckAdmission(source1);
    }

    // ---------------------------------------------------------------------------
    // HLS slot registry observability (regression for native upstream HLS blind spot)
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ReserveHlsSlot_PublishesSessionToRegistry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        using var slot = fixture.Manager.ReserveHlsSlot(fixture.Source);

        var sessions = fixture.Registry.GetActiveSessions();
        Assert.HasCount(1, sessions);
        Assert.AreEqual(fixture.Source.DisplayName, sessions[0].DisplayName);
        Assert.AreEqual(SessionState.Live, sessions[0].State);
        // ProviderId and ProviderChannelId are normalized to uppercase by ChannelSessionKey.
        Assert.AreEqual(slot.Key.ProviderId, sessions[0].ProviderId);
        Assert.AreEqual(slot.Key.ProviderChannelId, sessions[0].ProviderChannelId);
    }

    [TestMethod]
    public async Task ReserveHlsSlot_PublishesClientToRegistry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        using var slot = fixture.Manager.ReserveHlsSlot(fixture.Source);

        var clients = fixture.Registry.GetActiveClients();
        Assert.HasCount(1, clients);
        Assert.AreEqual(fixture.Source.RequestedRoute, clients[0].RequestedRoute);
        Assert.AreEqual(fixture.Source.RemoteIp, clients[0].RemoteIp);
        Assert.AreEqual(fixture.Source.UserAgent, clients[0].UserAgent);
    }

    [TestMethod]
    public async Task ReleaseHlsSlot_RemovesSessionFromRegistry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var slot = fixture.Manager.ReserveHlsSlot(fixture.Source);
        Assert.HasCount(1, fixture.Registry.GetActiveSessions());

        slot.Dispose();

        Assert.IsEmpty(fixture.Registry.GetActiveSessions());
    }

    [TestMethod]
    public async Task ReleaseHlsSlot_RemovesClientFromRegistry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var slot = fixture.Manager.ReserveHlsSlot(fixture.Source);
        Assert.HasCount(1, fixture.Registry.GetActiveClients());

        slot.Dispose();

        Assert.IsEmpty(fixture.Registry.GetActiveClients());
    }

    [TestMethod]
    public async Task AddHlsSlotBytes_AccumulatesAndPublishesToRegistry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        using var slot = fixture.Manager.ReserveHlsSlot(fixture.Source);

        fixture.Manager.AddHlsSlotBytes(slot.Key, 500);
        fixture.Manager.AddHlsSlotBytes(slot.Key, 250);

        var sessions = fixture.Registry.GetActiveSessions();
        Assert.HasCount(1, sessions);
        Assert.AreEqual(750, sessions[0].TotalBytesRelayed);

        var clients = fixture.Registry.GetActiveClients();
        Assert.HasCount(1, clients);
        Assert.AreEqual(750, clients[0].BytesSent);
    }

    // ---------------------------------------------------------------------------
    // Direct-relay slots (VOD/series admission + registry visibility)
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ReserveRelaySlot_CountsTowardProviderCap()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source1 = fixture.Source with { TunerLimit = 2 };
        var source2 = fixture.Source with
        {
            ProviderChannelId = "movie-2",
            StreamUrl = "http://fake/movie-2",
            DisplayName = "Test Movie 2",
            RequestedRoute = "/movie/key-2",
            TunerLimit = 2,
        };
        var source3 = fixture.Source with
        {
            ProviderChannelId = "movie-3",
            StreamUrl = "http://fake/movie-3",
            DisplayName = "Test Movie 3",
            RequestedRoute = "/movie/key-3",
            TunerLimit = 2,
        };

        using var slot1 = fixture.Manager.ReserveRelaySlot(source1);
        using var slot2 = fixture.Manager.ReserveRelaySlot(source2);

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.ReserveRelaySlot(source3)));
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task ReserveRelaySlot_CountsTowardMaxConcurrentSessions()
    {
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                MaxConcurrentSessions = 1,
                IdleGrace = TimeSpan.FromSeconds(10),
            });

        using var slot1 = fixture.Manager.ReserveRelaySlot(fixture.Source);

        var secondSource = fixture.Source with
        {
            ProviderChannelId = "movie-2",
            StreamUrl = "http://fake/movie-2",
            DisplayName = "Test Movie 2",
            RequestedRoute = "/movie/key-2",
        };

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.ReserveRelaySlot(secondSource)));
        Assert.AreEqual(StreamAdmissionFailureKind.MaxConcurrentSessions, ex.FailureKind);
    }

    [TestMethod]
    public async Task ReserveRelaySlot_SameChannelTwice_SecondCountsAndIsRejectedAtCapOne()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        // Unlike HLS slots, relay slots are per-connection: a second viewer of the
        // same title consumes a second upstream and must be counted.
        var source = fixture.Source with { TunerLimit = 1 };
        using var slot1 = fixture.Manager.ReserveRelaySlot(source);

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.ReserveRelaySlot(source)));
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task ReserveRelaySlot_SameChannelTwice_AllowedWithinCap()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source = fixture.Source with { TunerLimit = 2 };
        using var slot1 = fixture.Manager.ReserveRelaySlot(source);
        using var slot2 = fixture.Manager.ReserveRelaySlot(source);

        var sessions = fixture.Registry.GetActiveSessions();
        Assert.HasCount(2, sessions);
        Assert.AreNotEqual(sessions[0].SessionId, sessions[1].SessionId);
    }

    [TestMethod]
    public async Task ReserveRelaySlot_BlocksLiveSession()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var vodSource = fixture.Source with
        {
            ProviderChannelId = "movie-1",
            StreamUrl = "http://fake/movie-1",
            DisplayName = "Test Movie",
            RequestedRoute = "/movie/key-1",
            TunerLimit = 1,
        };
        using var slot = fixture.Manager.ReserveRelaySlot(vodSource);

        var liveSource = fixture.Source with { TunerLimit = 1 };
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(liveSource, CancellationToken.None).AsTask());
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task LiveSession_BlocksReserveRelaySlot()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var liveSource = fixture.Source with { TunerLimit = 1 };
        await fixture.Manager.GetOrCreateAsync(liveSource, CancellationToken.None);

        var vodSource = fixture.Source with
        {
            ProviderChannelId = "movie-1",
            StreamUrl = "http://fake/movie-1",
            DisplayName = "Test Movie",
            RequestedRoute = "/movie/key-1",
            TunerLimit = 1,
        };

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.ReserveRelaySlot(vodSource)));
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task ReserveRelaySlot_PublishesSessionAndClientToRegistry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        using var slot = fixture.Manager.ReserveRelaySlot(fixture.Source);

        var sessions = fixture.Registry.GetActiveSessions();
        Assert.HasCount(1, sessions);
        Assert.AreEqual(fixture.Source.DisplayName, sessions[0].DisplayName);
        Assert.AreEqual(SessionState.Live, sessions[0].State);
        Assert.AreEqual("Direct", sessions[0].RelayMode);

        var clients = fixture.Registry.GetActiveClients();
        Assert.HasCount(1, clients);
        Assert.AreEqual(fixture.Source.RequestedRoute, clients[0].RequestedRoute);
    }

    [TestMethod]
    public async Task ReserveRelaySlot_ReportBytes_UpdatesRegistrySnapshots()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        using var slot = fixture.Manager.ReserveRelaySlot(fixture.Source);
        slot.ReportBytes(12345);

        var sessions = fixture.Registry.GetActiveSessions();
        Assert.HasCount(1, sessions);
        Assert.AreEqual(12345, sessions[0].TotalBytesRelayed);

        var clients = fixture.Registry.GetActiveClients();
        Assert.HasCount(1, clients);
        Assert.AreEqual(12345, clients[0].BytesSent);
    }

    [TestMethod]
    public async Task ReleaseRelaySlot_RemovesFromRegistryAndFreesCapacity()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source = fixture.Source with { TunerLimit = 1 };
        var slot = fixture.Manager.ReserveRelaySlot(source);
        Assert.HasCount(1, fixture.Registry.GetActiveSessions());
        Assert.HasCount(1, fixture.Registry.GetActiveClients());

        slot.Dispose();

        Assert.IsEmpty(fixture.Registry.GetActiveSessions());
        Assert.IsEmpty(fixture.Registry.GetActiveClients());
        Assert.HasCount(1, fixture.Registry.GetRecentEndedSessions());

        using var slot2 = fixture.Manager.ReserveRelaySlot(source);
        Assert.IsNotNull(slot2);
    }

    [TestMethod]
    public async Task CheckAdmission_RelaySlotCountsTowardProviderCap()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var vodSource = fixture.Source with
        {
            ProviderChannelId = "movie-1",
            StreamUrl = "http://fake/movie-1",
            DisplayName = "Test Movie",
            RequestedRoute = "/movie/key-1",
            TunerLimit = 1,
        };
        using var slot = fixture.Manager.ReserveRelaySlot(vodSource);

        var liveSource = fixture.Source with { TunerLimit = 1 };
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => Task.Run(() => fixture.Manager.CheckAdmission(liveSource)));
        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);
    }

    [TestMethod]
    public async Task ResetAllAsync_ClearsRelaySlotsFromRegistryAndFreesCapacity()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        var source = fixture.Source with { TunerLimit = 1 };
        fixture.Manager.ReserveRelaySlot(source);
        Assert.HasCount(1, fixture.Registry.GetActiveSessions());
        Assert.HasCount(1, fixture.Registry.GetActiveClients());

        await fixture.Manager.ResetAllAsync();

        Assert.IsEmpty(fixture.Registry.GetActiveSessions());
        Assert.IsEmpty(fixture.Registry.GetActiveClients());

        // Capacity should be freed — a leaked/un-disposed relay slot must not
        // permanently occupy the provider cap after a reset.
        using var slot = fixture.Manager.ReserveRelaySlot(source);
        Assert.IsNotNull(slot);
    }

    [TestMethod]
    public async Task SweepExpiredHlsSlots_RemovesExpiredReservationFromRegistry()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 19, 18, 00, 00, TimeSpan.Zero));
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            timeProvider: timeProvider);

        var slot = fixture.Manager.ReserveHlsSlot(fixture.Source, ttl: TimeSpan.FromSeconds(1));
        Assert.HasCount(1, fixture.Registry.GetActiveSessions());
        Assert.HasCount(1, fixture.Registry.GetActiveClients());

        timeProvider.Advance(TimeSpan.FromSeconds(2));

        var evicted = fixture.Manager.SweepExpiredHlsSlots();

        Assert.AreEqual(1, evicted);
        Assert.IsEmpty(fixture.Registry.GetActiveSessions());
        Assert.IsEmpty(fixture.Registry.GetActiveClients());

        slot.Dispose();
    }

    [TestMethod]
    public async Task SweepExpiredHlsSlots_ExpiredSlotFreesCapacity()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 04, 19, 18, 00, 00, TimeSpan.Zero));
        await using var fixture = await SessionFixture.CreateAsync(
            FakeStreamingHandler.StreamForever(),
            proxyOptions: new StreamProxyOptions
            {
                StreamingEnabled = true,
                MaxConcurrentSessions = 1,
                IdleGrace = TimeSpan.FromSeconds(10),
            },
            timeProvider: timeProvider);

        var slot = fixture.Manager.ReserveHlsSlot(fixture.Source, ttl: TimeSpan.FromSeconds(1));
        var secondSource = fixture.Source with
        {
            ProviderChannelId = "channel-2",
            StreamUrl = "http://fake/stream-2",
            DisplayName = "Test Channel 2",
            RequestedRoute = "/live/key-2",
        };

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, fixture.Manager.SweepExpiredHlsSlots());

        using var slot2 = fixture.Manager.ReserveHlsSlot(secondSource);
        Assert.AreEqual("CHANNEL-2", slot2.Key.ProviderChannelId);

        slot.Dispose();
    }

    [TestMethod]
    public async Task TouchHlsSlot_UpdatesLastUpstreamByteUtcInRegistry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        using var slot = fixture.Manager.ReserveHlsSlot(fixture.Source);

        var before = fixture.Registry.GetActiveSessions()[0].LastUpstreamByteUtc;
        Assert.IsNull(before);

        fixture.Manager.TouchHlsSlot(slot.Key);

        var after = fixture.Registry.GetActiveSessions()[0].LastUpstreamByteUtc;
        Assert.IsNotNull(after);
    }

    [TestMethod]
    public async Task ReserveHlsSlot_SameChannelTwice_SingleRegistryEntry()
    {
        await using var fixture = await SessionFixture.CreateAsync(FakeStreamingHandler.StreamForever());

        using var slot1 = fixture.Manager.ReserveHlsSlot(fixture.Source);
        using var slot2 = fixture.Manager.ReserveHlsSlot(fixture.Source);

        // Both reservations share the same admission slot, so only one session in registry.
        Assert.HasCount(1, fixture.Registry.GetActiveSessions());
    }

    [TestMethod]
    public async Task ReserveHlsSlot_WhenActiveSharedSessionExists_RegistryShowsSharedSession()
    {
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(handler);

        // Start a shared TS session so the registry already has a session entry.
        var tsSession = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var cts = new CancellationTokenSource();
        await tsSession.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        await WaitUntilAsync(
            () => fixture.Registry.GetActiveSessions().Any(s => s.SessionId == tsSession.SessionId),
            TimeSpan.FromSeconds(5));

        // Reserving an HLS slot for the same channel does not add a second registry entry.
        using var hlsSlot = fixture.Manager.ReserveHlsSlot(fixture.Source);

        var sessions = fixture.Registry.GetActiveSessions();
        Assert.HasCount(1, sessions);
        Assert.AreEqual(tsSession.SessionId, sessions[0].SessionId);

        cts.Cancel();
        await tsSession.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ExternalRetention_KeepsSessionAliveUntilDisposed()
    {
        var handler = FakeStreamingHandler.StreamForever();
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            proxyOptions: new StreamProxyOptions { StreamingEnabled = true, IdleGrace = TimeSpan.FromMilliseconds(200) });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var requestCts = new CancellationTokenSource();
        var subscriber = await session.AttachSubscriberAsync(new DefaultHttpContext(), requestCts.Token);
        using var retention = session.RetainExternalActivity();

        await WaitUntilAsync(() => subscriber.BytesSent > 0, TimeSpan.FromSeconds(5));

        requestCts.Cancel();
        await subscriber.CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.Delay(350);
        Assert.IsTrue(fixture.Manager.TryGet(session.Key, out _));

        retention.Dispose();
        await WaitUntilAsync(() => !fixture.Manager.TryGet(session.Key, out _), TimeSpan.FromSeconds(5));
        Assert.IsFalse(fixture.Manager.TryGet(session.Key, out _));
    }

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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
    }

    private static GeneratedHlsSessionManager CreateGeneratedHlsManager(
        string root,
        ChannelSessionManager channelSessionManager,
        StreamingRegistry registry)
    {
        var options = Options.Create(new GeneratedHlsOptions
        {
            Enabled = true,
            Directory = Path.Combine(root, "generated-hls"),
            FfmpegPath = FakeFfmpegBinary.LocateExecutable(),
            SegmentDurationSeconds = 1,
            PlaylistSize = 2,
            DeleteThreshold = 1,
            StartupTimeoutSeconds = 3,
            InactivityTimeoutSeconds = 120,
            CleanupIntervalSeconds = 120,
            StartupStaleAgeHours = 1,
        });

        return new GeneratedHlsSessionManager(
            options,
            scopeFactory: null!,
            channelSessionManager,
            registry,
            NullLogger<GeneratedHlsSessionManager>.Instance);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"m3undle-channel-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup in tests
        }
    }

    private static DefaultHttpContext CreateHttpContext(string? remoteIp)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(remoteIp))
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        return context;
    }

    private static DefaultHttpContext CreateClientContext(string remoteIp, string userAgent, string requestPath)
    {
        var context = CreateHttpContext(remoteIp);
        context.Request.Headers.UserAgent = userAgent;
        context.Request.Path = requestPath;
        return context;
    }

    private static (DefaultHttpContext Context, MemoryStream Body) CreateResponseCaptureContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        return (context, body);
    }

    private static IReadOnlyList<byte[]> MpegTsSafeStartupSequence()
        =>
        [
            PatPacket(100),
            PmtPacket(100, 256),
            VideoPacket(256, [0x00, 0x00, 0x01, 0x67, 0x01, 0x02]),
            VideoPacket(256, [0x00, 0x00, 0x01, 0x68, 0x03, 0x04]),
            VideoPacket(256, [0x00, 0x00, 0x01, 0x65, 0x05, 0x06]),
            FakeStreamingHandler.ValidTsPacket(0xCC),
        ];

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i <= data.Length - pattern.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] == pattern[j])
                    continue;

                matched = false;
                break;
            }

            if (matched)
                return i;
        }

        return -1;
    }

    private static bool ContainsNullTsPacket(byte[] data)
    {
        for (var i = 0; i + 3 <= data.Length; i += 188)
        {
            if (data[i] == 0x47 && data[i + 1] == 0x1f && data[i + 2] == 0xff)
                return true;
        }
        return false;
    }

    private static byte[] PatPacket(int pmtPid)
        => Packet(0, payloadUnitStart: true,
        [
            0x00,
            0x00, 0xB0, 0x0D,
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            0x00, 0x01,
            (byte)(0xE0 | ((pmtPid >> 8) & 0x1F)), (byte)pmtPid,
            0x00, 0x00, 0x00, 0x00,
        ]);

    private static byte[] PmtPacket(int pmtPid, int videoPid)
        => Packet(pmtPid, payloadUnitStart: true,
        [
            0x00,
            0x02, 0xB0, 0x12,
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            (byte)(0xE0 | ((videoPid >> 8) & 0x1F)), (byte)videoPid,
            0xF0, 0x00,
            0x1B,
            (byte)(0xE0 | ((videoPid >> 8) & 0x1F)), (byte)videoPid,
            0xF0, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ]);

    private static byte[] VideoPacket(int videoPid, byte[] annexB)
        => Packet(videoPid, payloadUnitStart: true,
            [0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x00, .. annexB]);

    private static byte[] TimestampedVideoPacket(int videoPid, byte[] annexB, long dts90k)
        => Packet(videoPid, payloadUnitStart: true,
        [
            0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0xC0, 0x0A,
            .. EncodePesTimestamp(0x03, dts90k),
            .. EncodePesTimestamp(0x01, dts90k),
            .. annexB,
        ]);

    private static byte[] EncodePesTimestamp(int prefix, long value)
        =>
        [
            (byte)((prefix << 4) | (int)(((value >> 30) & 0x07) << 1) | 0x01),
            (byte)((value >> 22) & 0xFF),
            (byte)((((value >> 15) & 0x7F) << 1) | 0x01),
            (byte)((value >> 7) & 0xFF),
            (byte)(((value & 0x7F) << 1) | 0x01),
        ];

    private static byte[] AudioOnlyPmtPacket(int pmtPid)
        => Packet(pmtPid, payloadUnitStart: true,
        [
            0x00,
            0x02, 0xB0, 0x12,
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            0xE1, 0x00,  // PCR PID
            0xF0, 0x00,  // no program info
            0x0F,        // stream type: AAC audio (not H.264)
            0xE1, 0x00,  // elementary PID
            0xF0, 0x00,  // no ES info
            0x00, 0x00, 0x00, 0x00,
        ]);

    private static byte[] NullTsPacket()
        => Packet(0x1fff);

    private static byte[] Packet(int pid, bool payloadUnitStart = false, byte[]? payload = null)
    {
        var packet = Enumerable.Repeat((byte)0xFF, 188).ToArray();
        packet[0] = 0x47;
        packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = 0x10;
        payload?.CopyTo(packet.AsSpan(4));
        return packet;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private sealed class BlockingWriteStream : IOStream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }

    // ---------------------------------------------------------------------------
    // Fake streaming handler
    // ---------------------------------------------------------------------------

    private sealed class FakeStreamingHandler : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> _behaviors = new();
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _defaultBehavior;
        private int _connectionCount;

        public FakeStreamingHandler(Func<CancellationToken, Task<HttpResponseMessage>> defaultBehavior)
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
            var data = chunk ?? ValidTsPacket();
            return new FakeStreamingHandler(ct => StreamForeverResponse(data, ct));
        }

        public static FakeStreamingHandler StreamForeverSequence(IReadOnlyList<byte[]> chunks)
            => new(ct => StreamForeverSequenceResponse(chunks, ct));

        public static byte[] ValidTsPacket(byte fill = 0xAA)
        {
            var packet = new byte[188];
            Array.Fill(packet, fill);
            packet[0] = 0x47;
            packet[1] = 0x01;
            packet[2] = 0x00;
            packet[3] = 0x10;
            return packet;
        }

        public static FakeStreamingHandler ReturnStatus(
            HttpStatusCode statusCode,
            Action<HttpResponseMessage>? configure = null)
            => new FakeStreamingHandler(_ =>
            {
                var response = new HttpResponseMessage(statusCode);
                configure?.Invoke(response);
                return Task.FromResult(response);
            });

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

        public static Task<HttpResponseMessage> StreamForeverSequenceResponse(IReadOnlyList<byte[]> chunks, CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    var index = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        var chunk = chunks[index++ % chunks.Count];
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

        public static Task<HttpResponseMessage> WriteSequenceThenForever(IReadOnlyList<byte[]> once, byte[] forever, CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var chunk in once)
                    {
                        await pipe.Writer.WriteAsync(chunk, ct);
                        await Task.Delay(5, ct);
                    }

                    while (!ct.IsCancellationRequested)
                    {
                        await pipe.Writer.WriteAsync(forever, ct);
                        await Task.Delay(5, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                finally { pipe.Writer.Complete(); }
            });
            return Task.FromResult(CreateStreamingResponse(pipe.Reader.AsStream()));
        }

        public static Task<HttpResponseMessage> WritePhasedSequenceThenForever(
            IReadOnlyList<byte[]> initial,
            IReadOnlyList<byte[]> afterPause,
            byte[] forever,
            TimeSpan pause,
            CancellationToken ct)
            => WritePhasedSequencesThenForever([initial, afterPause], forever, pause, ct);

        public static Task<HttpResponseMessage> WritePhasedSequencesThenForever(
            IReadOnlyList<IReadOnlyList<byte[]>> phases,
            byte[] forever,
            TimeSpan pause,
            CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    var firstPhase = true;
                    foreach (var phase in phases)
                    {
                        if (!firstPhase)
                            await Task.Delay(pause, ct);
                        firstPhase = false;

                        foreach (var chunk in phase)
                        {
                            await pipe.Writer.WriteAsync(chunk, ct);
                            await Task.Delay(5, ct);
                        }
                    }

                    while (!ct.IsCancellationRequested)
                    {
                        await pipe.Writer.WriteAsync(forever, ct);
                        await Task.Delay(5, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                finally { pipe.Writer.Complete(); }
            });
            return Task.FromResult(CreateStreamingResponse(pipe.Reader.AsStream()));
        }

        public static Task<HttpResponseMessage> WriteSequenceThenStall(IReadOnlyList<byte[]> sequence, CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var chunk in sequence)
                    {
                        await pipe.Writer.WriteAsync(chunk, ct);
                        await Task.Delay(5, ct);
                    }

                    await Task.Delay(Timeout.Infinite, ct);
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

        public static Task<HttpResponseMessage> WriteOneChunkAfterDelay(byte[] chunk, TimeSpan delay, CancellationToken ct)
        {
            var pipe = new Pipe();
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct);

                    await pipe.Writer.WriteAsync(chunk, ct);
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
        public StreamingDiagnosticsStore DiagnosticsStore { get; }
        public ChannelSessionManager Manager { get; }
        public StreamSourceDescriptor Source { get; }

        private SessionFixture(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            FakeStreamingHandler handler,
            UpstreamFailureStrikeStore strikeStore,
            StreamingRegistry registry,
            StreamingDiagnosticsStore diagnosticsStore,
            ChannelSessionManager manager,
            StreamSourceDescriptor source)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
            Handler = handler;
            StrikeStore = strikeStore;
            Registry = registry;
            DiagnosticsStore = diagnosticsStore;
            Manager = manager;
            Source = source;
        }

        public static async Task<SessionFixture> CreateAsync(
            FakeStreamingHandler handler,
            BufferOptions? bufferOptions = null,
            StreamProxyOptions? proxyOptions = null,
            ReconnectOptions? reconnectOptions = null,
            TimeProvider? timeProvider = null,
            string cleanRelayMode = "off",
            string? ffmpegPath = null,
            string streamUrl = "http://fake/stream",
            bool forceMpegTs = false,
            CleanRelayOptions? cleanRelayOptions = null)
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
                PlaylistUrl = "http://fake/playlist.m3u",
                TimeoutSeconds = 30,
                CleanRelayMode = cleanRelayMode,
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
                StreamUrl = streamUrl,
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
            var healthProfileService = new StreamChannelHealthProfileService(
                scopeFactory,
                NullLogger<StreamChannelHealthProfileService>.Instance);
            var connector = new UpstreamStreamConnector(
                httpClientFactory, scopeFactory, healthProfileService, reconnectOpts,
                Options.Create(new GeneratedHlsOptions { FfmpegPath = ffmpegPath ?? string.Empty }),
                Options.Create(cleanRelayOptions ?? new CleanRelayOptions()),
                NullLogger<UpstreamStreamConnector>.Instance);
            var strikeStore = new UpstreamFailureStrikeStore();
            var admissionBackoffStore = timeProvider is null
                ? new StreamAdmissionBackoffStore()
                : new StreamAdmissionBackoffStore(timeProvider);
            var registry = new StreamingRegistry(proxyOpts);
            var diagnosticsStore = new StreamingDiagnosticsStore(proxyOpts);
            var manager = new ChannelSessionManager(
                bufOpts, proxyOpts, reconnectOpts, connector, strikeStore, admissionBackoffStore, registry,
                diagnosticsStore, NoopStreamChannelHealthEventRecorder.Instance, healthProfileService, new NullEventService(),
                NullLoggerFactory.Instance, timeProvider ?? TimeProvider.System);

            var source = new StreamSourceDescriptor(
                ProfileId: "profile-1",
                ProviderId: "provider-1",
                ProviderChannelId: "channel-1",
                StreamUrl: "http://fake/stream",
                DisplayName: "Test Channel",
                RequestedRoute: "/live/key-1",
                UserAgent: null,
                RemoteIp: null,
                ForceMpegTs: forceMpegTs);

            return new SessionFixture(connection, serviceProvider, handler, strikeStore, registry, diagnosticsStore, manager, source);
        }

        public async Task SeedHealthEventsAsync(params StreamChannelHealthEvent[] events)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.StreamChannelHealthEvents.AddRange(events);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.ResetAllAsync();
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
