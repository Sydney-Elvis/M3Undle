using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>Creates per-target deliveries from occurrences. Transport and retry live elsewhere; this never does I/O.</summary>
public sealed class NotificationDeliveryService(EndpointUrlService endpointUrls, ILogger<NotificationDeliveryService> logger)
{
    public const int NonTerminalCeiling = 10_000;

    internal int Ceiling { get; set; } = NonTerminalCeiling;

    public static readonly string[] NonTerminalStates =
    [
        NotificationDeliveryStates.Pending,
        NotificationDeliveryStates.Claimed,
        NotificationDeliveryStates.RetryScheduled,
    ];

    /// <summary>
    /// Stages one Pending delivery per target, skipping any the unique identity already records. At the ceiling it
    /// preserves existing work, rejects the rest and raises the visible capacity condition instead of dropping silently.
    /// </summary>
    public async Task<int> MaterializeAsync(
        ApplicationDbContext db,
        NotificationSettings settings,
        NotificationOccurrence occurrence,
        IReadOnlyList<EligibleTarget> targets,
        int routeRevision,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var nonTerminal = await db.NotificationDeliveries.CountAsync(x => NonTerminalStates.Contains(x.State), cancellationToken)
            + db.ChangeTracker.Entries<NotificationDelivery>()
                .Count(e => e.State == EntityState.Added && NonTerminalStates.Contains(e.Entity.State));

        foreach (var target in targets)
        {
            var exists = db.NotificationDeliveries.Local.Any(x => IsSameIdentity(x, occurrence.OccurrenceId, target))
                || await db.NotificationDeliveries.AnyAsync(x =>
                    x.OccurrenceId == occurrence.OccurrenceId && x.DestinationId == target.DestinationId
                    && x.TargetId == target.TargetId && x.DeliveryIdentityRevision == target.DeliveryIdentityRevision, cancellationToken);
            if (exists)
                continue;

            if (nonTerminal >= Ceiling)
            {
                if (settings.CapacitySuppressedUtc is null)
                {
                    settings.CapacitySuppressedUtc = nowUtc;
                    logger.LogWarning("Notification delivery queue is at its ceiling of {Ceiling}; new deliveries are being rejected.", NonTerminalCeiling);
                }
                break;
            }

            var deliveryId = Guid.NewGuid().ToString();
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                DeliveryId = deliveryId,
                OccurrenceId = occurrence.OccurrenceId,
                DestinationId = target.DestinationId,
                ProviderKind = target.ProviderKind,
                TargetId = target.TargetId,
                TargetLabel = target.Label,
                DeliveryIdentityRevision = target.DeliveryIdentityRevision,
                ConfigRevision = target.ConfigRevision,
                RouteRevision = routeRevision,
                State = NotificationDeliveryStates.Pending,
                DueUtc = nowUtc,
                PayloadTitle = occurrence.Title,
                PayloadBody = occurrence.Body,
                PayloadLinkPath = ComposeLink(occurrence.LinkPath),
                MessageId = $"<{deliveryId}@m3undle.local>",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            });
            created++;
            nonTerminal++;
        }

        return created;
    }

    /// <summary>Clears the capacity condition once the queue has room again; reconciliation then re-creates current state.</summary>
    public async Task ClearCapacityConditionIfRecoveredAsync(ApplicationDbContext db, NotificationSettings settings, CancellationToken cancellationToken)
    {
        if (settings.CapacitySuppressedUtc is null)
            return;

        var nonTerminal = await db.NotificationDeliveries.CountAsync(x => NonTerminalStates.Contains(x.State), cancellationToken);
        if (nonTerminal < Ceiling * 9 / 10)
            settings.CapacitySuppressedUtc = null;
    }

    // A link is only ever an ordinary UI route on the configured external base URL; with none configured it is omitted
    // rather than exposing a container or internal address.
    private string? ComposeLink(string? path)
    {
        var baseUrl = endpointUrls.GetExternalBaseUrl();
        if (baseUrl is null || string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            return null;
        return baseUrl + path;
    }

    private static bool IsSameIdentity(NotificationDelivery d, string occurrenceId, EligibleTarget target) =>
        d.OccurrenceId == occurrenceId && d.DestinationId == target.DestinationId
        && d.TargetId == target.TargetId && d.DeliveryIdentityRevision == target.DeliveryIdentityRevision;
}
