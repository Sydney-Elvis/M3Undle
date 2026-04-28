using M3Undle.Web.Streaming.Buffering;
using M3Undle.Web.Streaming.Subscribers;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class SubscriberConnectionTests
{
    [TestMethod]
    public async Task PumpAsync_SkipsLiveQueueChunks_AlreadyCoveredBySnapshot()
    {
        // Arrange: write 7 chunks to a buffer. Mark safe start at chunk 1 (sequence 1).
        // Snapshot = chunks 1..4 (snapshotLastSequence = 4).
        // Enqueue chunks 2..4 as overlapping live items plus 5..6 as new live items.
        // Expected output: snapshot(A1,A2,A3,A4) + live(A5,A6) — no duplicates of A2/A3/A4.
        var buffer = new RingBuffer(maxBytes: 1024);
        using var l0 = buffer.Write(new byte[] { 0xA0 });
        using var l1 = buffer.Write(new byte[] { 0xA1 });
        using var l2 = buffer.Write(new byte[] { 0xA2 });
        using var l3 = buffer.Write(new byte[] { 0xA3 });
        using var l4 = buffer.Write(new byte[] { 0xA4 });
        buffer.MarkSafeStart(l1);
        var snapshot = buffer.CreateSafeStartSnapshot();
        using var l5 = buffer.Write(new byte[] { 0xA5 });
        using var l6 = buffer.Write(new byte[] { 0xA6 });

        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var subscriber = new SubscriberConnection(
            "session-1", "/test", context, queueCapacity: 64,
            onCompleted: (_, _) => Task.CompletedTask);

        // Enqueue the overlapping range (2-4) plus the new range (5-6).
        subscriber.TryEnqueue(l2.Duplicate());
        subscriber.TryEnqueue(l3.Duplicate());
        subscriber.TryEnqueue(l4.Duplicate());
        subscriber.TryEnqueue(l5.Duplicate());
        subscriber.TryEnqueue(l6.Duplicate());

        // Signal no further live data before starting the pump.
        await subscriber.CompleteAsync(SubscriberDisconnectReason.Completed);

        // Act
        await subscriber.StartAsync(snapshot, CancellationToken.None);
        await subscriber.Completion;

        // Assert: each unique chunk appears exactly once, in order.
        var received = body.ToArray();
        CollectionAssert.AreEqual(
            new byte[] { 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6 },
            received,
            "Overlapping live-queue chunks must be suppressed; each byte delivered exactly once.");
    }

    [TestMethod]
    public async Task PumpAsync_EmptySnapshot_DeliversAllLiveQueueChunks()
    {
        // A live-edge subscriber (empty snapshot, snapshotLastSequence = -1) must receive
        // every enqueued chunk without filtering.
        var buffer = new RingBuffer(maxBytes: 1024);
        using var l0 = buffer.Write(new byte[] { 0xD0 });
        using var l1 = buffer.Write(new byte[] { 0xD1 });
        using var l2 = buffer.Write(new byte[] { 0xD2 });

        var snapshot = buffer.CreateLiveEdgeSnapshot();

        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var subscriber = new SubscriberConnection(
            "session-2", "/test", context, queueCapacity: 64,
            onCompleted: (_, _) => Task.CompletedTask);

        subscriber.TryEnqueue(l0.Duplicate());
        subscriber.TryEnqueue(l1.Duplicate());
        subscriber.TryEnqueue(l2.Duplicate());

        await subscriber.CompleteAsync(SubscriberDisconnectReason.Completed);

        await subscriber.StartAsync(snapshot, CancellationToken.None);
        await subscriber.Completion;

        var received = body.ToArray();
        CollectionAssert.AreEqual(new byte[] { 0xD0, 0xD1, 0xD2 }, received);
    }
}
