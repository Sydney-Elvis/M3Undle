using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>A concrete place a delivery can go right now, bound to the revisions that made it eligible.</summary>
public sealed record EligibleTarget(
    string DestinationId,
    string ProviderKind,
    int ConfigRevision,
    int DeliveryIdentityRevision,
    string TargetId,
    string Label);

public sealed class NotificationRouting(NotificationDestinationAdapters adapters)
{
    public static bool IsSendingAllowed(NotificationSettings settings) =>
        settings.SendingEnabled && !settings.Paused && !settings.RequiresActivation;

    public bool IsStructurallyComplete(NotificationDestination destination) =>
        adapters.Find(destination.Kind)?.IsStructurallyComplete(destination) == true;

    public IReadOnlyList<EligibleTarget> TargetsOf(NotificationDestination destination)
    {
        var adapter = adapters.Find(destination.Kind);
        if (adapter is null)
            return [];

        return adapter.GetTargets(destination)
            .Select(t => new EligibleTarget(destination.DestinationId, destination.Kind, destination.ConfigRevision,
                destination.DeliveryIdentityRevision, t.TargetId, t.Label))
            .ToList();
    }

    /// <summary>
    /// Targets a notification may be delivered to now: sending allowed, a route selecting an enabled destination
    /// whose exact saved revision has been verified, and a complete setup. Empty means "do not send".
    /// </summary>
    public async Task<IReadOnlyList<EligibleTarget>> GetEligibleTargetsAsync(
        ApplicationDbContext db,
        NotificationSettings settings,
        NotificationRoute? route,
        CancellationToken cancellationToken)
    {
        if (!IsSendingAllowed(settings) || route?.DestinationId is null)
            return [];

        var destination = await LoadDestinationAsync(db, route.DestinationId, cancellationToken);
        if (destination is null || !destination.Enabled
            || destination.VerifiedRevision != destination.ConfigRevision
            || !IsStructurallyComplete(destination))
            return [];

        return TargetsOf(destination);
    }

    public static Task<NotificationDestination?> LoadDestinationAsync(ApplicationDbContext db, string destinationId, CancellationToken cancellationToken) =>
        db.NotificationDestinations.AsNoTracking()
            .Include(x => x.Matrix)
            .Include(x => x.Smtp)
            .Include(x => x.Recipients)
            .FirstOrDefaultAsync(x => x.DestinationId == destinationId, cancellationToken);

    public static int EffectiveFailureDelayMinutes(NotificationSettings settings, NotificationRoute? route) =>
        route?.FailureDelayMinutes ?? settings.FailureDelayMinutes;

    public static int EffectiveReminderIntervalHours(NotificationSettings settings, NotificationRoute? route) =>
        route?.ReminderIntervalHours ?? settings.ReminderIntervalHours;
}
