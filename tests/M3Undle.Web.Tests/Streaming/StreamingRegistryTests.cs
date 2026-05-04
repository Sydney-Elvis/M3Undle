using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class StreamingRegistryTests
{
    [TestMethod]
    public void GetActiveSessions_AggregatesInternalChildSubscribersIntoVisibleParent()
    {
        var registry = CreateRegistry();
        var startedUtc = new DateTimeOffset(2026, 04, 20, 12, 00, 00, TimeSpan.Zero);

        registry.UpsertSession(new StreamSessionSnapshot(
            SessionId: "parent-session",
            ProviderId: "provider",
            ProviderChannelId: "channel",
            DisplayName: "CBS",
            State: SessionState.Live,
            SubscriberCount: 1,
            IsShared: false,
            BufferUsedBytes: 10,
            BufferMaxBytes: 20,
            StartedUtc: startedUtc,
            LastUpstreamByteUtc: startedUtc,
            ReconnectAttempts: 0,
            LastFailureKind: null));

        registry.UpsertSession(new StreamSessionSnapshot(
            SessionId: "child-hls",
            ProviderId: "provider",
            ProviderChannelId: "channel",
            DisplayName: "CBS HLS",
            State: SessionState.Live,
            SubscriberCount: 1,
            IsShared: false,
            BufferUsedBytes: 0,
            BufferMaxBytes: 0,
            StartedUtc: startedUtc.AddSeconds(1),
            LastUpstreamByteUtc: startedUtc.AddSeconds(1),
            ReconnectAttempts: 0,
            LastFailureKind: null,
            IsInternal: true,
            ParentStreamSessionId: "parent-session"));

        var sessions = registry.GetActiveSessions();

        Assert.HasCount(1, sessions);
        Assert.AreEqual("parent-session", sessions[0].SessionId);
        Assert.AreEqual(2, sessions[0].SubscriberCount);
        Assert.IsTrue(sessions[0].IsShared);
    }

    [TestMethod]
    public void TryGetSession_ForVisibleParent_ReturnsAggregatedSubscriberCount()
    {
        var registry = CreateRegistry();
        var startedUtc = new DateTimeOffset(2026, 04, 20, 12, 00, 00, TimeSpan.Zero);

        registry.UpsertSession(new StreamSessionSnapshot(
            SessionId: "parent-session",
            ProviderId: "provider",
            ProviderChannelId: "channel",
            DisplayName: "CBS",
            State: SessionState.Live,
            SubscriberCount: 1,
            IsShared: false,
            BufferUsedBytes: 10,
            BufferMaxBytes: 20,
            StartedUtc: startedUtc,
            LastUpstreamByteUtc: startedUtc,
            ReconnectAttempts: 0,
            LastFailureKind: null));

        registry.UpsertSession(new StreamSessionSnapshot(
            SessionId: "child-hls",
            ProviderId: "provider",
            ProviderChannelId: "channel",
            DisplayName: "CBS HLS",
            State: SessionState.Live,
            SubscriberCount: 2,
            IsShared: true,
            BufferUsedBytes: 0,
            BufferMaxBytes: 0,
            StartedUtc: startedUtc.AddSeconds(1),
            LastUpstreamByteUtc: startedUtc.AddSeconds(1),
            ReconnectAttempts: 0,
            LastFailureKind: null,
            IsInternal: true,
            ParentStreamSessionId: "parent-session"));

        var parent = registry.TryGetSession("parent-session");
        var child = registry.TryGetSession("child-hls");

        Assert.IsNotNull(parent);
        Assert.AreEqual(3, parent.SubscriberCount);
        Assert.IsTrue(parent.IsShared);

        Assert.IsNotNull(child);
        Assert.AreEqual(2, child.SubscriberCount);
        Assert.IsTrue(child.IsInternal);
        Assert.AreEqual("parent-session", child.ParentStreamSessionId);
    }

    [TestMethod]
    public void GetActiveSessions_ShowsParentlessGeneratedHlsSession()
    {
        var registry = CreateRegistry();
        var startedUtc = new DateTimeOffset(2026, 04, 20, 12, 00, 00, TimeSpan.Zero);

        registry.UpsertSession(new StreamSessionSnapshot(
            SessionId: "xtream-hls",
            ProviderId: "provider",
            ProviderChannelId: "channel",
            DisplayName: "FOX",
            State: SessionState.Live,
            SubscriberCount: 1,
            IsShared: false,
            BufferUsedBytes: 0,
            BufferMaxBytes: 0,
            StartedUtc: startedUtc,
            LastUpstreamByteUtc: startedUtc,
            ReconnectAttempts: 0,
            LastFailureKind: null,
            IsInternal: false,
            ParentStreamSessionId: null));

        var sessions = registry.GetActiveSessions();

        Assert.HasCount(1, sessions);
        Assert.AreEqual("xtream-hls", sessions[0].SessionId);
        Assert.AreEqual("FOX", sessions[0].DisplayName);
        Assert.IsFalse(sessions[0].IsInternal);
    }

    private static StreamingRegistry CreateRegistry()
        => new(Options.Create(new M3Undle.Web.Streaming.Configuration.StreamProxyOptions()));
}