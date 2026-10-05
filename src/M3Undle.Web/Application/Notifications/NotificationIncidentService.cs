using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

public static class NotificationSuppressionReasons
{
    public const string Paused = "paused";
    public const string RouteChanged = "route_changed";
    public const string ConfigChanged = "config_changed";
    public const string Resolved = "resolved";
    public const string NoLongerMonitored = "no_longer_monitored";
    public const string DestinationUnavailable = "destination_unavailable";
}

/// <summary>
/// Incident lifecycle and per-target decisions. Everything is staged in the caller's DbContext and committed by the
/// caller; nothing here sends or waits on a transport. Reconciliation is idempotent, so running it again after a crash,
/// a configuration change or a resume converges on the same state without duplicating anything.
/// </summary>
public sealed class NotificationIncidentService(
    ApplicationDbContext db,
    NotificationDeliveryService deliveries,
    NotificationRouting routing,
    TimeProvider timeProvider)
{
    public async Task<NotificationIncident> EnsureActiveAsync(
        string notificationKey,
        string subjectKind,
        string subjectId,
        string? label,
        string severity,
        DateTime firstUnhealthyUtc,
        string? safeDetail,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var active = db.NotificationIncidents.Local.FirstOrDefault(x =>
                x.NotificationKey == notificationKey && x.SubjectId == subjectId && x.State == NotificationIncidentStates.Active)
            ?? await db.NotificationIncidents.FirstOrDefaultAsync(x =>
                x.NotificationKey == notificationKey && x.SubjectId == subjectId && x.State == NotificationIncidentStates.Active,
                cancellationToken);

        if (active is not null)
        {
            active.LastObservedUtc = now;
            active.SubjectLabel = label ?? active.SubjectLabel;
            active.SafeDetail = safeDetail ?? active.SafeDetail;
            if (severity == "Error")
                active.Severity = severity;
            active.UpdatedUtc = now;
            return active;
        }

        var lastGeneration = await db.NotificationIncidents
            .Where(x => x.NotificationKey == notificationKey && x.SubjectId == subjectId)
            .MaxAsync(x => (int?)x.Generation, cancellationToken) ?? 0;

        var incident = new NotificationIncident
        {
            IncidentId = Guid.NewGuid().ToString(),
            NotificationKey = notificationKey,
            SubjectKind = subjectKind,
            SubjectId = subjectId,
            SubjectLabel = label,
            Generation = lastGeneration + 1,
            State = NotificationIncidentStates.Active,
            Severity = severity,
            FirstUnhealthyUtc = firstUnhealthyUtc,
            LastObservedUtc = now,
            SafeDetail = safeDetail,
            UpdatedUtc = now,
        };
        db.NotificationIncidents.Add(incident);
        return incident;
    }

    public async Task<NotificationIncident?> FindActiveAsync(string notificationKey, string subjectId, CancellationToken cancellationToken) =>
        db.NotificationIncidents.Local.FirstOrDefault(x =>
            x.NotificationKey == notificationKey && x.SubjectId == subjectId && x.State == NotificationIncidentStates.Active)
        ?? await db.NotificationIncidents.FirstOrDefaultAsync(x =>
            x.NotificationKey == notificationKey && x.SubjectId == subjectId && x.State == NotificationIncidentStates.Active,
            cancellationToken);

    public void Resolve(NotificationIncident incident, DateTime resolvedUtc, string reason)
    {
        incident.State = NotificationIncidentStates.Resolved;
        incident.ResolvedUtc = resolvedUtc;
        incident.Reason = reason;
        incident.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
    }

    /// <summary>The subject is gone or no longer monitored. This is not a recovery and sends no success notice.</summary>
    public void Close(NotificationIncident incident, string reason)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        incident.State = NotificationIncidentStates.Closed;
        incident.ResolvedUtc = now;
        incident.Reason = reason;
        incident.UpdatedUtc = now;
    }

    public async Task ReconcileAsync(NotificationIncident incident, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var route = await db.NotificationRoutes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.NotificationKey == incident.NotificationKey, cancellationToken);
        var targets = await routing.GetEligibleTargetsAsync(db, settings, route, cancellationToken);
        var sendingAllowed = NotificationRouting.IsSendingAllowed(settings);

        await SuppressObsoleteAsync(incident, targets, sendingAllowed, cancellationToken);

        switch (incident.State)
        {
            case NotificationIncidentStates.Active:
                MarkSustained(incident, settings, route, now);
                if (incident.OpenedUtc is not null && targets.Count > 0 && route is not null)
                {
                    await EnsureOpeningAsync(incident, settings, route, targets, now, cancellationToken);
                    await EnsureReminderAsync(incident, settings, route, targets, now, cancellationToken);
                }
                break;

            case NotificationIncidentStates.Resolved:
                await ResurrectPausedRecoveriesAsync(incident, targets, now, cancellationToken);
                if (route is { SendRecovery: true } && targets.Count > 0)
                    await EnsureRecoveryAsync(incident, settings, route, targets, now, cancellationToken);
                break;
        }
    }

    private void MarkSustained(NotificationIncident incident, NotificationSettings settings, NotificationRoute? route, DateTime now)
    {
        if (incident.OpenedUtc is not null)
            return;

        // Stream thresholds are catalog policy values, fixed to keep the tuning surface small; everything else uses the setting.
        var delay = NotificationCatalog.Find(incident.NotificationKey)?.FixedSustainedDelay
            ?? TimeSpan.FromMinutes(NotificationRouting.EffectiveFailureDelayMinutes(settings, route));
        var critical = incident.Severity == "Error";
        if (critical || now - incident.FirstUnhealthyUtc >= delay)
        {
            incident.OpenedUtc = now;
            incident.UpdatedUtc = now;
        }
    }

    private async Task EnsureOpeningAsync(
        NotificationIncident incident,
        NotificationSettings settings,
        NotificationRoute route,
        IReadOnlyList<EligibleTarget> targets,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var pending = new List<EligibleTarget>();
        foreach (var target in targets)
        {
            if (await HasAcceptedOpeningAsync(incident, target, cancellationToken))
                continue;
            if (await HasOutstandingOpeningAsync(incident, target, cancellationToken))
                continue;
            pending.Add(target);
        }

        if (pending.Count == 0)
            return;

        var key = $"{incident.NotificationKey}:{incident.IncidentId}:g{incident.Generation}:open:e{settings.ActivationEpoch}";
        var occurrence = await GetOrAddOccurrenceAsync(key, incident, NotificationOccurrenceKinds.Opening, 0, settings, route, now,
            NotificationContent.Opening(incident), cancellationToken);
        await deliveries.MaterializeAsync(db, settings, occurrence, pending, route.Revision, now, cancellationToken);
    }

    private async Task EnsureReminderAsync(
        NotificationIncident incident,
        NotificationSettings settings,
        NotificationRoute route,
        IReadOnlyList<EligibleTarget> targets,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!route.SendReminders || incident.OpenedUtc is null)
            return;

        var interval = TimeSpan.FromHours(NotificationRouting.EffectiveReminderIntervalHours(settings, route));
        var baseline = incident.LastReminderUtc ?? incident.OpenedUtc.Value;
        if (now - baseline < interval)
            return;

        var accepted = new List<EligibleTarget>();
        foreach (var target in targets)
        {
            if (await HasAcceptedOpeningAsync(incident, target, cancellationToken))
                accepted.Add(target);
        }

        if (accepted.Count == 0)
            return;

        var sequence = incident.ReminderSequence + 1;
        var key = $"{incident.NotificationKey}:{incident.IncidentId}:g{incident.Generation}:reminder:{sequence}";
        var occurrence = await GetOrAddOccurrenceAsync(key, incident, NotificationOccurrenceKinds.Reminder, sequence, settings, route, now,
            NotificationContent.Reminder(incident, now), cancellationToken);
        await deliveries.MaterializeAsync(db, settings, occurrence, accepted, route.Revision, now, cancellationToken);

        // Missed windows coalesce: one reminder per elapsed check, however many intervals passed.
        incident.ReminderSequence = sequence;
        incident.LastReminderUtc = now;
        incident.UpdatedUtc = now;
    }

    private async Task EnsureRecoveryAsync(
        NotificationIncident incident,
        NotificationSettings settings,
        NotificationRoute route,
        IReadOnlyList<EligibleTarget> targets,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var rows = await db.NotificationIncidentTargets
            .Where(x => x.IncidentId == incident.IncidentId && x.Generation == incident.Generation
                && x.OpeningAcceptedUtc != null && x.RecoveryQueuedUtc == null && !x.OpeningUncertain)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
            return;

        var recipients = new List<EligibleTarget>();
        foreach (var row in rows)
        {
            var target = targets.FirstOrDefault(t =>
                t.DestinationId == row.DestinationId && t.TargetId == row.TargetId
                && t.DeliveryIdentityRevision == row.DeliveryIdentityRevision);
            if (target is null)
                continue; // Removed or changed target: a recovery is never sent to an identity that did not get the opening.
            recipients.Add(target);
            row.RecoveryQueuedUtc = now;
            row.UpdatedUtc = now;
        }

        if (recipients.Count == 0)
            return;

        var resolvedUtc = incident.ResolvedUtc ?? now;
        var key = $"{incident.NotificationKey}:{incident.IncidentId}:g{incident.Generation}:recovery";
        var occurrence = await GetOrAddOccurrenceAsync(key, incident, NotificationOccurrenceKinds.Recovery, 0, settings, route, resolvedUtc,
            NotificationContent.Recovery(incident, resolvedUtc), cancellationToken);
        await deliveries.MaterializeAsync(db, settings, occurrence, recipients, route.Revision, now, cancellationToken);
    }

    private async Task SuppressObsoleteAsync(
        NotificationIncident incident,
        IReadOnlyList<EligibleTarget> targets,
        bool sendingAllowed,
        CancellationToken cancellationToken)
    {
        var occurrenceIds = await db.NotificationOccurrences.AsNoTracking()
            .Where(x => x.IncidentId == incident.IncidentId)
            .Select(x => new { x.OccurrenceId, x.Kind })
            .ToListAsync(cancellationToken);
        if (occurrenceIds.Count == 0)
            return;

        var kindById = occurrenceIds.ToDictionary(x => x.OccurrenceId, x => x.Kind, StringComparer.Ordinal);
        var ids = kindById.Keys.ToList();
        var unclaimed = await db.NotificationDeliveries
            .Where(x => ids.Contains(x.OccurrenceId)
                && (x.State == NotificationDeliveryStates.Pending || x.State == NotificationDeliveryStates.RetryScheduled)
                && x.TransportStartedUtc == null)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var delivery in unclaimed)
        {
            var kind = kindById[delivery.OccurrenceId];
            string? reason = null;

            if (incident.State == NotificationIncidentStates.Closed)
                reason = NotificationSuppressionReasons.NoLongerMonitored;
            else if (incident.State == NotificationIncidentStates.Resolved && kind is NotificationOccurrenceKinds.Opening or NotificationOccurrenceKinds.Reminder)
                reason = NotificationSuppressionReasons.Resolved;
            else if (!sendingAllowed)
                reason = NotificationSuppressionReasons.Paused;
            else if (!targets.Any(t => t.DestinationId == delivery.DestinationId && t.TargetId == delivery.TargetId
                         && t.DeliveryIdentityRevision == delivery.DeliveryIdentityRevision))
                reason = targets.Any(t => t.DestinationId == delivery.DestinationId)
                    ? NotificationSuppressionReasons.ConfigChanged
                    : NotificationSuppressionReasons.RouteChanged;
            else if (targets.Any(t => t.DestinationId == delivery.DestinationId && t.ConfigRevision != delivery.ConfigRevision))
                reason = NotificationSuppressionReasons.ConfigChanged;

            if (reason is null)
                continue;

            Suppress(delivery, reason, now);
        }
    }

    private async Task ResurrectPausedRecoveriesAsync(
        NotificationIncident incident,
        IReadOnlyList<EligibleTarget> targets,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return;

        var recoveryOccurrenceIds = await db.NotificationOccurrences.AsNoTracking()
            .Where(x => x.IncidentId == incident.IncidentId && x.Generation == incident.Generation && x.Kind == NotificationOccurrenceKinds.Recovery)
            .Select(x => x.OccurrenceId)
            .ToListAsync(cancellationToken);
        if (recoveryOccurrenceIds.Count == 0)
            return;

        var suppressed = await db.NotificationDeliveries
            .Where(x => recoveryOccurrenceIds.Contains(x.OccurrenceId)
                && x.State == NotificationDeliveryStates.Suppressed
                && x.SuppressedReason == NotificationSuppressionReasons.Paused)
            .ToListAsync(cancellationToken);

        foreach (var delivery in suppressed)
        {
            var stillEligible = targets.Any(t => t.DestinationId == delivery.DestinationId && t.TargetId == delivery.TargetId
                && t.DeliveryIdentityRevision == delivery.DeliveryIdentityRevision && t.ConfigRevision == delivery.ConfigRevision);
            if (!stillEligible)
                continue;

            delivery.State = NotificationDeliveryStates.Pending;
            delivery.SuppressedReason = null;
            delivery.DueUtc = now;
            delivery.UpdatedUtc = now;
            delivery.Revision++;
        }
    }

    private async Task<bool> HasAcceptedOpeningAsync(NotificationIncident incident, EligibleTarget target, CancellationToken cancellationToken)
    {
        if (db.NotificationIncidentTargets.Local.Any(x => Matches(x, incident, target) && x.OpeningAcceptedUtc is not null))
            return true;
        return await db.NotificationIncidentTargets.AnyAsync(x =>
            x.IncidentId == incident.IncidentId && x.Generation == incident.Generation && x.DestinationId == target.DestinationId
            && x.TargetId == target.TargetId && x.DeliveryIdentityRevision == target.DeliveryIdentityRevision
            && x.OpeningAcceptedUtc != null, cancellationToken);
    }

    // An opening already queued, in flight, retrying or awaiting a decision for this target must not be duplicated by a
    // fresh occurrence from a newer activation epoch.
    private Task<bool> HasOutstandingOpeningAsync(NotificationIncident incident, EligibleTarget target, CancellationToken cancellationToken) =>
        (from d in db.NotificationDeliveries
         join o in db.NotificationOccurrences on d.OccurrenceId equals o.OccurrenceId
         where o.IncidentId == incident.IncidentId && o.Generation == incident.Generation
               && o.Kind == NotificationOccurrenceKinds.Opening
               && d.DestinationId == target.DestinationId && d.TargetId == target.TargetId
               && d.DeliveryIdentityRevision == target.DeliveryIdentityRevision
               && (d.State == NotificationDeliveryStates.Pending || d.State == NotificationDeliveryStates.Claimed
                   || d.State == NotificationDeliveryStates.RetryScheduled
                   // A failed or undecided attempt blocks a duplicate only while the configuration it used is unchanged;
                   // once the setup has been edited and re-verified, a fresh current-state opening is the right answer.
                   || ((d.State == NotificationDeliveryStates.Uncertain || d.State == NotificationDeliveryStates.Failed)
                       && d.ConfigRevision == target.ConfigRevision))
         select d.DeliveryId).AnyAsync(cancellationToken);

    private async Task<NotificationOccurrence> GetOrAddOccurrenceAsync(
        string key,
        NotificationIncident incident,
        string kind,
        int sequence,
        NotificationSettings settings,
        NotificationRoute route,
        DateTime occurredUtc,
        NotificationText text,
        CancellationToken cancellationToken)
    {
        var existing = db.NotificationOccurrences.Local.FirstOrDefault(x => x.OccurrenceKey == key)
            ?? await db.NotificationOccurrences.FirstOrDefaultAsync(x => x.OccurrenceKey == key, cancellationToken);
        if (existing is not null)
            return existing;

        var occurrence = new NotificationOccurrence
        {
            OccurrenceId = Guid.NewGuid().ToString(),
            OccurrenceKey = key,
            NotificationKey = incident.NotificationKey,
            IncidentId = incident.IncidentId,
            Generation = incident.Generation,
            Kind = kind,
            Sequence = sequence,
            PolicyRevision = route.Revision,
            ActivationEpoch = settings.ActivationEpoch,
            Severity = text.Severity,
            Title = text.Title,
            Body = text.Body,
            SubjectLabel = incident.SubjectLabel,
            LinkPath = LinkFor(incident),
            OccurredUtc = occurredUtc,
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
        };
        db.NotificationOccurrences.Add(occurrence);
        return occurrence;
    }

    private static string? LinkFor(NotificationIncident incident) =>
        incident.NotificationKey switch
        {
            NotificationKeys.EpgFetchFailed or NotificationKeys.EpgRefreshOverdue or NotificationKeys.EpgCoverageInsufficient => "/epg",
            NotificationKeys.ProviderFetchFailed => "/providers",
            NotificationKeys.DownstreamRefreshFailed => "/settings?section=integrations",
            NotificationKeys.StreamUnstable => "/streams",
            _ => null,
        };

    internal static void Suppress(NotificationDelivery delivery, string reason, DateTime now)
    {
        delivery.State = NotificationDeliveryStates.Suppressed;
        delivery.SuppressedReason = reason;
        delivery.ClaimOwner = null;
        delivery.ClaimExpiresUtc = null;
        delivery.UpdatedUtc = now;
        delivery.Revision++;
    }

    private static bool Matches(NotificationIncidentTarget row, NotificationIncident incident, EligibleTarget target) =>
        row.IncidentId == incident.IncidentId && row.Generation == incident.Generation && row.DestinationId == target.DestinationId
        && row.TargetId == target.TargetId && row.DeliveryIdentityRevision == target.DeliveryIdentityRevision;
}
