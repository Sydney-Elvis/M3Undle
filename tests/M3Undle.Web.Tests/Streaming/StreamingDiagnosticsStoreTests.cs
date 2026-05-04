using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Observability;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class StreamingDiagnosticsStoreTests
{
    [TestMethod]
    public void Query_FiltersBySessionProviderChannelAndKind()
    {
        var store = CreateStore();
        store.Record(CreateEvent("session-1", "provider-1", "channel-1", StreamDiagnosticEventKind.UpstreamConnected));
        store.Record(CreateEvent("session-2", "provider-1", "channel-2", StreamDiagnosticEventKind.UpstreamFailure));
        store.Record(CreateEvent("session-3", "provider-2", "channel-1", StreamDiagnosticEventKind.UpstreamFailure));

        var bySession = store.Query(sessionId: "session-1");
        var byProviderChannel = store.Query(providerId: "provider-1", providerChannelId: "channel-2");
        var byKind = store.Query(kind: StreamDiagnosticEventKind.UpstreamFailure);

        Assert.HasCount(1, bySession);
        Assert.AreEqual("session-1", bySession[0].SessionId);

        Assert.HasCount(1, byProviderChannel);
        Assert.AreEqual("session-2", byProviderChannel[0].SessionId);

        Assert.HasCount(2, byKind);
    }

    [TestMethod]
    public void Query_PrunesByCount()
    {
        var store = CreateStore(maxEvents: 2);
        store.Record(CreateEvent("session-1", "provider", "channel", StreamDiagnosticEventKind.SessionCreated));
        store.Record(CreateEvent("session-2", "provider", "channel", StreamDiagnosticEventKind.SessionCreated));
        store.Record(CreateEvent("session-3", "provider", "channel", StreamDiagnosticEventKind.SessionCreated));

        var events = store.Query();

        Assert.HasCount(2, events);
        Assert.AreEqual("session-2", events[0].SessionId);
        Assert.AreEqual("session-3", events[1].SessionId);
    }

    [TestMethod]
    public void Query_ReturnsInTimestampOrder()
    {
        var store = CreateStore();
        var t1 = DateTimeOffset.UtcNow.AddSeconds(-10);
        var t2 = DateTimeOffset.UtcNow.AddSeconds(-5);
        var t3 = DateTimeOffset.UtcNow;

        // Enqueue deliberately out of timestamp order.
        store.Record(CreateEvent("session-c", "provider", "channel", StreamDiagnosticEventKind.SessionCreated, t3));
        store.Record(CreateEvent("session-a", "provider", "channel", StreamDiagnosticEventKind.SessionCreated, t1));
        store.Record(CreateEvent("session-b", "provider", "channel", StreamDiagnosticEventKind.SessionCreated, t2));

        var events = store.Query();

        Assert.HasCount(3, events);
        Assert.AreEqual("session-a", events[0].SessionId);
        Assert.AreEqual("session-b", events[1].SessionId);
        Assert.AreEqual("session-c", events[2].SessionId);
    }

    [TestMethod]
    public void Query_FiltersBySinceUtc()
    {
        var store = CreateStore();
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2);

        store.Record(CreateEvent("old-session", "provider", "channel", StreamDiagnosticEventKind.SessionCreated, cutoff.AddMinutes(-1)));
        store.Record(CreateEvent("new-session-1", "provider", "channel", StreamDiagnosticEventKind.SessionCreated, cutoff));
        store.Record(CreateEvent("new-session-2", "provider", "channel", StreamDiagnosticEventKind.SessionCreated, cutoff.AddMinutes(1)));

        var events = store.Query(sinceUtc: cutoff);

        Assert.HasCount(2, events);
        Assert.AreEqual("new-session-1", events[0].SessionId);
        Assert.AreEqual("new-session-2", events[1].SessionId);
    }

    [TestMethod]
    public void Query_PrunesByRetention()
    {
        var store = CreateStore(retentionSeconds: 60);
        store.Record(CreateEvent(
            "old-session",
            "provider",
            "channel",
            StreamDiagnosticEventKind.SessionCreated,
            DateTimeOffset.UtcNow.AddMinutes(-5)));
        store.Record(CreateEvent("new-session", "provider", "channel", StreamDiagnosticEventKind.SessionCreated));

        var events = store.Query();

        Assert.HasCount(1, events);
        Assert.AreEqual("new-session", events[0].SessionId);
    }

    private static StreamingDiagnosticsStore CreateStore(int maxEvents = 1000, int retentionSeconds = 900)
        => new(Options.Create(new StreamProxyOptions
        {
            DiagnosticsMaxEvents = maxEvents,
            DiagnosticsRetentionSeconds = retentionSeconds,
        }));

    private static StreamDiagnosticEvent CreateEvent(
        string sessionId,
        string providerId,
        string providerChannelId,
        StreamDiagnosticEventKind kind,
        DateTimeOffset? timestampUtc = null)
        => new(
            EventId: Guid.NewGuid().ToString("N"),
            TimestampUtc: timestampUtc ?? DateTimeOffset.UtcNow,
            Kind: kind,
            SessionId: sessionId,
            ProviderId: providerId,
            ProviderChannelId: providerChannelId);
}
