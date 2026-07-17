using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class DestructiveOperationLockTests
{
    [TestMethod]
    public void TryAcquire_WhenFree_SucceedsAndRecordsTheOperationName()
    {
        var sut = new DestructiveOperationLock();

        var acquired = sut.TryAcquire("backup", out var handle);

        Assert.IsTrue(acquired);
        Assert.IsNotNull(handle);
        Assert.AreEqual("backup", sut.CurrentOperation);
    }

    [TestMethod]
    public void TryAcquire_WhenAlreadyHeld_FailsWithoutReplacingTheHolder()
    {
        var sut = new DestructiveOperationLock();
        Assert.IsTrue(sut.TryAcquire("backup", out _));

        var acquired = sut.TryAcquire("restore", out var handle);

        Assert.IsFalse(acquired);
        Assert.IsNull(handle);
        Assert.AreEqual("backup", sut.CurrentOperation, "The original holder's name must not be overwritten by the failed attempt.");
    }

    [TestMethod]
    public void DisposingTheHandle_ReleasesTheLockForTheNextAcquirer()
    {
        var sut = new DestructiveOperationLock();
        Assert.IsTrue(sut.TryAcquire("backup", out var handle));

        handle!.Dispose();

        Assert.IsNull(sut.CurrentOperation);
        Assert.IsTrue(sut.TryAcquire("restore", out _));
        Assert.AreEqual("restore", sut.CurrentOperation);
    }

    [TestMethod]
    public void DisposingTheHandleTwice_ReleasesOnlyOnce()
    {
        var sut = new DestructiveOperationLock();
        Assert.IsTrue(sut.TryAcquire("backup", out var handle));

        handle!.Dispose();
        handle.Dispose();

        Assert.IsTrue(sut.TryAcquire("restore", out _), "A double-dispose must not over-release the semaphore and let two operations run at once.");
    }
}
