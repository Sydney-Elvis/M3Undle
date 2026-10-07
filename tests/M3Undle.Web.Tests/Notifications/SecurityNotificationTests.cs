using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class SecurityNotificationTests
{
    private static NotificationSecurityRecorder Recorder(NotificationLifecycleHarness h, M3Undle.Web.Data.ApplicationDbContext db) =>
        new(db, new NotificationOccurrenceWriter(db, h.Time), h.Time);

    private static async Task RecordFailuresAsync(NotificationLifecycleHarness h, string identifier, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await using var db = h.NewContext();
            await Recorder(h, db).RecordFailedLoginAsync(identifier, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task FailedLogins_AreSummarisedOncePerAccountPerClosedWindow_WithCounts()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SecurityLoginFailed);

        // Start the clock at the beginning of a fixed window so the whole storm lands in one bucket.
        h.Time.Advance(NotificationSecurityRecorder.WindowStart(h.Time.UtcNow).AddMinutes(5) - h.Time.UtcNow);
        await RecordFailuresAsync(h, "alice@example.com", 7);
        await RecordFailuresAsync(h, "mallory", 2);

        await h.RunAsync();
        Assert.IsEmpty(await h.DeliveriesAsync(), "A window that is still open is not summarised yet.");

        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.RunAsync();
        await h.RunAsync();

        var summaries = (await h.DeliveriesAsync()).Select(d => d.Occurrence).OrderByDescending(o => o.Title).ToList();
        Assert.HasCount(2, summaries, "One summary per account, not one message per attempt.");
        Assert.IsTrue(summaries.Any(o => o.Title.StartsWith("7 ", StringComparison.Ordinal)));
        Assert.IsTrue(summaries.Any(o => o.Title.StartsWith("2 ", StringComparison.Ordinal)));

        await using var db = h.NewContext();
        Assert.IsTrue(await db.NotificationConditionObservations.Where(o => o.EvidenceKey == NotificationEvidenceKeys.LoginAttempt).AllAsync(o => o.Consumed));
    }

    [TestMethod]
    public async Task Summaries_NeverContainWhatWasTyped_OrAnAddressOrBrowser_AndTheSameAccountGroupsConsistently()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SecurityLoginFailed, NotificationKeys.SecurityAccountLocked);

        h.Time.Advance(NotificationSecurityRecorder.WindowStart(h.Time.UtcNow).AddMinutes(5) - h.Time.UtcNow);
        await RecordFailuresAsync(h, "Alice@Example.com", 2);
        await RecordFailuresAsync(h, "  alice@example.com ", 1);
        await using (var db = h.NewContext())
            await Recorder(h, db).RecordAccountLockedAsync("Alice@Example.com", h.Time.UtcNow.AddMinutes(15), CancellationToken.None);

        h.Time.Advance(TimeSpan.FromMinutes(6));
        await h.RunAsync();

        var all = await h.DeliveriesAsync();
        Assert.HasCount(2, all);
        var login = all.Single(d => d.Occurrence.NotificationKey == NotificationKeys.SecurityLoginFailed).Occurrence;
        StringAssert.Contains(login.Title, "3", "Case and surrounding whitespace do not split one account.");
        foreach (var (occurrence, delivery) in all)
        {
            var text = occurrence.Title + occurrence.Body + occurrence.SubjectLabel + occurrence.OccurrenceKey + delivery.PayloadBody;
            Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("example.com", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", text);
        }

        await using var verify = h.NewContext();
        var subject = await verify.NotificationConditionObservations.Select(o => o.SubjectId).Distinct().SingleAsync();
        Assert.AreEqual(64, subject.Length, "Observations carry a keyed hash, not the identifier.");
        Assert.DoesNotContain("alice", subject, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Lockout_IsCapturedOncePerTransition_NotForAttemptsWhileAlreadyLocked()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SecurityAccountLocked);
        var lockoutEnd = h.Time.UtcNow.AddMinutes(15);

        await using (var db = h.NewContext())
        {
            var recorder = Recorder(h, db);
            await recorder.RecordAccountLockedAsync("bob", lockoutEnd, CancellationToken.None);
            await recorder.RecordAccountLockedAsync("bob", lockoutEnd, CancellationToken.None); // the same transition reported again
        }

        await h.RunAsync();
        Assert.HasCount(1, await h.DeliveriesAsync());

        await using (var db = h.NewContext())
            await Recorder(h, db).RecordAccountLockedAsync("bob", lockoutEnd.AddHours(1), CancellationToken.None);
        await h.RunAsync();
        Assert.HasCount(2, await h.DeliveriesAsync(), "A new lockout is a new transition.");
    }

    [TestMethod]
    public async Task NothingIsRetained_WhileTheRowsAreOff_SendingIsDisabled_OrRestoredStateIsAwaitingActivation()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();

        await RecordFailuresAsync(h, "alice", 3);
        await using (var db = h.NewContext())
            await Recorder(h, db).RecordAccountLockedAsync("alice", h.Time.UtcNow.AddMinutes(5), CancellationToken.None);
        await using (var db = h.NewContext())
        {
            Assert.AreEqual(0, await db.NotificationConditionObservations.CountAsync(), "Defaults retain nothing.");
            Assert.AreEqual(0, await db.NotificationOccurrences.CountAsync());
        }

        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SecurityLoginFailed);
        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).RequiresActivation = true;
            await db.SaveChangesAsync();
        }

        await RecordFailuresAsync(h, "alice", 3);
        await using (var db = h.NewContext())
            Assert.AreEqual(0, await db.NotificationConditionObservations.CountAsync(), "A restored instance records nothing until activated.");

        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).RequiresActivation = false;
            await db.SaveChangesAsync();
        }

        await RecordFailuresAsync(h, "alice", 1);
        await using var verify = h.NewContext();
        Assert.AreEqual(1, await verify.NotificationConditionObservations.CountAsync());
    }

    [TestMethod]
    public async Task SettingsSeededBeforeTheSaltExisted_GetOneLazily_AndKeepIt()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.SecurityLoginFailed);
        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).IdentifierSalt = string.Empty;
            await db.SaveChangesAsync();
        }

        await RecordFailuresAsync(h, "alice", 1);
        string first;
        await using (var db = h.NewContext())
        {
            first = (await db.NotificationSettings.AsNoTracking().SingleAsync()).IdentifierSalt;
            Assert.IsFalse(string.IsNullOrEmpty(first));
        }

        await RecordFailuresAsync(h, "alice", 1);
        await using var verify = h.NewContext();
        Assert.AreEqual(first, (await verify.NotificationSettings.AsNoTracking().SingleAsync()).IdentifierSalt);
        Assert.AreEqual(1, await verify.NotificationConditionObservations.Select(o => o.SubjectId).Distinct().CountAsync());
    }
}
