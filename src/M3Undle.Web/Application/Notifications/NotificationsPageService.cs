using M3Undle.Web.Contracts.Notifications;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// The single application surface behind both the Settings → Notifications section and the REST API, so validation,
/// revision checks and throttling are enforced once. It opens a fresh scope per call and never returns a secret.
/// </summary>
public sealed class NotificationsPageService(
    IServiceScopeFactory scopeFactory,
    NotificationConfigurationService configuration,
    NotificationActionThrottle throttle,
    NotificationRuntimeOptions runtimeOptions,
    NotificationDestinationAdapters adapters,
    TimeProvider timeProvider)
{
    public const int MaxPageSize = 100;

    public async Task<NotificationsOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await NotificationConfigurationService.EnsureSeededAsync(db, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        var settings = await db.NotificationSettings.AsNoTracking().SingleAsync(cancellationToken);
        var destinations = await db.NotificationDestinations.AsNoTracking()
            .Include(x => x.Matrix).Include(x => x.Smtp).Include(x => x.Recipients)
            .OrderBy(x => x.Kind)
            .ToListAsync(cancellationToken);
        var routes = await db.NotificationRoutes.AsNoTracking().ToDictionaryAsync(x => x.NotificationKey, StringComparer.Ordinal, cancellationToken);

        var allowed = NotificationRouting.IsSendingAllowed(settings);
        var destinationById = destinations.ToDictionary(x => x.DestinationId, StringComparer.Ordinal);

        var routeDtos = NotificationCatalog.Definitions.Select(definition =>
        {
            routes.TryGetValue(definition.Key, out var route);
            var destination = route?.DestinationId is { } id && destinationById.TryGetValue(id, out var d) ? d : null;
            return new NotificationRouteDto(
                definition.Key, definition.Label, definition.Trigger, definition.SubjectKind,
                definition.Lifecycle == NotificationLifecycle.Incident, definition.ProducerAvailable,
                destination?.Kind, route?.Revision ?? 1, route?.SendRecovery ?? true, route?.SendReminders ?? true,
                route?.FailureDelayMinutes, route?.ReminderIntervalHours,
                RouteIssue(definition, destination, allowed));
        }).ToList();

        var counts = await db.NotificationDeliveries.AsNoTracking()
            .GroupBy(x => x.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.State, x => x.Count, StringComparer.Ordinal, cancellationToken);
        var lastAccepted = await db.NotificationDeliveries.AsNoTracking()
            .Where(x => x.AcceptedUtc != null)
            .GroupBy(x => x.ProviderKind)
            .Select(g => new { Kind = g.Key, Last = g.Max(x => x.AcceptedUtc) })
            .ToDictionaryAsync(x => x.Kind, x => x.Last, StringComparer.Ordinal, cancellationToken);

        return new NotificationsOverviewResponse(
            new NotificationSettingsDto(
                settings.Revision, settings.SendingEnabled, settings.Paused, settings.RequiresActivation, allowed,
                settings.FailureDelayMinutes, settings.OverdueGraceMinutes, settings.ReminderIntervalHours,
                settings.CoverageWarnHours, settings.CoverageWarnPercent, settings.CoverageRecoverHours,
                settings.CoverageRecoverPercent, settings.CoverageGapMinutes, settings.RetentionDays),
            destinations.Select(ToDto).ToList(),
            routeDtos,
            new NotificationStatusSummaryDto(
                counts.GetValueOrDefault(NotificationDeliveryStates.Pending) + counts.GetValueOrDefault(NotificationDeliveryStates.Claimed),
                counts.GetValueOrDefault(NotificationDeliveryStates.RetryScheduled),
                counts.GetValueOrDefault(NotificationDeliveryStates.Failed),
                counts.GetValueOrDefault(NotificationDeliveryStates.Uncertain),
                settings.CapacitySuppressedUtc is not null,
                lastAccepted));
    }

    public async Task<NotificationDeliveryPageResponse> GetDeliveriesAsync(
        string? state, string? providerKind, string? notificationKey, int page, int pageSize, CancellationToken cancellationToken)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = from d in db.NotificationDeliveries.AsNoTracking()
                    join o in db.NotificationOccurrences.AsNoTracking() on d.OccurrenceId equals o.OccurrenceId
                    select new { d, o };
        if (!string.IsNullOrWhiteSpace(state))
            query = query.Where(x => x.d.State == state);
        if (!string.IsNullOrWhiteSpace(providerKind))
            query = query.Where(x => x.d.ProviderKind == providerKind);
        if (!string.IsNullOrWhiteSpace(notificationKey))
            query = query.Where(x => x.o.NotificationKey == notificationKey);

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.d.CreatedUtc)
            .ThenBy(x => x.d.DeliveryId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(x =>
        {
            var terminalProblem = x.d.State is NotificationDeliveryStates.Failed or NotificationDeliveryStates.Uncertain;
            return new NotificationDeliveryDto(
                x.d.DeliveryId, x.d.Revision, x.d.ProviderKind, x.d.TargetLabel, x.o.NotificationKey,
                NotificationCatalog.Find(x.o.NotificationKey)?.Label ?? x.o.NotificationKey, x.o.Kind,
                NotificationContent.Clean(x.d.PayloadTitle), x.d.State, x.d.AttemptCount, x.d.CreatedUtc, x.d.DueUtc, x.d.AcceptedUtc,
                x.d.ErrorCode, x.d.ErrorText, x.d.SuppressedReason, terminalProblem, terminalProblem,
                x.d.State == NotificationDeliveryStates.Uncertain);
        }).ToList();

        return new NotificationDeliveryPageResponse(items, total, page, pageSize);
    }

    // ------------------------------------------------------------------------------------------------ actions

    public Task<NotificationOperationResult> UpdateSettingsAsync(NotificationSettingsRequest r, CancellationToken cancellationToken) =>
        configuration.UpdateSettingsAsync(new NotificationSettingsUpdate(
            r.SendingEnabled, r.Paused, r.FailureDelayMinutes, r.OverdueGraceMinutes, r.ReminderIntervalHours, r.CoverageWarnHours,
            r.CoverageWarnPercent, r.CoverageRecoverHours, r.CoverageRecoverPercent, r.CoverageGapMinutes, r.RetentionDays),
            r.ExpectedRevision, cancellationToken);

    public Task<NotificationOperationResult> UpdateRouteAsync(string key, NotificationRouteRequest r, CancellationToken cancellationToken) =>
        configuration.UpdateRouteAsync(
            key, new NotificationRouteUpdate(r.DestinationKind, r.SendRecovery, r.SendReminders, r.FailureDelayMinutes, r.ReminderIntervalHours),
            r.ExpectedRevision, cancellationToken);

    public Task<NotificationOperationResult> SaveDestinationAsync(string kind, NotificationDestinationRequest r, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case NotificationProviderKinds.Smtp when r.Smtp is { } smtp:
                return configuration.SaveSmtpAsync(
                    new SmtpSetupRequest(smtp.Host, smtp.Port, smtp.TlsMode, smtp.AuthMode, smtp.Username, smtp.Password, smtp.ClearPassword,
                        smtp.SenderAddress, smtp.SenderName, smtp.Recipients ?? []),
                    r.ExpectedRevision, cancellationToken);
            case NotificationProviderKinds.Matrix when r.Matrix is { } matrix:
                return configuration.SaveMatrixAsync(
                    new MatrixSetupRequest(matrix.HomeserverUrl, matrix.RoomId, matrix.AccessToken, matrix.ClearAccessToken, matrix.AllowInsecureHttp),
                    r.ExpectedRevision, cancellationToken);
            case NotificationProviderKinds.Smtp or NotificationProviderKinds.Matrix:
                return Task.FromResult(new NotificationOperationResult(
                    NotificationOperationStatus.Invalid, "The request does not contain this method's settings.",
                    new Dictionary<string, string[]> { [kind] = ["Provide the settings for this method."] }));
            default:
                return Task.FromResult(new NotificationOperationResult(NotificationOperationStatus.NotFound));
        }
    }

    public Task<NotificationTestResult> TestDestinationAsync(string kind, int expectedRevision, CancellationToken cancellationToken) =>
        configuration.TestAsync(kind, expectedRevision, cancellationToken);

    public Task<NotificationOperationResult> SetEnabledAsync(string kind, bool enabled, int expectedRevision, CancellationToken cancellationToken) =>
        configuration.SetDestinationEnabledAsync(kind, enabled, expectedRevision, cancellationToken);

    public async Task<NotificationOperationResult> RetryDeliveryAsync(
        string deliveryId, int expectedRevision, bool acknowledgeDuplicateRisk, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var kind = await db.NotificationDeliveries.AsNoTracking().Where(d => d.DeliveryId == deliveryId).Select(d => d.ProviderKind).FirstOrDefaultAsync(cancellationToken);
        if (kind is null)
            return new(NotificationOperationStatus.NotFound);
        if (Throttled(kind) is { } limited)
            return limited;

        var result = await scope.ServiceProvider.GetRequiredService<NotificationDeliveryActions>()
            .RetryAsync(deliveryId, expectedRevision, acknowledgeDuplicateRisk, cancellationToken);
        return FromAction(result);
    }

    public async Task<NotificationOperationResult> DismissDeliveryAsync(string deliveryId, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var kind = await db.NotificationDeliveries.AsNoTracking().Where(d => d.DeliveryId == deliveryId).Select(d => d.ProviderKind).FirstOrDefaultAsync(cancellationToken);
        if (kind is null)
            return new(NotificationOperationStatus.NotFound);
        if (Throttled(kind) is { } limited)
            return limited;

        return FromAction(await scope.ServiceProvider.GetRequiredService<NotificationDeliveryActions>()
            .DismissAsync(deliveryId, expectedRevision, cancellationToken));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private NotificationOperationResult? Throttled(string providerKind) =>
        throttle.TryAcquire($"delivery-action:{providerKind}", limit: 10, TimeSpan.FromMinutes(1), serverWideLimit: 60, out var retryAfter)
            ? null
            : new(NotificationOperationStatus.RateLimited, "Too many retry or dismiss actions. Wait a moment.", RetryAfter: retryAfter);

    private static NotificationOperationResult FromAction(DeliveryActionResult result) => result.Status switch
    {
        DeliveryActionStatus.Ok => new(NotificationOperationStatus.Ok),
        DeliveryActionStatus.NotFound => new(NotificationOperationStatus.NotFound),
        DeliveryActionStatus.Invalid => new(NotificationOperationStatus.Invalid, result.Message,
            new Dictionary<string, string[]> { ["acknowledgeDuplicateRisk"] = [result.Message ?? "Acknowledge the duplicate risk."] }),
        _ => new(NotificationOperationStatus.Conflict, result.Message),
    };

    private static string? RouteIssue(NotificationDefinition definition, NotificationDestination? destination, bool sendingAllowed)
    {
        if (!definition.ProducerAvailable)
            return "Not available yet.";
        if (destination is null)
            return null;
        if (!destination.Enabled)
            return "The selected method is disabled.";
        if (destination.VerifiedRevision != destination.ConfigRevision)
            return "The selected method needs a successful test of its saved settings.";
        if (!sendingAllowed)
            return "Sending is switched off or paused, so nothing will be sent yet.";
        return null;
    }

    private NotificationDestinationDto ToDto(NotificationDestination d)
    {
        var complete = adapters.Find(d.Kind)?.IsStructurallyComplete(d) == true;
        return new NotificationDestinationDto(
            d.Kind, d.Kind == NotificationProviderKinds.Matrix ? "Matrix" : d.Kind == NotificationProviderKinds.Smtp ? "Email (SMTP)" : d.Kind,
            d.Enabled, d.ConfigRevision, d.VerificationStatus, d.VerificationDetail, d.VerifiedRevision == d.ConfigRevision, d.VerifiedUtc, complete,
            d.Matrix is null ? null : new NotificationMatrixDto(
                d.Matrix.HomeserverUrl, d.Matrix.RoomId, !string.IsNullOrEmpty(d.Matrix.AccessTokenEncrypted), d.Matrix.BotUserId, d.Matrix.DeviceId,
                d.Matrix.AllowInsecureHttp, runtimeOptions.AllowInsecureMatrixHttp),
            d.Smtp is null ? null : new NotificationSmtpDto(
                d.Smtp.Host, d.Smtp.Port, d.Smtp.TlsMode, d.Smtp.AuthMode, d.Smtp.Username, !string.IsNullOrEmpty(d.Smtp.PasswordEncrypted),
                d.Smtp.SenderAddress, d.Smtp.SenderName, d.Recipients.OrderBy(r => r.SortOrder).Select(r => r.Address).ToList()));
    }
}
