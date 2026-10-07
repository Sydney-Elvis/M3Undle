using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// Batched history cleanup. Terminal deliveries, consumed evidence and finished incidents age out after the retention window;
/// pending, claimed, failed and uncertain work, active incidents and acceptance records still needed for a recovery never do.
/// </summary>
public sealed class NotificationRetention(ApplicationDbContext db, TimeProvider timeProvider)
{
    private const int BatchSize = 500;

    public async Task<int> CleanupAsync(int retentionDays, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        var removed = 0;

        removed += await BatchedAsync(() => db.NotificationConditionObservations
            .Where(x => x.Consumed && x.ConsumedUtc != null && x.ConsumedUtc < cutoff)
            .OrderBy(x => x.ObservationId).Take(BatchSize), cancellationToken);

        removed += await BatchedAsync(() => db.NotificationDeliveries
            .Where(x => (x.State == NotificationDeliveryStates.Accepted || x.State == NotificationDeliveryStates.Suppressed
                         || x.State == NotificationDeliveryStates.Dismissed) && x.UpdatedUtc < cutoff)
            .OrderBy(x => x.UpdatedUtc).Take(BatchSize), cancellationToken);

        // An occurrence is removable once nothing hangs off it. Active incidents keep theirs for reminder numbering.
        removed += await BatchedAsync(() => db.NotificationOccurrences
            .Where(o => o.CreatedUtc < cutoff
                && !db.NotificationDeliveries.Any(d => d.OccurrenceId == o.OccurrenceId)
                && (o.IncidentId == null
                    || !db.NotificationIncidents.Any(i => i.IncidentId == o.IncidentId && i.State == NotificationIncidentStates.Active)))
            .OrderBy(o => o.CreatedUtc).Take(BatchSize), cancellationToken);

        // A finished incident's acceptance records are kept while a recovery could still be owed for them.
        var finished = await db.NotificationIncidents
            .Where(i => i.State != NotificationIncidentStates.Active && i.ResolvedUtc != null && i.ResolvedUtc < cutoff
                && !db.NotificationOccurrences.Any(o => o.IncidentId == i.IncidentId)
                && !db.NotificationIncidentTargets.Any(t => t.IncidentId == i.IncidentId && t.Generation == i.Generation
                    && i.State == NotificationIncidentStates.Resolved && t.OpeningAcceptedUtc != null
                    && t.RecoveryQueuedUtc == null && !t.OpeningUncertain))
            .OrderBy(i => i.ResolvedUtc).Take(BatchSize)
            .Select(i => i.IncidentId)
            .ToListAsync(cancellationToken);
        if (finished.Count > 0)
        {
            removed += await db.NotificationIncidentTargets.Where(t => finished.Contains(t.IncidentId)).ExecuteDeleteAsync(cancellationToken);
            removed += await db.NotificationIncidents.Where(i => finished.Contains(i.IncidentId)).ExecuteDeleteAsync(cancellationToken);
        }

        return removed;
    }

    private static async Task<int> BatchedAsync<T>(Func<IQueryable<T>> batch, CancellationToken cancellationToken) where T : class
    {
        var total = 0;
        while (true)
        {
            var deleted = await batch().ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
                return total;
        }
    }
}
