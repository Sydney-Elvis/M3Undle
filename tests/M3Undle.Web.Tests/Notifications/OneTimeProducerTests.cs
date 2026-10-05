using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class OneTimeProducerTests
{
    private static readonly string[] Migrations = ["20260101000000_First", "20260102000000_Second"];

    private static async Task CaptureAsync(NotificationLifecycleHarness h, string boot, string[] migrations)
    {
        await using var db = h.NewContext();
        await NotificationStartupCapture.CaptureAsync(db, new NotificationOccurrenceWriter(db, h.Time), "1.2.3", migrations, boot, h.Time.UtcNow, CancellationToken.None);
    }

    [TestMethod]
    public async Task Startup_IsCapturedOncePerBoot_WithGroupedMigrations_AndOnlyWhenRouted()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();

        await CaptureAsync(h, "boot-0", Migrations);
        await using (var db = h.NewContext())
            Assert.AreEqual(0, await db.NotificationOccurrences.CountAsync(), "Rows Off retain nothing.");

        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SystemRestarted, NotificationKeys.SystemMigrationsApplied);
        await CaptureAsync(h, "boot-1", Migrations);
        await CaptureAsync(h, "boot-1", Migrations);
        await CaptureAsync(h, "boot-2", []);

        await using var verify = h.NewContext();
        var occurrences = await verify.NotificationOccurrences.OrderBy(o => o.OccurrenceKey).ToListAsync();
        CollectionAssert.AreEqual(
            new[] { "system.migrations_applied:boot-1", "system.restarted:boot-1", "system.restarted:boot-2" },
            occurrences.Select(o => o.OccurrenceKey).ToArray(),
            "One restart per boot; migrations are one grouped occurrence for the boot that applied them, none when nothing applied.");
        var migrations = occurrences.Single(o => o.NotificationKey == NotificationKeys.SystemMigrationsApplied);
        StringAssert.Contains(migrations.Body, "2 database migration(s)");
        StringAssert.Contains(migrations.Body, "First, Second");
    }

    [TestMethod]
    public async Task Startup_OfARestoredInstance_IsNotAnnouncedUntilActivated()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SystemRestarted);
        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).RequiresActivation = true;
            await db.SaveChangesAsync();
        }

        await CaptureAsync(h, "boot-restored", Migrations);
        await using var verify = h.NewContext();
        Assert.AreEqual(0, await verify.NotificationOccurrences.CountAsync(), "A restored timeline's startup is not replayed.");
    }

    [TestMethod]
    public async Task OneTimeOccurrences_BecomeDeliveriesThroughCurrentRouting_AndNeverReplayAfterBeingDisabled()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SystemRestarted);
        await CaptureAsync(h, "boot-1", []);

        await using (var db = h.NewContext())
        {
            // Sending is switched off before the reconciler runs: the occurrence is closed out, not held for later.
            (await db.NotificationSettings.SingleAsync()).SendingEnabled = false;
            await db.SaveChangesAsync();
        }

        await h.RunAsync();
        Assert.IsEmpty(await h.DeliveriesAsync());

        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).SendingEnabled = true;
            await db.SaveChangesAsync();
        }

        await h.RunAsync();
        Assert.IsEmpty(await h.DeliveriesAsync(), "Re-enabling must not replay an event that happened while sending was off.");

        await CaptureAsync(h, "boot-2", []);
        await h.RunAsync();
        var delivery = (await h.DeliveriesAsync()).Single();
        Assert.AreEqual("system.restarted:boot-2", delivery.Occurrence.OccurrenceKey);
        Assert.AreEqual(NotificationOccurrenceKinds.OneTime, delivery.Occurrence.Kind);
    }
}
