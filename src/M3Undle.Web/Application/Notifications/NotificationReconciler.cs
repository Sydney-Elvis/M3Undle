using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// One pass of state reconciliation: consume evidence, evaluate time-based conditions, then converge every incident that
/// still has something to decide. Safe to run at any time and any number of times; it holds no transport.
/// </summary>
public sealed class NotificationReconciler(
    ApplicationDbContext db,
    EpgCoverageFacts coverageFacts,
    EpgNotificationEvaluator epgEvaluator,
    OperationalNotificationEvaluator operationalEvaluator,
    SecurityNotificationEvaluator securityEvaluator,
    NotificationIncidentService incidents,
    NotificationDeliveryService deliveryService,
    NotificationRouting routing,
    NotificationRetention retention,
    NotificationRuntimeState runtimeState,
    TimeProvider timeProvider,
    ILogger<NotificationReconciler> logger)
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await NotificationConfigurationService.EnsureSeededAsync(db, now, cancellationToken);

        // The settings row is a snapshot: capacity is persisted with a targeted update so a concurrent
        // administrator edit never turns a background pass into a concurrency failure.
        var settings = await db.NotificationSettings.AsNoTracking().SingleAsync(cancellationToken);
        var capacityBefore = settings.CapacitySuppressedUtc;

        await coverageFacts.RefreshRelevanceAsync(force: false, cancellationToken);
        await coverageFacts.BackfillMissingFromCacheAsync(cancellationToken);

        await epgEvaluator.ConsumeObservationsAsync(settings, cancellationToken);
        await epgEvaluator.CloseOrphanedIncidentsAsync(settings, cancellationToken);
        await epgEvaluator.EvaluateOverdueAsync(settings, cancellationToken);
        await epgEvaluator.EvaluateCoverageAsync(settings, cancellationToken);

        await operationalEvaluator.ConsumeObservationsAsync(settings, cancellationToken);
        await operationalEvaluator.CloseOrphanedAsync(cancellationToken);
        await operationalEvaluator.EvaluateStreamsAsync(settings, cancellationToken);
        await securityEvaluator.SummariseClosedWindowsAsync(cancellationToken);

        await ReconcileIncidentsAsync(settings, cancellationToken);
        await MaterializeOneTimeAsync(settings, cancellationToken);
        await deliveryService.ClearCapacityConditionIfRecoveredAsync(db, settings, cancellationToken);

        if (runtimeState.LastCleanupUtc is not { } lastCleanup || now - lastCleanup >= TimeSpan.FromHours(1))
        {
            runtimeState.LastCleanupUtc = now;
            var removed = await retention.CleanupAsync(settings.RetentionDays, cancellationToken);
            if (removed > 0)
                logger.LogInformation("Notification retention removed {Count} expired record(s).", removed);
        }

        if (settings.CapacitySuppressedUtc != capacityBefore)
        {
            await db.NotificationSettings.Where(x => x.Id == settings.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.CapacitySuppressedUtc, settings.CapacitySuppressedUtc), cancellationToken);
        }
    }

    public async Task ReconcileIncidentsAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        ids.UnionWith(await db.NotificationIncidents.AsNoTracking()
            .Where(x => x.State == NotificationIncidentStates.Active)
            .Select(x => x.IncidentId)
            .ToListAsync(cancellationToken));

        ids.UnionWith(await (
                from target in db.NotificationIncidentTargets.AsNoTracking()
                join incident in db.NotificationIncidents.AsNoTracking() on target.IncidentId equals incident.IncidentId
                where incident.State == NotificationIncidentStates.Resolved && target.Generation == incident.Generation
                      && target.OpeningAcceptedUtc != null && target.RecoveryQueuedUtc == null && !target.OpeningUncertain
                select incident.IncidentId)
            .ToListAsync(cancellationToken));

        ids.UnionWith(await (
                from delivery in db.NotificationDeliveries.AsNoTracking()
                join occurrence in db.NotificationOccurrences.AsNoTracking() on delivery.OccurrenceId equals occurrence.OccurrenceId
                where occurrence.IncidentId != null
                      && ((delivery.State == NotificationDeliveryStates.Pending && delivery.TransportStartedUtc == null)
                          || (delivery.State == NotificationDeliveryStates.RetryScheduled && delivery.TransportStartedUtc == null)
                          || (delivery.State == NotificationDeliveryStates.Suppressed
                              && delivery.SuppressedReason == NotificationSuppressionReasons.Paused
                              && occurrence.Kind == NotificationOccurrenceKinds.Recovery))
                select occurrence.IncidentId!)
            .Distinct()
            .ToListAsync(cancellationToken));

        foreach (var id in ids)
        {
            try
            {
                var incident = await db.NotificationIncidents.FirstOrDefaultAsync(x => x.IncidentId == id, cancellationToken);
                if (incident is null)
                    continue;
                await incidents.ReconcileAsync(incident, settings, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent writer won a unique identity; detach everything and let the next pass converge.
                logger.LogWarning(ex, "Notification incident {IncidentId} reconciliation lost a write race; it will be retried.", id);
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// One-time occurrences were captured under the policy at their commit. Delivery is decided by the current routing;
    /// anything that is no longer routable is closed out here rather than held for a later replay.
    /// </summary>
    public async Task MaterializeOneTimeAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pending = await db.NotificationOccurrences
            .Where(x => x.Kind == NotificationOccurrenceKinds.OneTime && x.MaterializedUtc == null)
            .OrderBy(x => x.CreatedUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var occurrence in pending)
        {
            var route = await db.NotificationRoutes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.NotificationKey == occurrence.NotificationKey, cancellationToken);
            var targets = await routing.GetEligibleTargetsAsync(db, settings, route, cancellationToken);
            if (targets.Count > 0 && route is not null)
                await deliveryService.MaterializeAsync(db, settings, occurrence, targets, route.Revision, now, cancellationToken);

            occurrence.MaterializedUtc = now;
        }

        if (pending.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }
}
