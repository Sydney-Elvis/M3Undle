using M3Undle.Core.Epg;
using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Epg;

/// <summary>
/// The only place real EPG source outcomes are persisted. Source status columns and the fetch run are
/// written in one commit, before any best-effort UI event is published, and a cache-only read is never
/// recorded as health evidence.
/// </summary>
public sealed class EpgSourceOutcomeRecorder(
    ApplicationDbContext db,
    NotificationOccurrenceWriter notificationWriter,
    EpgCoverageFacts coverageFacts,
    IEventService eventService,
    TimeProvider timeProvider,
    ILogger<EpgSourceOutcomeRecorder> logger)
{
    public async Task<EpgFetchRun?> RecordAsync(
        EpgSource source,
        EpgSourceFetcher.FetchResult result,
        EpgCatalogue catalogue,
        DateTime startedUtc,
        CancellationToken cancellationToken)
    {
        if (!result.IsRealCheck)
        {
            logger.LogDebug("EPG source {EpgSourceId}: cached payload reused; no source evidence recorded.", source.EpgSourceId);
            return null;
        }

        var finishedUtc = timeProvider.GetUtcNow().UtcDateTime;
        var succeeded = result.Disposition != EpgFetchDisposition.Failed;

        var tracked = await db.EpgSources.FirstOrDefaultAsync(x => x.EpgSourceId == source.EpgSourceId, cancellationToken);
        if (tracked is not null)
        {
            tracked.LastCheckedUtc = finishedUtc;
            tracked.LastCheckStatus = result.Status;
            if (succeeded)
            {
                tracked.LastSuccessUtc = finishedUtc;
                tracked.ETag = result.ETag ?? tracked.ETag;
                tracked.LastModifiedUtc = result.LastModifiedUtc ?? tracked.LastModifiedUtc;
            }
            else
            {
                tracked.LastFailureUtc = finishedUtc;
                logger.LogWarning(
                    "EPG source {EpgSourceId} ({Name}) fetch failed: {Error}",
                    source.EpgSourceId, source.Name, result.ErrorSummary ?? "unknown error");
            }
            tracked.UpdatedUtc = finishedUtc;
        }

        var channelCount = catalogue.Channels.Count;
        var programmeCount = catalogue.ProgrammesByChannel.Values.Sum(p => p.Count);

        var fetchRun = new EpgFetchRun
        {
            EpgFetchRunId = Guid.NewGuid().ToString(),
            EpgSourceId = source.EpgSourceId,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Status = result.Status,
            Bytes = result.Bytes > 0 ? (int)Math.Min(result.Bytes, int.MaxValue) : null,
            ChannelCount = channelCount > 0 ? channelCount : null,
            ProgrammeCount = programmeCount > 0 ? programmeCount : null,
            ErrorSummary = result.ErrorSummary,
        };
        db.EpgFetchRuns.Add(fetchRun);

        // Staged in the same commit as the source outcome so health evidence can never disagree with it.
        notificationWriter.StageObservation(
            NotificationEvidenceKeys.EpgSourceCheck,
            NotificationSubjectKinds.EpgSource,
            source.EpgSourceId,
            result.Disposition switch
            {
                EpgFetchDisposition.Downloaded => NotificationObservationOutcomes.Ok,
                EpgFetchDisposition.Unchanged => NotificationObservationOutcomes.Unchanged,
                _ => NotificationObservationOutcomes.Failed,
            },
            finishedUtc,
            succeeded ? null : ClassifyFailure(result.ErrorSummary));

        // Only a downloaded payload changes what the cache holds, so only then do coverage facts change.
        if (result.Disposition == EpgFetchDisposition.Downloaded)
            await coverageFacts.StageFromCatalogueAsync(source.EpgSourceId, catalogue, finishedUtc, finishedUtc.ToString("O"), cancellationToken);

        // Outcome evidence must survive host shutdown, so it commits independently of the caller's token.
        await db.SaveChangesAsync(CancellationToken.None);

        if (tracked is not null)
        {
            if (succeeded)
                await PublishRecoveredIfNeededAsync(tracked, cancellationToken);
            else
                await PublishFailedAsync(tracked, result.ErrorSummary, finishedUtc, cancellationToken);
        }

        return fetchRun;
    }

    public async Task StageSourceChannelsAsync(
        string epgSourceId,
        IReadOnlyList<EpgChannelRecord> channels,
        CancellationToken cancellationToken)
    {
        if (channels.Count == 0)
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await db.EpgSourceChannels
            .Where(x => x.EpgSourceId == epgSourceId)
            .ToListAsync(cancellationToken);
        var byId = existing.ToDictionary(x => x.XmltvChannelId, StringComparer.Ordinal);
        var addedThisRun = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ch in channels)
        {
            if (byId.TryGetValue(ch.XmltvChannelId, out var row))
            {
                row.DisplayName = ch.DisplayName;
                row.IconUrl = ch.IconUrl;
                row.LastSeenUtc = now;
            }
            else if (addedThisRun.Add(ch.XmltvChannelId))
            {
                db.EpgSourceChannels.Add(new EpgSourceChannel
                {
                    EpgSourceChannelId = Guid.NewGuid().ToString(),
                    EpgSourceId = epgSourceId,
                    XmltvChannelId = ch.XmltvChannelId,
                    DisplayName = ch.DisplayName,
                    IconUrl = ch.IconUrl,
                    LastSeenUtc = now,
                });
            }
        }
    }

    private async Task PublishFailedAsync(EpgSource source, string? error, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var scopeName = source.Name;
        if (source.ProviderId is not null)
        {
            scopeName = await db.Providers
                .AsNoTracking()
                .Where(p => p.ProviderId == source.ProviderId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? source.ProviderId;
        }

        var staleness = EpgHealth.DescribeStale(source.LastSuccessUtc, nowUtc);
        await PublishBestEffortAsync(
            SystemEventSeverity.Warning,
            SystemEventTypes.EpgFetchFailed,
            $"Guide update failed for '{scopeName}' — serving cached guide data",
            $"{source.Name}: {error ?? "unknown error"} ({staleness}).",
            source);
    }

    private async Task PublishRecoveredIfNeededAsync(EpgSource source, CancellationToken cancellationToken)
    {
        try
        {
            var hasEvent = await eventService.HasEventAsync(
                SystemEventTypes.EpgFetchFailed, ct: cancellationToken, epgSourceId: source.EpgSourceId);
            if (!hasEvent)
                return;

            await eventService.PublishAsync(
                SystemEventSeverity.Info,
                SystemEventTypes.EpgBackOnline,
                $"Guide updates recovered for source '{source.Name}'",
                providerId: source.ProviderId,
                epgSourceId: source.EpgSourceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish EpgBackOnline event for source {EpgSourceId}.", source.EpgSourceId);
        }
    }

    private async Task PublishBestEffortAsync(
        SystemEventSeverity severity,
        string eventType,
        string title,
        string detail,
        EpgSource source)
    {
        try
        {
            await eventService.PublishAsync(severity, eventType, title, detail, source.ProviderId, epgSourceId: source.EpgSourceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish system event {EventType} for EPG source {EpgSourceId}.", eventType, source.EpgSourceId);
        }
    }

    // Observations are exported and may leave the instance, so they carry a class of failure, never upstream text.
    internal static string ClassifyFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "error";
        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        if (error.StartsWith("HTTP fetch failed", StringComparison.OrdinalIgnoreCase))
            return "http";
        if (error.StartsWith("File read failed", StringComparison.OrdinalIgnoreCase))
            return "file";
        if (error.StartsWith("No URL", StringComparison.OrdinalIgnoreCase)
            || error.StartsWith("No file path", StringComparison.OrdinalIgnoreCase)
            || error.StartsWith("URL contains undefined variable", StringComparison.OrdinalIgnoreCase))
            return "configuration";
        if (error.Contains("provider", StringComparison.OrdinalIgnoreCase))
            return "provider";
        return "error";
    }
}
