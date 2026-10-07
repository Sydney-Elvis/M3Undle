using M3Undle.Core.Epg;
using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class EpgNotificationLifecycleTests
{
    private static async Task<NotificationLifecycleHarness> NewAsync(int? scheduleHours = 6)
    {
        var h = await NotificationLifecycleHarness.CreateAsync(scheduleHours);
        await h.SeedSourceAsync();
        return h;
    }

    // ---------------------------------------------------------------- fetch failure lifecycle

    [TestMethod]
    public async Task FetchFailure_OpensOnlyAfterTheSustainedDelay()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        await h.RunAsync();
        h.Time.Advance(TimeSpan.FromMinutes(9));
        await h.RunAsync();

        Assert.HasCount(1, await h.IncidentsAsync(NotificationKeys.EpgFetchFailed));
        Assert.IsEmpty(await h.DeliveriesAsync(), "Nothing is sent before the failure has been sustained for the delay.");

        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.RunAsync();

        var delivery = (await h.DeliveriesAsync()).Single();
        Assert.AreEqual(NotificationOccurrenceKinds.Opening, delivery.Occurrence.Kind);
        Assert.AreEqual(NotificationDeliveryStates.Pending, delivery.Delivery.State);
        StringAssert.Contains(delivery.Delivery.PayloadBody, "http");
        Assert.DoesNotContain("secret", delivery.Delivery.PayloadBody);
    }

    [TestMethod]
    public async Task FailureThatRecoversBeforeTheDelay_SendsNothingAtAll()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "timeout");
        await h.RunAsync();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();
        h.Time.Advance(TimeSpan.FromHours(1));
        await h.RunAsync();

        var incident = (await h.IncidentsAsync()).Single();
        Assert.AreEqual(NotificationIncidentStates.Resolved, incident.State);
        Assert.IsEmpty(await h.DeliveriesAsync(), "A condition that never became sustained is never announced, nor recovered.");
    }

    [TestMethod]
    public async Task Recovery_IsQueuedOnlyAfterTheOpeningWasAccepted_AndOnlyForThatTarget()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Smtp, NotificationKeys.EpgFetchFailed);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();

        var openings = await h.DeliveriesAsync();
        Assert.HasCount(2, openings, "One independent delivery per recipient.");
        var acceptedOne = openings.First(x => x.Delivery.TargetLabel == "a@example.org");
        await h.AcceptAsync(acceptedOne.Delivery.DeliveryId);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Unchanged);
        await h.RunAsync();

        var all = await h.DeliveriesAsync();
        var other = all.Single(x => x.Delivery.TargetLabel == "b@example.org" && x.Occurrence.Kind == NotificationOccurrenceKinds.Opening);
        Assert.AreEqual(NotificationDeliveryStates.Suppressed, other.Delivery.State, "An unattempted opening is suppressed once the incident resolves.");
        Assert.AreEqual(NotificationSuppressionReasons.Resolved, other.Delivery.SuppressedReason);

        var recoveries = all.Where(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery).ToList();
        Assert.HasCount(1, recoveries);
        Assert.AreEqual("a@example.org", recoveries[0].Delivery.TargetLabel, "Only the recipient who got the opening gets the recovery.");

        await h.RunAsync();
        await h.RunAsync();
        Assert.AreEqual(1, (await h.DeliveriesAsync()).Count(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery), "Reconciling again never duplicates.");
    }

    [TestMethod]
    public async Task OpeningStillInFlightAtResolution_GetsItsRecoveryOnceAcceptanceIsPersisted()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        var opening = (await h.DeliveriesAsync()).Single();

        await using (var db = h.NewContext())
        {
            var d = await db.NotificationDeliveries.SingleAsync();
            d.State = NotificationDeliveryStates.Claimed;
            d.TransportStartedUtc = h.Time.UtcNow;
            await db.SaveChangesAsync();
        }

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();
        Assert.IsFalse((await h.DeliveriesAsync()).Any(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery),
            "No recovery while the opening's outcome is unknown.");

        await using (var db = h.NewContext())
        {
            var d = await db.NotificationDeliveries.Include(x => x.Occurrence).SingleAsync();
            d.State = NotificationDeliveryStates.Pending;
            d.TransportStartedUtc = null;
            await db.SaveChangesAsync();
        }
        await h.AcceptAsync(opening.Delivery.DeliveryId);
        await h.RunAsync();

        Assert.AreEqual(1, (await h.DeliveriesAsync()).Count(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery));
    }

    [TestMethod]
    public async Task Reminders_FollowTheInterval_AreUniquePerSequence_AndOnlyReachAcceptedTargets()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        await h.AcceptAsync((await h.DeliveriesAsync()).Single().Delivery.DeliveryId);

        h.Time.Advance(TimeSpan.FromHours(5));
        await h.RunAsync();
        Assert.AreEqual(0, (await h.DeliveriesAsync()).Count(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Reminder), "Too early.");

        h.Time.Advance(TimeSpan.FromHours(1.1));
        await h.RunAsync();
        await h.RunAsync();
        Assert.AreEqual(1, (await h.DeliveriesAsync()).Count(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Reminder), "Re-running does not repeat a reminder.");

        h.Time.Advance(TimeSpan.FromHours(30));
        await h.RunAsync();
        var reminders = (await h.DeliveriesAsync()).Where(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Reminder).ToList();
        Assert.HasCount(2, reminders, "Missed windows coalesce into one reminder, not five.");
        CollectionAssert.AreEqual(new[] { 1, 2 }, reminders.Select(r => r.Occurrence.Sequence).OrderBy(x => x).ToArray());
    }

    [TestMethod]
    public async Task RecurrenceStartsANewGeneration_WithItsOwnOpening()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);

        for (var round = 0; round < 2; round++)
        {
            await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
            h.Time.Advance(TimeSpan.FromMinutes(11));
            await h.RunAsync();
            var opening = (await h.DeliveriesAsync()).Single(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Opening
                && x.Delivery.State == NotificationDeliveryStates.Pending);
            await h.AcceptAsync(opening.Delivery.DeliveryId);
            await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Ok);
            await h.RunAsync();
            var recovery = (await h.DeliveriesAsync()).Single(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery
                && x.Delivery.State == NotificationDeliveryStates.Pending);
            await h.AcceptAsync(recovery.Delivery.DeliveryId);
        }

        var incidents = await h.IncidentsAsync(NotificationKeys.EpgFetchFailed);
        CollectionAssert.AreEqual(new[] { 1, 2 }, incidents.Select(i => i.Generation).ToArray());
        Assert.AreEqual(2, (await h.DeliveriesAsync()).Count(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Opening));
    }

    // ---------------------------------------------------------------- independence of the three conditions

    [TestMethod]
    public async Task Genuine304RecoversFetchHealth_WhileCoverageStaysDegraded()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed, NotificationKeys.EpgCoverageInsufficient);

        var expired = new[] { h.Interval(-30, -20) };
        await h.SetCoverageAsync("src-1", ("ch1", expired), ("ch2", expired));
        await h.PublishMappingsAsync("src-1", "ch1", "ch2");
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync(NotificationKeys.EpgFetchFailed)).Single().State);
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync(NotificationKeys.EpgCoverageInsufficient)).Single().State);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Unchanged);
        await h.RunAsync();
        await h.RunAsync();

        Assert.AreEqual(NotificationIncidentStates.Resolved, (await h.IncidentsAsync(NotificationKeys.EpgFetchFailed)).Single().State,
            "A 304 is a genuine successful check.");
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync(NotificationKeys.EpgCoverageInsufficient)).Single().State,
            "The cached programmes are still expired.");
    }

    [TestMethod]
    public async Task DismissingUiEvents_DoesNotChangeIncidentHealth()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();

        await using (var db = h.NewContext())
        {
            db.SystemEvents.Add(new SystemEvent
            {
                SystemEventId = "e1", EventType = SystemEventTypes_EpgFetchFailed, Severity = "Warning", Title = "x", OccurredAt = h.Time.UtcNow, EpgSourceId = "src-1",
            });
            await db.SaveChangesAsync();
            await db.SystemEvents.ExecuteDeleteAsync();
        }

        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync()).Single().State);
        Assert.HasCount(1, await h.DeliveriesAsync());
    }

    private const string SystemEventTypes_EpgFetchFailed = "EpgFetchFailed";

    // ---------------------------------------------------------------- overdue

    [TestMethod]
    public async Task Overdue_PersistsPastTheDeadlinePlusGrace_AndAnyCompletedAttemptEndsIt()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync(scheduleHours: 6);
        var checkedAt = h.Time.UtcNow;
        await h.SeedSourceAsync(configure: s => { s.LastCheckedUtc = checkedAt; s.LastCheckStatus = "ok"; });
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgRefreshOverdue);

        h.Time.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromMinutes(14)));
        await h.RunAsync();
        Assert.IsEmpty(await h.IncidentsAsync(NotificationKeys.EpgRefreshOverdue), "Inside the grace period.");

        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.RunAsync();
        var incident = (await h.IncidentsAsync(NotificationKeys.EpgRefreshOverdue)).Single();
        Assert.AreEqual(NotificationIncidentStates.Active, incident.State);
        Assert.AreEqual(checkedAt.AddHours(6), incident.FirstUnhealthyUtc, "The missed deadline is what is persisted.");
        Assert.AreEqual(1, (await h.DeliveriesAsync()).Count);

        h.Time.Advance(TimeSpan.FromHours(1));
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Resolved, (await h.IncidentsAsync(NotificationKeys.EpgRefreshOverdue)).Single().State,
            "Even a failed real attempt ends overdue; failure is a separate condition.");
    }

    [TestMethod]
    public async Task Overdue_NeverFiresForManualScheduleStandaloneSourcesOrLegacyEvidence()
    {
        await using var manual = await NotificationLifecycleHarness.CreateAsync(scheduleHours: null);
        await manual.SeedSourceAsync(configure: s => s.LastCheckedUtc = manual.Time.UtcNow);
        await manual.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgRefreshOverdue);
        manual.Time.Advance(TimeSpan.FromDays(10));
        await manual.RunAsync();
        Assert.IsEmpty(await manual.IncidentsAsync(), "A manual schedule means the runtime never promised a check.");

        await using var standalone = await NotificationLifecycleHarness.CreateAsync(scheduleHours: 6);
        await standalone.SeedSourceAsync(providerId: null, configure: s => s.LastCheckedUtc = standalone.Time.UtcNow);
        await standalone.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgRefreshOverdue);
        standalone.Time.Advance(TimeSpan.FromDays(10));
        await standalone.RunAsync();
        Assert.IsEmpty(await standalone.IncidentsAsync(), "Standalone sources are fetched by hand, never by the scheduler.");

        await using var legacy = await NotificationLifecycleHarness.CreateAsync(scheduleHours: 6);
        await legacy.SeedSourceAsync(configure: s => s.LastSuccessUtc = legacy.Time.UtcNow.AddDays(-3));
        await legacy.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgRefreshOverdue);
        await legacy.RunAsync();
        Assert.IsEmpty(await legacy.IncidentsAsync(), "Ambiguous pre-upgrade timestamps are Unknown, not overdue.");
    }

    // ---------------------------------------------------------------- coverage

    [TestMethod]
    public async Task Coverage_BelowWarningFractionOpensAfterDelay_AndNeedsTwoHealthyEvaluationsToRecover()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgCoverageInsufficient);

        var good = new[] { h.Interval(-1, 20) };
        var bad = new[] { h.Interval(-1, 3) };
        // 8 of 10 healthy = 80% < 90%.
        await h.SetCoverageAsync("src-1", Enumerable.Range(0, 10).Select(i => ($"ch{i}", i < 8 ? good : bad)).ToArray());
        await h.PublishMappingsAsync("src-1", Enumerable.Range(0, 10).Select(i => $"ch{i}").ToArray());
        await h.RunAsync();

        var incident = (await h.IncidentsAsync()).Single();
        Assert.AreEqual("Warning", incident.Severity);
        Assert.IsEmpty(await h.DeliveriesAsync(), "Ordinary coverage warnings honour the sustained delay.");

        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        Assert.HasCount(1, await h.DeliveriesAsync());

        // 9 of 10 healthy = 90%: above the warning threshold but below the 95% recovery threshold, so it stays open.
        await h.SetCoverageAsync("src-1", ("ch8", new[] { h.Interval(-1, 20) }));
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync()).Single().State);

        await h.SetCoverageAsync("src-1", ("ch9", new[] { h.Interval(-1, 20) }));
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Active, (await h.IncidentsAsync()).Single().State, "One healthy evaluation is not enough.");
        await h.RunAsync();
        Assert.AreEqual(NotificationIncidentStates.Resolved, (await h.IncidentsAsync()).Single().State);
    }

    [TestMethod]
    public async Task Coverage_NoUsableDataIsCritical_AndOpensWithoutTheFailureDelay()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgCoverageInsufficient);

        await h.SetCoverageAsync("src-1", ("ch1", new[] { h.Interval(-30, -20) }), ("ch2", Array.Empty<EpgInterval>()));
        await h.PublishMappingsAsync("src-1", "ch1", "ch2");
        await h.RunAsync();

        var incident = (await h.IncidentsAsync()).Single();
        Assert.AreEqual("Error", incident.Severity);
        var delivery = (await h.DeliveriesAsync()).Single();
        Assert.AreEqual("Error", delivery.Occurrence.Severity);
    }

    [TestMethod]
    public async Task Coverage_IrrelevantFarFutureProgrammesCannotMakeItHealthy_AndNoRelevantChannelsIsNotMonitored()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgCoverageInsufficient);

        await h.SetCoverageAsync("src-1",
            ("relevant", new[] { h.Interval(40, 60) }),
            ("irrelevant", new[] { h.Interval(-1, 100) }));
        await h.PublishMappingsAsync("src-1", "relevant");
        await h.RunAsync();
        Assert.AreEqual(1, (await h.IncidentsAsync()).Count, "Only far-future data for the relevant channel is not coverage.");

        await h.RemoveMappingsAsync();
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.RunAsync();
        var closed = (await h.IncidentsAsync()).Single();
        Assert.AreEqual(NotificationIncidentStates.Closed, closed.State, "Not monitored, rather than healthy or critical.");
    }

    // ---------------------------------------------------------------- routing, pause and subject removal

    [TestMethod]
    public async Task OffRoute_TracksHealthButNeverQueuesAnything_AndLaterEnableReconcilesCurrentState()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(30));
        await h.RunAsync();
        Assert.AreEqual(1, (await h.IncidentsAsync()).Count);
        Assert.IsEmpty(await h.DeliveriesAsync(), "Off affects external delivery only.");

        await using (var db = h.NewContext())
        {
            var route = await db.NotificationRoutes.SingleAsync(r => r.NotificationKey == NotificationKeys.EpgFetchFailed);
            route.DestinationId = (await db.NotificationDestinations.SingleAsync(d => d.Kind == NotificationProviderKinds.Matrix)).DestinationId;
            route.Revision++;
            (await db.NotificationSettings.SingleAsync()).ActivationEpoch++;
            await db.SaveChangesAsync();
        }

        await h.RunAsync();
        var opening = (await h.DeliveriesAsync()).Single();
        StringAssert.Contains(opening.Occurrence.Body, "has not updated successfully");
        StringAssert.Contains(opening.Occurrence.OccurrenceKey, ":open:e");
    }

    [TestMethod]
    public async Task PauseSuppressesUnclaimedWork_AndResumeRestoresOnlyEligibleRecoveries()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        await h.AcceptAsync((await h.DeliveriesAsync()).Single().Delivery.DeliveryId);

        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Ok);
        await h.RunAsync();
        Assert.AreEqual(NotificationDeliveryStates.Pending,
            (await h.DeliveriesAsync()).Single(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery).Delivery.State);

        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).Paused = true;
            await db.SaveChangesAsync();
        }
        await h.RunAsync();
        var paused = (await h.DeliveriesAsync()).Single(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery);
        Assert.AreEqual(NotificationDeliveryStates.Suppressed, paused.Delivery.State);
        Assert.AreEqual(NotificationSuppressionReasons.Paused, paused.Delivery.SuppressedReason);

        await using (var db = h.NewContext())
        {
            var s = await db.NotificationSettings.SingleAsync();
            s.Paused = false;
            s.ActivationEpoch++;
            await db.SaveChangesAsync();
        }
        await h.RunAsync();
        var resumed = (await h.DeliveriesAsync()).Single(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery);
        Assert.AreEqual(NotificationDeliveryStates.Pending, resumed.Delivery.State, "Recovery for an accepted opening is restored on resume.");
        Assert.AreEqual(1, (await h.DeliveriesAsync()).Count(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery));
    }

    [TestMethod]
    public async Task ChangingTheRoute_SuppressesObsoleteUnclaimedWork_WithoutRetargetingThePayload()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        var original = (await h.DeliveriesAsync()).Single().Delivery;

        await h.EnableAsync(NotificationProviderKinds.Smtp, NotificationKeys.EpgFetchFailed);
        await h.RunAsync();

        var all = await h.DeliveriesAsync();
        var old = all.Single(x => x.Delivery.DeliveryId == original.DeliveryId).Delivery;
        Assert.AreEqual(NotificationDeliveryStates.Suppressed, old.State);
        Assert.AreEqual(NotificationSuppressionReasons.RouteChanged, old.SuppressedReason);
        Assert.AreEqual(NotificationProviderKinds.Matrix, old.ProviderKind, "The old payload stays with its old destination.");
        var fresh = all.Where(x => x.Delivery.ProviderKind == NotificationProviderKinds.Smtp).ToList();
        Assert.HasCount(2, fresh, "A fresh current-state opening for each new recipient.");
    }

    [TestMethod]
    public async Task DisabledSource_ClosesItsIncidentsAsNoLongerMonitored_WithoutRecoveryNotice()
    {
        await using var h = await NewAsync();
        await h.EnableAsync(NotificationProviderKinds.Matrix, NotificationKeys.EpgFetchFailed);
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();
        await h.AcceptAsync((await h.DeliveriesAsync()).Single().Delivery.DeliveryId);

        await using (var db = h.NewContext())
        {
            (await db.EpgSources.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        await h.RunAsync();

        var incident = (await h.IncidentsAsync()).Single();
        Assert.AreEqual(NotificationIncidentStates.Closed, incident.State);
        Assert.IsFalse((await h.DeliveriesAsync()).Any(x => x.Occurrence.Kind == NotificationOccurrenceKinds.Recovery),
            "No longer monitored is not a success.");
    }

    [TestMethod]
    public async Task QueueCeiling_PreservesExistingWorkAndRaisesAVisibleCondition()
    {
        await using var h = await NewAsync();
        h.DeliveryCeiling = 1;
        await h.EnableAsync(NotificationProviderKinds.Smtp, NotificationKeys.EpgFetchFailed);
        await h.StageObservationAsync("src-1", NotificationObservationOutcomes.Failed, "http");
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.RunAsync();

        Assert.HasCount(1, await h.DeliveriesAsync(), "Only one of two recipients fits under the ceiling.");
        await using var db = h.NewContext();
        Assert.IsNotNull((await db.NotificationSettings.SingleAsync()).CapacitySuppressedUtc, "The rejection must be visible, not silent.");
        Assert.AreEqual(NotificationIncidentStates.Active, (await db.NotificationIncidents.SingleAsync()).State, "Incident health is unaffected by capacity.");
    }
}
