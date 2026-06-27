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
            writeStallTimeout: TimeSpan.FromSeconds(30),
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
            writeStallTimeout: TimeSpan.FromSeconds(30),
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

    [TestMethod]
    public async Task TryEnqueue_WhenQueueIsFull_ReturnsFalse()
    {
        var buffer = new RingBuffer(maxBytes: 1024);
        using var first = buffer.Write(new byte[] { 0xE0 });
        using var second = buffer.Write(new byte[] { 0xE1 });

        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var subscriber = new SubscriberConnection(
            "session-3", "/test", context, queueCapacity: 1,
            writeStallTimeout: TimeSpan.FromSeconds(30),
            onCompleted: (_, _) => Task.CompletedTask);

        var firstQueued = first.Duplicate();
        var secondQueued = second.Duplicate();

        Assert.IsTrue(subscriber.TryEnqueue(firstQueued));
        if (subscriber.TryEnqueue(secondQueued))
        {
            Assert.Fail("A full subscriber queue must report enqueue failure so the session can evict the slow client.");
        }

        secondQueued.Dispose();
        Assert.AreEqual(1, subscriber.QueueDepth);

        await subscriber.CompleteAsync(SubscriberDisconnectReason.Completed);

        await subscriber.StartAsync(buffer.CreateLiveEdgeSnapshot(), CancellationToken.None);
        await subscriber.Completion;

        CollectionAssert.AreEqual(new byte[] { 0xE0 }, body.ToArray());
    }

    [TestMethod]
    public void RegisterQueueOverflow_WithinGracePeriod_KeepsClientConnected()
    {
        var subscriber = CreateSubscriber(queueCapacity: 4);

        // A long grace means a transient backlog never asks the session to evict the client;
        // the first overflow opens the window and subsequent overflows stay within it.
        Assert.AreEqual(SubscriberQueueOverflowResult.EnteredGrace, subscriber.RegisterQueueOverflow(TimeSpan.FromHours(1)));
        Assert.AreEqual(SubscriberQueueOverflowResult.WithinGrace, subscriber.RegisterQueueOverflow(TimeSpan.FromHours(1)));
        Assert.AreEqual(SubscriberQueueOverflowResult.WithinGrace, subscriber.RegisterQueueOverflow(TimeSpan.FromHours(1)));
    }

    [TestMethod]
    public void RegisterQueueOverflow_AfterGraceElapsed_RequestsEviction()
    {
        var subscriber = CreateSubscriber(queueCapacity: 4);

        // With a zero grace, the first overflow opens the window and the next overflow has
        // already exceeded it, so the session is told to evict the stuck consumer.
        Assert.AreEqual(SubscriberQueueOverflowResult.EnteredGrace, subscriber.RegisterQueueOverflow(TimeSpan.Zero));
        Assert.AreEqual(SubscriberQueueOverflowResult.GraceExceeded, subscriber.RegisterQueueOverflow(TimeSpan.Zero));
    }

    [TestMethod]
    public void TryEnqueue_AfterDrainingBelowLowWater_ClearsGraceTimer()
    {
        var buffer = new RingBuffer(maxBytes: 1024);
        using var chunk = buffer.Write(new byte[] { 0xF0 });
        var subscriber = CreateSubscriber(queueCapacity: 4);

        // Open the grace window, then accept a chunk that leaves the queue below the
        // low-water mark — the client is keeping up again, so the timer must reset and the
        // next overflow starts a fresh window instead of immediately evicting.
        Assert.AreEqual(SubscriberQueueOverflowResult.EnteredGrace, subscriber.RegisterQueueOverflow(TimeSpan.Zero));
        Assert.IsTrue(subscriber.TryEnqueue(chunk.Duplicate()));
        Assert.AreEqual(SubscriberQueueOverflowResult.EnteredGrace, subscriber.RegisterQueueOverflow(TimeSpan.Zero));
    }

    [TestMethod]
    public void SetResyncPending_SetsFlag_AndClearResyncPending_ClearsIt()
    {
        var subscriber = CreateSubscriber(queueCapacity: 4);

        Assert.IsFalse(subscriber.IsResyncPending, "Resync should not be pending after construction.");

        subscriber.SetResyncPending();
        Assert.IsTrue(subscriber.IsResyncPending, "SetResyncPending must raise the flag.");

        subscriber.ClearResyncPending();
        Assert.IsFalse(subscriber.IsResyncPending, "ClearResyncPending must clear the flag.");
    }

    [TestMethod]
    public void ClearResyncPending_AlsoClearsGraceTimer()
    {
        var subscriber = CreateSubscriber(queueCapacity: 4);

        // Open the grace window, enter resync, clear resync — the next overflow should be
        // treated as a fresh EnteredGrace rather than a continuation of the old window.
        subscriber.RegisterQueueOverflow(TimeSpan.FromHours(1));
        subscriber.SetResyncPending();
        subscriber.ClearResyncPending();

        Assert.AreEqual(
            SubscriberQueueOverflowResult.EnteredGrace,
            subscriber.RegisterQueueOverflow(TimeSpan.FromHours(1)),
            "Grace timer must be reset by ClearResyncPending so the next overflow starts a fresh window.");
    }

    private static SubscriberConnection CreateSubscriber(int queueCapacity)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return new SubscriberConnection(
            "session-overflow", "/test", context, queueCapacity,
            writeStallTimeout: TimeSpan.FromSeconds(30),
            onCompleted: (_, _) => Task.CompletedTask);
    }
}
