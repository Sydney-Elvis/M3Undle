using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// Sends due deliveries through registered providers. It owns claims, attempts, backoff and persisted outcomes; providers
/// only classify results. No database transaction is held while a transport is in use, and every revision the delivery was
/// bound to is checked again before it is claimed.
/// </summary>
public sealed class NotificationDeliveryProcessor(
    ApplicationDbContext db,
    NotificationProviderRegistry registry,
    NotificationDestinationAdapters adapters,
    NotificationConfigurationService configuration,
    NotificationSignal reconcileSignal,
    TimeProvider timeProvider,
    ILogger<NotificationDeliveryProcessor> logger)
{
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>Returns claims that expired. Without a transport-start marker nothing was sent, so they simply return to work.</summary>
    public async Task<int> RecoverExpiredClaimsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = await db.NotificationDeliveries
            .Where(d => d.State == NotificationDeliveryStates.Claimed && d.ClaimExpiresUtc != null && d.ClaimExpiresUtc < now)
            .ToListAsync(cancellationToken);

        foreach (var delivery in expired)
        {
            if (delivery.TransportStartedUtc is null)
            {
                delivery.State = NotificationDeliveryStates.Pending;
            }
            else if (registry.Find(delivery.ProviderKind)?.SupportsIdempotentRetry == true)
            {
                delivery.State = NotificationDeliveryStates.RetryScheduled;
                delivery.TransportStartedUtc = null;
                delivery.DueUtc = now;
            }
            else
            {
                delivery.State = NotificationDeliveryStates.Uncertain;
                delivery.ErrorCode = "claim_expired";
                delivery.ErrorText = "The worker stopped after the send began, so it is not known whether the message was accepted.";
                await RecordTargetUncertainAsync(delivery, cancellationToken);
            }

            delivery.ClaimOwner = null;
            delivery.ClaimExpiresUtc = null;
            delivery.UpdatedUtc = now;
            delivery.Revision++;
        }

        if (expired.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    /// <summary>Handles at most one due delivery for the provider kind. Returns true when it did something.</summary>
    public async Task<bool> ProcessNextAsync(string kind, string owner, CancellationToken hostToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(hostToken);
        if (settings is null || !NotificationRouting.IsSendingAllowed(settings))
            return false;

        var candidate = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.ProviderKind == kind
                && (d.State == NotificationDeliveryStates.Pending || d.State == NotificationDeliveryStates.RetryScheduled)
                && d.DueUtc <= now)
            .OrderBy(d => d.DueUtc).ThenBy(d => d.CreatedUtc)
            .FirstOrDefaultAsync(hostToken);
        if (candidate is null)
            return false;

        var obsoleteReason = await FindObsoleteReasonAsync(candidate, hostToken);
        if (obsoleteReason is not null)
        {
            await db.NotificationDeliveries
                .Where(d => d.DeliveryId == candidate.DeliveryId && d.Revision == candidate.Revision
                    && (d.State == NotificationDeliveryStates.Pending || d.State == NotificationDeliveryStates.RetryScheduled))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.State, NotificationDeliveryStates.Suppressed)
                    .SetProperty(d => d.SuppressedReason, obsoleteReason)
                    .SetProperty(d => d.UpdatedUtc, now)
                    .SetProperty(d => d.Revision, d => d.Revision + 1), hostToken);
            return true;
        }

        var leaseEnd = now + NotificationRetryPolicy.ClaimLease;
        var claimed = await db.NotificationDeliveries
            .Where(d => d.DeliveryId == candidate.DeliveryId && d.Revision == candidate.Revision
                && (d.State == NotificationDeliveryStates.Pending || d.State == NotificationDeliveryStates.RetryScheduled))
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.State, NotificationDeliveryStates.Claimed)
                .SetProperty(d => d.ClaimOwner, owner)
                .SetProperty(d => d.ClaimExpiresUtc, leaseEnd)
                .SetProperty(d => d.UpdatedUtc, now)
                .SetProperty(d => d.Revision, d => d.Revision + 1), hostToken);
        if (claimed == 0)
            return true;

        var provider = registry.Find(kind);
        var resolved = provider is null ? null : await configuration.ResolveAsync(kind, hostToken);
        if (provider is null || resolved is null)
        {
            await FinishAsync(candidate.DeliveryId, owner, NotificationDeliveryStates.Failed,
                provider is null ? "provider_unavailable" : "configuration_incomplete",
                provider is null ? "No provider is registered for this destination." : "The saved configuration is incomplete or its secret could not be read.",
                null, null, transportStarted: false, hostToken: CancellationToken.None);
            return true;
        }

        var current = await db.NotificationDeliveries.AsNoTracking()
            .Include(d => d.Occurrence)
            .FirstAsync(d => d.DeliveryId == candidate.DeliveryId, hostToken);
        if (resolved.ConfigRevision != current.ConfigRevision || resolved.DeliveryIdentityRevision != current.DeliveryIdentityRevision)
        {
            // The setup changed between validation and claim; nothing has been sent, so hand it back to be re-validated.
            await ReleaseClaimAsync(current.DeliveryId, owner, CancellationToken.None);
            return true;
        }

        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var started = await db.NotificationDeliveries
            .Where(d => d.DeliveryId == current.DeliveryId && d.ClaimOwner == owner && d.State == NotificationDeliveryStates.Claimed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.TransportStartedUtc, startedAt)
                .SetProperty(d => d.AttemptCount, d => d.AttemptCount + 1)
                .SetProperty(d => d.UpdatedUtc, startedAt)
                .SetProperty(d => d.Revision, d => d.Revision + 1), hostToken);
        if (started == 0)
            return true;

        var attemptsMade = current.AttemptCount + 1;
        var request = new NotificationSendRequest(
            current.DeliveryId,
            current.MessageId,
            new NotificationMessage(current.PayloadTitle, current.PayloadBody, current.Occurrence.Severity, current.PayloadLinkPath, current.Occurrence.OccurredUtc),
            resolved.Targets.First(t => t.TargetId == current.TargetId),
            current.ConfigRevision,
            resolved.Configuration);

        NotificationSendResult result;
        using var deadline = new CancellationTokenSource(NotificationRetryPolicy.NetworkDeadline, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken, deadline.Token);
        try
        {
            result = await provider.SendAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            // Host shutdown: never mark it processed. If the transport had begun, the boundary decides retry or uncertainty.
            await InterruptedAsync(current, owner, provider.SupportsIdempotentRetry);
            throw;
        }
        catch (OperationCanceledException)
        {
            result = provider.SupportsIdempotentRetry
                ? NotificationSendResult.Retryable("timeout", "The send did not finish before the deadline.")
                : NotificationSendResult.Uncertain("timeout", "The send did not finish before the deadline, so it may already have been accepted.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification provider {Kind} threw while sending delivery {DeliveryId}.", kind, current.DeliveryId);
            result = NotificationSendResult.Uncertain("provider_error", "The provider failed unexpectedly during the send.");
        }

        await ApplyResultAsync(current, owner, attemptsMade, result);
        reconcileSignal.Wake();
        return true;
    }

    private async Task ApplyResultAsync(NotificationDelivery delivery, string owner, int attemptsMade, NotificationSendResult result)
    {
        // Results are persisted even if the host is stopping: the outcome of a send that already happened must not be lost.
        var token = CancellationToken.None;
        switch (result.Outcome)
        {
            case NotificationSendOutcome.Accepted:
                await FinishAsync(delivery.DeliveryId, owner, NotificationDeliveryStates.Accepted, null, null, result.RemoteReference, null, true, token);
                break;

            case NotificationSendOutcome.RetryableFailure when attemptsMade < NotificationRetryPolicy.MaxAttemptsPerCycle:
                var delay = NotificationRetryPolicy.NextDelay(attemptsMade, result.RetryAfter, Jitter());
                await FinishAsync(delivery.DeliveryId, owner, NotificationDeliveryStates.RetryScheduled, result.ErrorCode, result.ErrorText, null,
                    timeProvider.GetUtcNow().UtcDateTime + delay, transportStarted: false, token);
                break;

            case NotificationSendOutcome.RetryableFailure:
                await FinishAsync(delivery.DeliveryId, owner, NotificationDeliveryStates.Failed, result.ErrorCode ?? "retries_exhausted",
                    "Automatic retries were exhausted. Use Retry to try again.", null, null, true, token);
                break;

            case NotificationSendOutcome.Uncertain:
                await FinishAsync(delivery.DeliveryId, owner, NotificationDeliveryStates.Uncertain, result.ErrorCode, result.ErrorText, null, null, true, token);
                break;

            default:
                await FinishAsync(delivery.DeliveryId, owner, NotificationDeliveryStates.Failed, result.ErrorCode ?? "permanent_failure", result.ErrorText, null, null, true, token);
                break;
        }
    }

    private async Task InterruptedAsync(NotificationDelivery delivery, string owner, bool idempotent)
    {
        var token = CancellationToken.None;
        if (idempotent)
        {
            await FinishAsync(delivery.DeliveryId, owner, NotificationDeliveryStates.RetryScheduled, "shutdown",
                "The application stopped during the send; it will be retried.", null, timeProvider.GetUtcNow().UtcDateTime, false, token);
        }
        else
        {
            await FinishAsync(delivery.DeliveryId, owner, NotificationDeliveryStates.Uncertain, "shutdown",
                "The application stopped during the send, so it is not known whether the message was accepted.", null, null, true, token);
        }
    }

    private async Task ReleaseClaimAsync(string deliveryId, string owner, CancellationToken token)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.NotificationDeliveries
            .Where(d => d.DeliveryId == deliveryId && d.ClaimOwner == owner && d.State == NotificationDeliveryStates.Claimed && d.TransportStartedUtc == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.State, NotificationDeliveryStates.Pending)
                .SetProperty(d => d.ClaimOwner, (string?)null)
                .SetProperty(d => d.ClaimExpiresUtc, (DateTime?)null)
                .SetProperty(d => d.UpdatedUtc, now)
                .SetProperty(d => d.Revision, d => d.Revision + 1), token);
    }

    private async Task FinishAsync(
        string deliveryId,
        string owner,
        string state,
        string? errorCode,
        string? errorText,
        string? remoteReference,
        DateTime? dueUtc,
        bool transportStarted,
        CancellationToken hostToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(hostToken);

        var accepted = state == NotificationDeliveryStates.Accepted;
        var safeText = errorText is null ? null : NotificationContent.Clean(errorText);
        var changed = await db.NotificationDeliveries
            .Where(d => d.DeliveryId == deliveryId && d.ClaimOwner == owner && d.State == NotificationDeliveryStates.Claimed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.State, state)
                .SetProperty(d => d.ClaimOwner, (string?)null)
                .SetProperty(d => d.ClaimExpiresUtc, (DateTime?)null)
                .SetProperty(d => d.AcceptedUtc, accepted ? now : (DateTime?)null)
                .SetProperty(d => d.RemoteReference, remoteReference)
                .SetProperty(d => d.ErrorCode, accepted ? null : errorCode)
                .SetProperty(d => d.ErrorText, accepted ? null : safeText)
                .SetProperty(d => d.DueUtc, dueUtc ?? now)
                .SetProperty(d => d.TransportStartedUtc, d => transportStarted ? d.TransportStartedUtc : null)
                .SetProperty(d => d.UpdatedUtc, now)
                .SetProperty(d => d.Revision, d => d.Revision + 1), hostToken);

        if (changed == 1)
        {
            var delivery = await db.NotificationDeliveries.AsNoTracking()
                .Include(d => d.Occurrence)
                .FirstAsync(d => d.DeliveryId == deliveryId, hostToken);
            if (accepted)
                await RecordTargetAcceptanceAsync(delivery, now, hostToken);
            else if (state == NotificationDeliveryStates.Uncertain)
                await RecordTargetUncertainAsync(delivery, hostToken);
            await db.SaveChangesAsync(hostToken);
        }

        await transaction.CommitAsync(hostToken);
    }

    private async Task RecordTargetAcceptanceAsync(NotificationDelivery delivery, DateTime now, CancellationToken cancellationToken)
    {
        var occurrence = delivery.Occurrence;
        if (occurrence.IncidentId is null || occurrence.Generation is null)
            return;

        var target = await FindOrAddTargetAsync(delivery, occurrence, now, cancellationToken);
        switch (occurrence.Kind)
        {
            case NotificationOccurrenceKinds.Opening:
                target.OpeningAcceptedUtc ??= now;
                target.OpeningUncertain = false;
                break;
            case NotificationOccurrenceKinds.Reminder:
                target.LastReminderSequence = Math.Max(target.LastReminderSequence, occurrence.Sequence);
                break;
            case NotificationOccurrenceKinds.Recovery:
                target.RecoveryQueuedUtc ??= now;
                target.RecoveryAcceptedUtc = now;
                break;
        }

        target.UpdatedUtc = now;
    }

    private async Task RecordTargetUncertainAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var occurrence = delivery.Occurrence
            ?? await db.NotificationOccurrences.AsNoTracking().FirstAsync(o => o.OccurrenceId == delivery.OccurrenceId, cancellationToken);
        if (occurrence.IncidentId is null || occurrence.Generation is null || occurrence.Kind != NotificationOccurrenceKinds.Opening)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var target = await FindOrAddTargetAsync(delivery, occurrence, now, cancellationToken);
        if (target.OpeningAcceptedUtc is null)
            target.OpeningUncertain = true;
        target.UpdatedUtc = now;
    }

    private async Task<NotificationIncidentTarget> FindOrAddTargetAsync(
        NotificationDelivery delivery, NotificationOccurrence occurrence, DateTime now, CancellationToken cancellationToken)
    {
        var generation = occurrence.Generation!.Value;
        var existing = db.NotificationIncidentTargets.Local.FirstOrDefault(x =>
                x.IncidentId == occurrence.IncidentId && x.Generation == generation && x.DestinationId == delivery.DestinationId
                && x.TargetId == delivery.TargetId && x.DeliveryIdentityRevision == delivery.DeliveryIdentityRevision)
            ?? await db.NotificationIncidentTargets.FirstOrDefaultAsync(x =>
                x.IncidentId == occurrence.IncidentId && x.Generation == generation && x.DestinationId == delivery.DestinationId
                && x.TargetId == delivery.TargetId && x.DeliveryIdentityRevision == delivery.DeliveryIdentityRevision, cancellationToken);
        if (existing is not null)
            return existing;

        var created = new NotificationIncidentTarget
        {
            IncidentTargetId = Guid.NewGuid().ToString(),
            IncidentId = occurrence.IncidentId!,
            Generation = generation,
            DestinationId = delivery.DestinationId,
            TargetId = delivery.TargetId,
            DeliveryIdentityRevision = delivery.DeliveryIdentityRevision,
            UpdatedUtc = now,
        };
        db.NotificationIncidentTargets.Add(created);
        return created;
    }

    /// <summary>The reason a still-unclaimed delivery should no longer be sent, or null when it is still current.</summary>
    private async Task<string?> FindObsoleteReasonAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var occurrence = await db.NotificationOccurrences.AsNoTracking()
            .FirstAsync(o => o.OccurrenceId == delivery.OccurrenceId, cancellationToken);
        var route = await db.NotificationRoutes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.NotificationKey == occurrence.NotificationKey, cancellationToken);
        var destination = await NotificationRouting.LoadDestinationAsync(db, delivery.DestinationId, cancellationToken);

        if (destination is null || !destination.Enabled)
            return NotificationSuppressionReasons.DestinationUnavailable;
        if (route?.DestinationId != destination.DestinationId)
            return NotificationSuppressionReasons.RouteChanged;
        if (destination.ConfigRevision != delivery.ConfigRevision
            || destination.VerifiedRevision != destination.ConfigRevision
            || destination.DeliveryIdentityRevision != delivery.DeliveryIdentityRevision)
            return NotificationSuppressionReasons.ConfigChanged;

        var adapter = adapters.Find(destination.Kind);
        if (adapter is null || !adapter.GetTargets(destination).Any(t => t.TargetId == delivery.TargetId))
            return NotificationSuppressionReasons.ConfigChanged;

        if (occurrence.Kind == NotificationOccurrenceKinds.Reminder && !route.SendReminders)
            return NotificationSuppressionReasons.RouteChanged;
        if (occurrence.Kind == NotificationOccurrenceKinds.Recovery && !route.SendRecovery)
            return NotificationSuppressionReasons.RouteChanged;

        if (occurrence.IncidentId is not null)
        {
            var state = await db.NotificationIncidents.AsNoTracking()
                .Where(i => i.IncidentId == occurrence.IncidentId)
                .Select(i => i.State)
                .FirstOrDefaultAsync(cancellationToken);
            if (state == NotificationIncidentStates.Closed)
                return NotificationSuppressionReasons.NoLongerMonitored;
            if (state == NotificationIncidentStates.Resolved
                && occurrence.Kind is NotificationOccurrenceKinds.Opening or NotificationOccurrenceKinds.Reminder)
                return NotificationSuppressionReasons.Resolved;
        }

        return null;
    }
}
