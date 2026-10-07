using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class NotificationRetentionTests
{
    [TestMethod]
    public async Task Cleanup_RemovesOldTerminalHistory_ButKeepsEverythingRecoveryOrOperatorsStillNeed()
    {
        await using var h = await NotificationLifecycleHarness.CreateAsync();
        var now = h.Time.UtcNow;
        var old = now.AddDays(-40);

        await using (var db = h.NewContext())
        {
            var destinationId = (await db.NotificationDestinations.FirstAsync()).DestinationId;
            db.NotificationOccurrences.Add(new NotificationOccurrence
            {
                OccurrenceId = "o", OccurrenceKey = "o", NotificationKey = NotificationKeys.SystemRestarted, Kind = NotificationOccurrenceKinds.OneTime,
                Title = "t", Body = "b", OccurredUtc = old, CreatedUtc = old,
            });
            await db.SaveChangesAsync();

            string[] states =
            [
                NotificationDeliveryStates.Accepted, NotificationDeliveryStates.Suppressed, NotificationDeliveryStates.Dismissed,
                NotificationDeliveryStates.Failed, NotificationDeliveryStates.Uncertain, NotificationDeliveryStates.Pending,
                NotificationDeliveryStates.Claimed, NotificationDeliveryStates.RetryScheduled,
            ];
            foreach (var (state, i) in states.Select((s, i) => (s, i)))
            {
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    DeliveryId = $"d-{state}", OccurrenceId = "o", DestinationId = destinationId, ProviderKind = "matrix", TargetId = $"t{i}",
                    TargetLabel = "t", DeliveryIdentityRevision = 1, ConfigRevision = 1, State = state, DueUtc = old, PayloadTitle = "t",
                    PayloadBody = "b", MessageId = $"<{i}@x>", CreatedUtc = old, UpdatedUtc = old,
                });
            }

            db.NotificationConditionObservations.AddRange(
                new NotificationConditionObservation { EvidenceKey = "e", SubjectKind = "s", SubjectId = "x", Outcome = "ok", CompletedUtc = old, Consumed = true, ConsumedUtc = old },
                new NotificationConditionObservation { EvidenceKey = "e", SubjectKind = "s", SubjectId = "x", Outcome = "ok", CompletedUtc = old, Consumed = false });

            // A resolved incident whose accepted opening still owes a recovery must keep its acceptance record.
            db.NotificationIncidents.AddRange(
                Incident("owed", NotificationIncidentStates.Resolved, old),
                Incident("finished", NotificationIncidentStates.Resolved, old),
                Incident("active", NotificationIncidentStates.Active, old));
            await db.SaveChangesAsync();
            db.NotificationIncidentTargets.Add(new NotificationIncidentTarget
            {
                IncidentTargetId = "owed-t", IncidentId = "owed", Generation = 1, DestinationId = destinationId, TargetId = "t",
                DeliveryIdentityRevision = 1, OpeningAcceptedUtc = old, UpdatedUtc = old,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = h.NewContext())
        {
            var removed = await new NotificationRetention(db, h.Time).CleanupAsync(30, CancellationToken.None);
            Assert.IsGreaterThan(0, removed);
        }

        await using var verify = h.NewContext();
        var deliveries = (await verify.NotificationDeliveries.Select(d => d.State).ToListAsync()).Order().ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                NotificationDeliveryStates.Claimed, NotificationDeliveryStates.Failed, NotificationDeliveryStates.Pending,
                NotificationDeliveryStates.RetryScheduled, NotificationDeliveryStates.Uncertain,
            }.Order().ToArray(),
            deliveries, "Accepted, suppressed and dismissed history ages out; unfinished and undecided work never does.");
        Assert.AreEqual(1, await verify.NotificationConditionObservations.CountAsync(), "Only consumed evidence expires.");
        Assert.IsTrue(await verify.NotificationOccurrences.AnyAsync(), "An occurrence with remaining deliveries is kept.");

        var incidents = (await verify.NotificationIncidents.Select(i => i.IncidentId).ToListAsync()).Order().ToArray();
        CollectionAssert.AreEqual(new[] { "active", "owed" }, incidents, "Active incidents and records owing a recovery are preserved; finished ones are removed.");
        Assert.AreEqual(1, await verify.NotificationIncidentTargets.CountAsync());
    }

    private static NotificationIncident Incident(string id, string state, DateTime old) => new()
    {
        IncidentId = id, NotificationKey = NotificationKeys.EpgFetchFailed, SubjectKind = NotificationSubjectKinds.EpgSource, SubjectId = id,
        State = state, FirstUnhealthyUtc = old, LastObservedUtc = old, ResolvedUtc = state == NotificationIncidentStates.Active ? null : old, UpdatedUtc = old,
    };
}
