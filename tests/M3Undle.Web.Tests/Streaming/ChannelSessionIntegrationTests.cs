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
    public async Task Session_UpstreamStall_EmitsFailureAndReconnectDiagnostics()
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
        var chunk = new byte[188];
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
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await session.AttachSubscriberAsync(new DefaultHttpContext(), cts.Token);

        // Wait until reconnect completes and bytes are flowing on the second connection.
        await WaitUntilAsync(
            () =>
            {
                var snap = fixture.Registry.TryGetSession(session.SessionId);
                return snap is { ReconnectAttempts: > 0 } && snap.BytesSinceReconnect > 0;
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
        var chunk = new byte[188];
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
    public async Task Session_ProviderProxyAuthRequired_RecordsCooldownAndRejectsInitialAttach()
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
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        Assert.AreEqual(StreamAdmissionFailureKind.Cooldown, ex.FailureKind);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
        Assert.AreEqual(20, ex.RetryAfterSeconds);
        Assert.IsTrue(fixture.StrikeStore.IsCoolingDown(fixture.Source.SessionKey, out var remaining));
        Assert.IsGreaterThan(TimeSpan.Zero, remaining);
        Assert.AreEqual(1, handler.ConnectionCount);
    }

    [TestMethod]
    public async Task Session_RateLimited_UsesProviderRetryAfterForCooldown()
    {
        // Provider Retry-After (12s) > StrikeCooldown (5s): provider value should win.
        var handler = FakeStreamingHandler.ReturnStatus((HttpStatusCode)429, response =>
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12)));
        await using var fixture = await SessionFixture.CreateAsync(
            handler,
            reconnectOptions: new ReconnectOptions
            {
                ReadStallTimeout = TimeSpan.FromSeconds(30),
                OutageWindow = TimeSpan.FromSeconds(30),
                StrikeCooldown = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                FixedStepBackoffSeconds = [0],
            });

        var session = await fixture.Manager.GetOrCreateAsync(fixture.Source, CancellationToken.None);
        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => session.AttachSubscriberAsync(new DefaultHttpContext(), CancellationToken.None));

        Assert.AreEqual(StreamAdmissionFailureKind.Cooldown, ex.FailureKind);
        Assert.AreEqual(12, ex.RetryAfterSeconds);
        Assert.IsTrue(fixture.StrikeStore.IsCoolingDown(fixture.Source.SessionKey, out var remaining));
        Assert.IsGreaterThan(TimeSpan.FromSeconds(5), remaining);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromSeconds(12), remaining);
        Assert.AreEqual(1, handler.ConnectionCount);

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
        // ResolveCooldownDuration should use max(10, 30) = 30s so we don't retry too early.
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
    public async Task Session_ProviderTunerLimit_PreemptsIdleGraceForSameRemoteIpRetune()
    {
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

        var session2 = await fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None);

        Assert.AreEqual(source2.SessionKey, session2.Key);
        await WaitUntilAsync(() => !fixture.Manager.TryGet(source1.SessionKey, out _), TimeSpan.FromSeconds(5));
        Assert.IsFalse(fixture.Manager.TryGet(source1.SessionKey, out _));

        await session2.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_ProviderTunerLimit_DoesNotPreemptIdleGraceForDifferentRemoteIp()
    {
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

        var ex = await AssertThrowsAsync<StreamAdmissionException>(
            () => fixture.Manager.GetOrCreateAsync(source2, CancellationToken.None).AsTask());

        Assert.AreEqual(StreamAdmissionFailureKind.ProviderLimit, ex.FailureKind);

        await session1.DisposeAsync();
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

    private static DefaultHttpContext CreateHttpContext(string? remoteIp)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrWhiteSpace(remoteIp))
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        return context;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
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
            TimeProvider? timeProvider = null)
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
                Options.Create(new GeneratedHlsOptions()),
                NullLogger<UpstreamStreamConnector>.Instance);
            var strikeStore = new UpstreamFailureStrikeStore();
            var admissionBackoffStore = timeProvider is null
                ? new StreamAdmissionBackoffStore()
                : new StreamAdmissionBackoffStore(timeProvider);
            var registry = new StreamingRegistry(proxyOpts);
            var diagnosticsStore = new StreamingDiagnosticsStore(proxyOpts);
            var manager = new ChannelSessionManager(
                bufOpts, proxyOpts, reconnectOpts, connector, strikeStore, admissionBackoffStore, registry,
                diagnosticsStore, NullLoggerFactory.Instance, timeProvider ?? TimeProvider.System);

            var source = new StreamSourceDescriptor(
                ProfileId: "profile-1",
                ProviderId: "provider-1",
                ProviderChannelId: "channel-1",
                StreamUrl: "http://fake/stream",
                DisplayName: "Test Channel",
                RequestedRoute: "/live/key-1",
                UserAgent: null,
                RemoteIp: null);

            return new SessionFixture(connection, serviceProvider, handler, strikeStore, registry, diagnosticsStore, manager, source);
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
}
