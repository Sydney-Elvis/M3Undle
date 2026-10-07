using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>Identifies this process start, so startup occurrences are captured once per boot.</summary>
public static class NotificationBoot
{
    public static readonly string Id = Guid.NewGuid().ToString("N");
}

public static class NotificationEvidenceKeys
{
    /// <summary>One observation per real EPG source check (download, 304, file read or failure).</summary>
    public const string EpgSourceCheck = "epg.source_check";

    /// <summary>One observation per committed provider playlist fetch (success or failure).</summary>
    public const string ProviderFetch = "provider.fetch";

    /// <summary>One observation per committed downstream integration command outcome.</summary>
    public const string DownstreamCommand = "downstream.command";

    /// <summary>One observation per failed sign-in; grouped into fixed windows when summarised.</summary>
    public const string LoginAttempt = "security.login";
}

public static class NotificationObservationOutcomes
{
    public const string Ok = "ok";
    public const string Unchanged = "unchanged";
    public const string Failed = "failed";
}

public sealed record OneTimeNotification(
    string NotificationKey,
    string OccurrenceKey,
    string Severity,
    string Title,
    string Body,
    string? SubjectLabel,
    string? LinkPath,
    DateTime OccurredUtc);

/// <summary>
/// Stages evidence and occurrences in the caller's DbContext so they commit atomically with the business
/// outcome that produced them. It never saves, never opens its own scope and never touches a transport.
/// </summary>
public sealed class NotificationOccurrenceWriter(ApplicationDbContext db, TimeProvider timeProvider)
{
    public NotificationConditionObservation StageObservation(
        string evidenceKey,
        string subjectKind,
        string subjectId,
        string outcome,
        DateTime completedUtc,
        string? safeDetail = null)
    {
        var observation = new NotificationConditionObservation
        {
            EvidenceKey = evidenceKey,
            SubjectKind = subjectKind,
            SubjectId = subjectId,
            Outcome = outcome,
            CompletedUtc = completedUtc,
            SafeDetail = safeDetail,
        };
        db.NotificationConditionObservations.Add(observation);
        return observation;
    }

    /// <summary>
    /// Captures a one-time occurrence under the policy in force at the commit of the business outcome. When the row is
    /// Off, or sending is disabled or paused, nothing is retained for later playback.
    /// </summary>
    /// <summary>Whether a one-time notification would be retained right now: sending allowed and the row routed.</summary>
    public async Task<bool> CanCaptureAsync(string notificationKey, CancellationToken cancellationToken)
        => await GetCapturePolicyAsync(notificationKey, cancellationToken) is not null;

    private async Task<(NotificationSettings Settings, NotificationRoute Route)?> GetCapturePolicyAsync(string notificationKey, CancellationToken cancellationToken)
    {
        var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (settings is null || !NotificationRouting.IsSendingAllowed(settings))
            return null;

        var route = await db.NotificationRoutes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.NotificationKey == notificationKey, cancellationToken);
        return route?.DestinationId is null ? null : (settings, route);
    }

    public async Task<NotificationOccurrence?> StageOneTimeAsync(OneTimeNotification notification, CancellationToken cancellationToken)
    {
        var definition = NotificationCatalog.Find(notification.NotificationKey)
            ?? throw new ArgumentException($"Unknown notification key '{notification.NotificationKey}'.", nameof(notification));
        if (definition.Lifecycle != NotificationLifecycle.OneTime)
            throw new ArgumentException($"'{definition.Key}' is an incident notification, not a one-time occurrence.", nameof(notification));

        if (await GetCapturePolicyAsync(definition.Key, cancellationToken) is not var (settings, route))
            return null;

        var alreadyStaged = db.NotificationOccurrences.Local.Any(x => x.OccurrenceKey == notification.OccurrenceKey)
            || await db.NotificationOccurrences.AnyAsync(x => x.OccurrenceKey == notification.OccurrenceKey, cancellationToken);
        if (alreadyStaged)
            return null;

        var occurrence = new NotificationOccurrence
        {
            OccurrenceId = Guid.NewGuid().ToString(),
            OccurrenceKey = notification.OccurrenceKey,
            NotificationKey = definition.Key,
            Kind = NotificationOccurrenceKinds.OneTime,
            Sequence = 0,
            PolicyRevision = route.Revision,
            ActivationEpoch = settings.ActivationEpoch,
            Severity = notification.Severity,
            Title = notification.Title,
            Body = notification.Body,
            SubjectLabel = notification.SubjectLabel,
            LinkPath = notification.LinkPath,
            OccurredUtc = notification.OccurredUtc,
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
        };
        db.NotificationOccurrences.Add(occurrence);
        return occurrence;
    }
}
