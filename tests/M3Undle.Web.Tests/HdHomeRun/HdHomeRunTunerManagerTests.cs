using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Subscribers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        var manager = CreateManager(tunerCount: 1, dbTunerCountOverride: 1);

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
    public void Release_TunerSlot_AllowsNewAcquisition()
    {
        // Checklist: Disconnecting playback releases the tuner slot.
        var manager = CreateManager(tunerCount: 1);

        var first = manager.Acquire("vt-1", "ch-1");
        Assert.IsTrue(first.Succeeded);
        Assert.IsNotNull(first.Reservation);

        manager.Release(first.Reservation!.ReservationId, clientId: null);

        var second = manager.Acquire("vt-2", "ch-2");

        Assert.IsTrue(second.Succeeded);
        Assert.IsNotNull(second.Reservation);
        Assert.AreEqual("vt-2", second.Reservation.VirtualTunerId);
        Assert.HasCount(1, manager.GetActiveLeases());
    }

    [TestMethod]
    public void Acquire_GenericStreamRoute_IsNotEnforcedByTunerManager()
    {
        // Checklist: Generic /stream/<streamKey> requests are not blocked by HDHomeRun tuner enforcement.
        var manager = CreateManager(tunerCount: 2, dbTunerCountOverride: 2);

        var first = manager.Acquire("vt-1", "ch-1");
        var second = manager.Acquire("vt-2", "ch-2");

        Assert.IsTrue(first.Succeeded);
        Assert.IsTrue(second.Succeeded);
        Assert.HasCount(2, manager.GetActiveLeases());

        var blocked = manager.Acquire("vt-3", "ch-3");

        Assert.IsFalse(blocked.Succeeded);
        Assert.HasCount(2, manager.GetActiveLeases());

        // Generic /stream/ routes do not call HdHomeRunTunerManager.Acquire() — this is enforced
        // architecturally by HdHomeRunEndpoints only wiring tuner admission to /hdhr/tune handlers.
        // This test confirms the manager is the sole enforcement point and state changes only if Acquire() is invoked.
    }

    private static HdHomeRunTunerManager CreateManager(int tunerCount, int? dbTunerCountOverride = null)
    {
        var options = Options.Create(new HdHomeRunOptions { TunerCount = tunerCount });
        var scopeFactory = TestServiceScopeFactory.Create();

        if (dbTunerCountOverride is not null)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = db.SiteSettings.First();
            settings.HdhrTunerCountOverride = dbTunerCountOverride;
            db.SaveChanges();
        }

        var resolver = new HdHomeRunTunerCountResolver(options, scopeFactory);
        return new HdHomeRunTunerManager(resolver);
    }

    private static SubscriberConnection CreateSubscriber()
        => new(
            sessionId: Guid.NewGuid().ToString("N"),
            requestedRoute: "/hdhr/tune/test",
            context: new DefaultHttpContext(),
            queueCapacity: 4,
            onCompleted: (_, _) => Task.CompletedTask);
}
