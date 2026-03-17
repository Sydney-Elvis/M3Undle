using M3Undle.Web.Application;
using M3Undle.Web.Streaming.Subscribers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.HdHomeRun;

[TestClass]
public sealed class HdHomeRunTunerManagerTests
{
    [TestMethod]
    public void Acquire_FirstTuner_Succeeds()
    {
        var manager = CreateManager(tunerCount: 2);

        var result = manager.Acquire("tuner-a", "stream-1");

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Reservation);
        Assert.IsNull(result.PriorSubscriber);
        Assert.HasCount(1, manager.GetActiveLeases());
    }

    [TestMethod]
    public void Acquire_SameVirtualTuner_ReplacesPriorLeaseWithoutConsumingAnotherSlot()
    {
        var manager = CreateManager(tunerCount: 1);

        var first = manager.Acquire("tuner-a", "stream-1");
        var firstSubscriber = CreateSubscriber();
        manager.Activate(first.Reservation!, firstSubscriber, "Channel One");

        var second = manager.Acquire("tuner-a", "stream-2");

        Assert.IsTrue(second.Succeeded);
        Assert.AreSame(firstSubscriber, second.PriorSubscriber);
        Assert.HasCount(1, manager.GetActiveLeases());
        Assert.AreEqual("stream-2", manager.GetActiveLeases()[0].StreamKey);
    }

    [TestMethod]
    public void Acquire_DistinctTunersBeyondConfiguredCount_Fails()
    {
        var manager = CreateManager(tunerCount: 1);

        var first = manager.Acquire("tuner-a", "stream-1");
        Assert.IsTrue(first.Succeeded);

        var second = manager.Acquire("tuner-b", "stream-2");

        Assert.IsFalse(second.Succeeded);
        Assert.IsNull(second.Reservation);
        StringAssert.Contains(second.Error ?? string.Empty, "tuner slots");
    }

    [TestMethod]
    public void Activate_WithStaleReservation_DoesNotOverwriteCurrentLease()
    {
        var manager = CreateManager(tunerCount: 1);

        var first = manager.Acquire("tuner-a", "stream-1");
        var second = manager.Acquire("tuner-a", "stream-2");
        var staleSubscriber = CreateSubscriber();

        manager.Activate(first.Reservation!, staleSubscriber, "Stale Channel");

        var active = manager.GetActiveLeases().Single();
        Assert.AreEqual("stream-2", active.StreamKey);
        Assert.IsNull(active.ClientId);
        Assert.IsNull(active.ChannelName);
    }

    [TestMethod]
    public void Release_WithMismatchedClientId_DoesNotRemoveLease()
    {
        var manager = CreateManager(tunerCount: 1);

        var reservation = manager.Acquire("tuner-a", "stream-1").Reservation!;
        var subscriber = CreateSubscriber();
        manager.Activate(reservation, subscriber, "Channel One");

        manager.Release(reservation.ReservationId, clientId: "different-client");

        Assert.HasCount(1, manager.GetActiveLeases());
    }

    [TestMethod]
    public void Acquire_UsesEnvironmentOverride_ForTunerCount()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_TUNER_COUNT", "2");
        try
        {
            var manager = CreateManager(tunerCount: 1);

            var first = manager.Acquire("tuner-a", "stream-1");
            var second = manager.Acquire("tuner-b", "stream-2");

            Assert.IsTrue(first.Succeeded);
            Assert.IsTrue(second.Succeeded);
            Assert.HasCount(2, manager.GetActiveLeases());
        }
        finally
        {
            Environment.SetEnvironmentVariable("M3UNDLE_HDHR_TUNER_COUNT", null);
        }
    }

    private static HdHomeRunTunerManager CreateManager(int tunerCount)
        => new(
            Options.Create(new HdHomeRunOptions { TunerCount = tunerCount }),
            new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));

    private static SubscriberConnection CreateSubscriber()
        => new(
            sessionId: Guid.NewGuid().ToString("N"),
            requestedRoute: "/hdhr/tune/test",
            context: new DefaultHttpContext(),
            queueCapacity: 4,
            onCompleted: (_, _) => Task.CompletedTask);
}
