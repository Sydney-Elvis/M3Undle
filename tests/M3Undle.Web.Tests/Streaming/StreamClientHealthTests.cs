using M3Undle.Web.Streaming.Observability;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class StreamClientHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Hls_RecentSegment_IsLive()
        => Assert.AreEqual(ClientHealthStatus.Live, Evaluate(HlsClient(Now.AddSeconds(-2), Now.AddSeconds(-2))));

    [TestMethod]
    public void Hls_ManifestPollingWithoutSegments_IsSlow()
        => Assert.AreEqual(ClientHealthStatus.Slow, Evaluate(HlsClient(Now.AddSeconds(-1), null)));

    [TestMethod]
    public void Hls_NoRequestPastEvictionThreshold_IsStalled()
        => Assert.AreEqual(ClientHealthStatus.Stalled, Evaluate(HlsClient(Now.AddSeconds(-61), Now.AddSeconds(-61))));

    [TestMethod]
    public void Hls_NewClientWaitingForFirstSegment_IsUnknown()
        => Assert.AreEqual(ClientHealthStatus.Unknown, Evaluate(HlsClient(Now.AddSeconds(-1), null, Now.AddSeconds(-1))));

    [TestMethod]
    public void Ts_BackloggedAndBelowUpstream_IsSlow()
    {
        var client = BaseClient(ClientTransport.DirectRelay) with { BytesPerSecond = 100_000, QueueDepth = 5 };
        Assert.AreEqual(ClientHealthStatus.Slow, Evaluate(client, 1_000_000));
    }

    private static ClientHealthStatus Evaluate(StreamClientSnapshot client, long? upstream = null)
        => StreamClientHealth.Evaluate(client, upstream, 2, 60, Now);

    private static StreamClientSnapshot HlsClient(
        DateTimeOffset lastAccess,
        DateTimeOffset? lastMedia,
        DateTimeOffset? connected = null)
        => BaseClient(ClientTransport.GeneratedHls) with
        {
            ConnectedUtc = connected ?? Now.AddMinutes(-5),
            LastActivityUtc = lastAccess,
            LastMediaActivityUtc = lastMedia,
        };

    private static StreamClientSnapshot BaseClient(ClientTransport transport)
        => new("client", "session", "/stream", "192.0.2.1", "Test", Now.AddMinutes(-5), 0, 0, Transport: transport);
}
