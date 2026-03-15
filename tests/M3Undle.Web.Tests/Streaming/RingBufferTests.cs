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
}
