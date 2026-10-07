using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

public enum DeliveryActionStatus
{
    Ok,
    NotFound,

    /// <summary>The revision is stale, or the delivery is not in a state that allows this action.</summary>
    Conflict,

    /// <summary>The request itself is incomplete, for example a missing duplicate-risk acknowledgement.</summary>
    Invalid,
}

public sealed record DeliveryActionResult(DeliveryActionStatus Status, string? Message = null);

/// <summary>Explicit administrator actions on terminal deliveries. They never retarget: a retry keeps its bound destination.</summary>
public sealed class NotificationDeliveryActions(ApplicationDbContext db, NotificationSignal reconcileSignal, TimeProvider timeProvider)
{
    public async Task<DeliveryActionResult> RetryAsync(
        string deliveryId, int expectedRevision, bool acknowledgeDuplicateRisk, CancellationToken cancellationToken)
    {
        var delivery = await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.DeliveryId == deliveryId, cancellationToken);
        if (delivery is null)
            return new(DeliveryActionStatus.NotFound);
        if (delivery.Revision != expectedRevision)
            return new(DeliveryActionStatus.Conflict, "The delivery changed. Reload and try again.");
        if (delivery.State is not (NotificationDeliveryStates.Failed or NotificationDeliveryStates.Uncertain))
            return new(DeliveryActionStatus.Conflict, "Only failed or uncertain deliveries can be retried.");
        if (delivery.State == NotificationDeliveryStates.Uncertain && !acknowledgeDuplicateRisk)
            return new(DeliveryActionStatus.Invalid, "The message may already have been accepted. Retrying can deliver it twice; acknowledge that to continue.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var changed = await db.NotificationDeliveries
            .Where(d => d.DeliveryId == deliveryId && d.Revision == expectedRevision
                && (d.State == NotificationDeliveryStates.Failed || d.State == NotificationDeliveryStates.Uncertain))
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.State, NotificationDeliveryStates.Pending)
                .SetProperty(d => d.CycleNumber, d => d.CycleNumber + 1)
                .SetProperty(d => d.AttemptCount, 0)
                .SetProperty(d => d.DueUtc, now)
                .SetProperty(d => d.ErrorCode, (string?)null)
                .SetProperty(d => d.ErrorText, (string?)null)
                .SetProperty(d => d.TransportStartedUtc, (DateTime?)null)
                .SetProperty(d => d.UpdatedUtc, now)
                .SetProperty(d => d.Revision, d => d.Revision + 1), cancellationToken);

        if (changed == 0)
            return new(DeliveryActionStatus.Conflict, "The delivery changed. Reload and try again.");
        reconcileSignal.Wake();
        return new(DeliveryActionStatus.Ok);
    }

    public async Task<DeliveryActionResult> DismissAsync(string deliveryId, int expectedRevision, CancellationToken cancellationToken)
    {
        var delivery = await db.NotificationDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.DeliveryId == deliveryId, cancellationToken);
        if (delivery is null)
            return new(DeliveryActionStatus.NotFound);
        if (delivery.Revision != expectedRevision
            || delivery.State is not (NotificationDeliveryStates.Failed or NotificationDeliveryStates.Uncertain))
            return new(DeliveryActionStatus.Conflict, "Only failed or uncertain deliveries can be dismissed, and the record must be current.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var changed = await db.NotificationDeliveries
            .Where(d => d.DeliveryId == deliveryId && d.Revision == expectedRevision
                && (d.State == NotificationDeliveryStates.Failed || d.State == NotificationDeliveryStates.Uncertain))
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.State, NotificationDeliveryStates.Dismissed)
                .SetProperty(d => d.DismissedUtc, now)
                .SetProperty(d => d.UpdatedUtc, now)
                .SetProperty(d => d.Revision, d => d.Revision + 1), cancellationToken);

        return changed == 0 ? new(DeliveryActionStatus.Conflict, "The delivery changed. Reload and try again.") : new(DeliveryActionStatus.Ok);
    }
}
