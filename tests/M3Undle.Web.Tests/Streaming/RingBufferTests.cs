using M3Undle.Web.Streaming.Buffering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class RingBufferTests
{
    [TestMethod]
    public void WriteAndSnapshot_PreservesOrderAndUsage()
    {
        var buffer = new RingBuffer(maxBytes: 16);
        using var _ = buffer.Write(new byte[] { 1, 2, 3, 4 });
        using var __ = buffer.Write(new byte[] { 5, 6, 7 });

        var snapshot = buffer.CreateSnapshot();
        try
        {
            Assert.HasCount(2, snapshot.Chunks);
            Assert.AreEqual(7, snapshot.UsedBytes);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, snapshot.Chunks[0].Memory.ToArray());
            CollectionAssert.AreEqual(new byte[] { 5, 6, 7 }, snapshot.Chunks[1].Memory.ToArray());
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void Write_WhenCapacityExceeded_EvictsOldestBytes()
    {
        var buffer = new RingBuffer(maxBytes: 6);
        using var _ = buffer.Write(new byte[] { 1, 2, 3, 4 });
        using var __ = buffer.Write(new byte[] { 5, 6, 7, 8 });

        var snapshot = buffer.CreateSnapshot();
        try
        {
            Assert.HasCount(1, snapshot.Chunks);
            CollectionAssert.AreEqual(new byte[] { 5, 6, 7, 8 }, snapshot.Chunks[0].Memory.ToArray());
            Assert.AreEqual(4, snapshot.UsedBytes);
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void ResetGeneration_ClearsContentAndIncrementsGeneration()
    {
        var buffer = new RingBuffer(maxBytes: 16);
        using var _ = buffer.Write(new byte[] { 1, 2, 3 });

        var before = buffer.CreateSnapshot();
        foreach (var lease in before.Chunks)
            lease.Dispose();

        buffer.ResetGeneration();

        var after = buffer.CreateSnapshot();
        try
        {
            Assert.AreEqual(before.Generation + 1, after.Generation);
            Assert.IsEmpty(after.Chunks);
            Assert.AreEqual(0, after.UsedBytes);
        }
        finally
        {
            foreach (var lease in after.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void Write_Lease_RemainsValid_AfterChunkEviction()
    {
        // maxBytes=6, so a second 4-byte write forces the first chunk out of the ring.
        var buffer = new RingBuffer(maxBytes: 6);
        using var leaseA = buffer.Write(new byte[] { 10, 20, 30, 40 });

        // This write evicts chunk A from the ring, but the caller lease still retains it.
        using var leaseB = buffer.Write(new byte[] { 50, 60, 70, 80 });

        Assert.IsTrue(leaseA.HasValue);
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 40 }, leaseA.Memory.ToArray());
    }

    [TestMethod]
    public void Snapshot_RetainsChunks_AfterSubsequentEviction()
    {
        var buffer = new RingBuffer(maxBytes: 8);
        using var _ = buffer.Write(new byte[] { 1, 2, 3, 4 });

        var snapshot = buffer.CreateSnapshot();
        try
        {
            // Write enough data to evict the chunk captured in the snapshot.
            using var __ = buffer.Write(new byte[] { 5, 6, 7, 8, 9 });

            Assert.HasCount(1, snapshot.Chunks);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, snapshot.Chunks[0].Memory.ToArray());
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void Snapshot_RetainsChunks_AfterResetGeneration()
    {
        var buffer = new RingBuffer(maxBytes: 16);
        using var _ = buffer.Write(new byte[] { 1, 2, 3 });

        var snapshot = buffer.CreateSnapshot();
        try
        {
            buffer.ResetGeneration();

            Assert.HasCount(1, snapshot.Chunks);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, snapshot.Chunks[0].Memory.ToArray());
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void Complete_PreventsFurtherWrites()
    {
        var buffer = new RingBuffer(maxBytes: 16);
        buffer.Complete();

        Assert.ThrowsExactly<InvalidOperationException>(() => buffer.Write(new byte[] { 1, 2, 3 }));
    }

    [TestMethod]
    public void Complete_ClearsBuffer()
    {
        var buffer = new RingBuffer(maxBytes: 16);
        using var _ = buffer.Write(new byte[] { 1, 2, 3 });

        buffer.Complete();

        var snapshot = buffer.CreateSnapshot();
        try
        {
            Assert.IsEmpty(snapshot.Chunks);
            Assert.AreEqual(0, snapshot.UsedBytes);
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void Complete_SnapshotLeasesRemainValid()
    {
        var buffer = new RingBuffer(maxBytes: 16);
        using var _ = buffer.Write(new byte[] { 7, 8, 9 });

        var snapshot = buffer.CreateSnapshot();
        try
        {
            buffer.Complete();

            Assert.HasCount(1, snapshot.Chunks);
            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, snapshot.Chunks[0].Memory.ToArray());
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void ResetGeneration_AllowsWritesAndResetsSequence()
    {
        var buffer = new RingBuffer(maxBytes: 16);
        using var firstLease = buffer.Write(new byte[] { 1, 2 });

        var generationBefore = buffer.CurrentGeneration;
        buffer.ResetGeneration();

        using var secondLease = buffer.Write(new byte[] { 3, 4 });

        Assert.AreEqual(0, secondLease.Sequence);
        Assert.AreEqual(generationBefore + 1, secondLease.Generation);
    }

    // ---------------------------------------------------------------------------
    // Safe-start snapshot tests
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void CreateSafeStartSnapshot_WhenNoMarkSet_ReturnsEmptySnapshot()
    {
        var buffer = new RingBuffer(maxBytes: 64);
        using var _ = buffer.Write(new byte[] { 1, 2, 3 });

        var snapshot = buffer.CreateSafeStartSnapshot();
        try
        {
            Assert.IsEmpty(snapshot.Chunks);
            Assert.AreEqual(0, snapshot.UsedBytes);
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void MarkSafeStart_ByLease_SnapshotStartsFromMarkedChunk()
    {
        var buffer = new RingBuffer(maxBytes: 64);
        using var l0 = buffer.Write(new byte[] { 0xA0 });
        using var l1 = buffer.Write(new byte[] { 0xA1 });
        using var l2 = buffer.Write(new byte[] { 0xA2 });

        buffer.MarkSafeStart(l1);

        var snapshot = buffer.CreateSafeStartSnapshot();
        try
        {
            Assert.HasCount(2, snapshot.Chunks);
            CollectionAssert.AreEqual(new byte[] { 0xA1 }, snapshot.Chunks[0].Memory.ToArray());
            CollectionAssert.AreEqual(new byte[] { 0xA2 }, snapshot.Chunks[1].Memory.ToArray());
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void MarkSafeStart_ByGenerationAndSequence_SnapshotRespectsMarkedBoundary()
    {
        var buffer = new RingBuffer(maxBytes: 64);
        using var l0 = buffer.Write(new byte[] { 0xB0 });
        using var l1 = buffer.Write(new byte[] { 0xB1 });
        using var l2 = buffer.Write(new byte[] { 0xB2 });

        buffer.MarkSafeStart(l2.Generation, l2.Sequence);

        var snapshot = buffer.CreateSafeStartSnapshot();
        try
        {
            Assert.HasCount(1, snapshot.Chunks);
            CollectionAssert.AreEqual(new byte[] { 0xB2 }, snapshot.Chunks[0].Memory.ToArray());
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void MarkSafeStart_CanBeUpdatedToLaterSequence()
    {
        var buffer = new RingBuffer(maxBytes: 64);
        using var l0 = buffer.Write(new byte[] { 0xC0 });
        using var l1 = buffer.Write(new byte[] { 0xC1 });
        using var l2 = buffer.Write(new byte[] { 0xC2 });

        buffer.MarkSafeStart(l0);
        buffer.MarkSafeStart(l2);

        var snapshot = buffer.CreateSafeStartSnapshot();
        try
        {
            Assert.HasCount(1, snapshot.Chunks);
            CollectionAssert.AreEqual(new byte[] { 0xC2 }, snapshot.Chunks[0].Memory.ToArray());
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void MarkSafeStart_AfterResetGeneration_OldMarkCleared()
    {
        var buffer = new RingBuffer(maxBytes: 64);
        using var l0 = buffer.Write(new byte[] { 1 });
        buffer.MarkSafeStart(l0);

        buffer.ResetGeneration();

        var snapshot = buffer.CreateSafeStartSnapshot();
        try
        {
            Assert.IsEmpty(snapshot.Chunks);
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }

    [TestMethod]
    public void MarkSafeStart_WrongGeneration_IsIgnored()
    {
        var buffer = new RingBuffer(maxBytes: 64);
        using var l0 = buffer.Write(new byte[] { 1 });
        var gen0 = l0.Generation;
        var seq0 = l0.Sequence;

        buffer.ResetGeneration();
        using var l1 = buffer.Write(new byte[] { 2 });

        buffer.MarkSafeStart(gen0, seq0);

        var snapshot = buffer.CreateSafeStartSnapshot();
        try
        {
            Assert.IsEmpty(snapshot.Chunks);
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }
    }
}
