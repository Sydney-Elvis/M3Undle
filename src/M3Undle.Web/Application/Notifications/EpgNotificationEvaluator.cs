using M3Undle.Core.Epg;
using M3Undle.Web.Application.Epg;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// Turns committed EPG facts into incidents. Failure, overdue and coverage are independent conditions: a 304 can
/// recover fetch health while coverage stays degraded, and a completed check of any outcome ends "overdue".
/// </summary>
public sealed class EpgNotificationEvaluator(
    ApplicationDbContext db,
    NotificationIncidentService incidents,
    EpgCoverageFacts coverageFacts,
    IRefreshScheduleService refreshSchedule,
    TimeProvider timeProvider)
{
    private const int BatchSize = 200;
    private static readonly string[] EpgKeys =
        [NotificationKeys.EpgFetchFailed, NotificationKeys.EpgRefreshOverdue, NotificationKeys.EpgCoverageInsufficient];

    /// <summary>Applies observations strictly in database-id order, committing each so progress survives a crash.</summary>
    public async Task<int> ConsumeObservationsAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var processed = 0;
        while (true)
        {
            var batch = await db.NotificationConditionObservations
                .Where(x => !x.Consumed && x.EvidenceKey == NotificationEvidenceKeys.EpgSourceCheck)
                .OrderBy(x => x.ObservationId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return processed;

            foreach (var observation in batch)
            {
                await ApplySourceCheckAsync(observation, settings, cancellationToken);

                observation.Consumed = true;
                observation.ConsumedUtc = timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                processed++;
            }
        }
    }

    private async Task ApplySourceCheckAsync(NotificationConditionObservation observation, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var source = await db.EpgSources.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EpgSourceId == observation.SubjectId, cancellationToken);
        if (source is null)
            return;

        var label = await LabelAsync(source, cancellationToken);
        var touched = new List<NotificationIncident>();

        var overdue = await incidents.FindActiveAsync(NotificationKeys.EpgRefreshOverdue, source.EpgSourceId, cancellationToken);
        if (overdue is not null)
        {
            incidents.Resolve(overdue, observation.CompletedUtc, "A real check completed.");
            touched.Add(overdue);
        }

        if (observation.Outcome == NotificationObservationOutcomes.Failed)
        {
            touched.Add(await incidents.EnsureActiveAsync(
                NotificationKeys.EpgFetchFailed, NotificationSubjectKinds.EpgSource, source.EpgSourceId, label,
                "Warning", observation.CompletedUtc, observation.SafeDetail, cancellationToken));
        }
        else
        {
            var failing = await incidents.FindActiveAsync(NotificationKeys.EpgFetchFailed, source.EpgSourceId, cancellationToken);
            if (failing is not null)
            {
                incidents.Resolve(failing, observation.CompletedUtc, "The source was checked successfully.");
                touched.Add(failing);
            }
        }

        foreach (var incident in touched)
            await incidents.ReconcileAsync(incident, settings, cancellationToken);
    }

    /// <summary>
    /// A source expected to be checked on the runtime's own schedule but not checked by deadline plus grace stays
    /// overdue until a real attempt completes. Anything the runtime does not schedule can never be late.
    /// </summary>
    public async Task EvaluateOverdueAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var active = await refreshSchedule.GetActiveProfileSettingsAsync(cancellationToken);
        var scheduleHours = active?.Settings.IntervalHours;

        var eligibleProviderIds = await (
                from provider in db.Providers.AsNoTracking()
                where provider.Enabled
                join link in db.ProfileProviders.AsNoTracking() on provider.ProviderId equals link.ProviderId
                where link.Enabled && link.Profile.Enabled
                select provider.ProviderId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var sources = await db.EpgSources.AsNoTracking().Where(x => x.Enabled && x.ProviderId != null).ToListAsync(cancellationToken);
        var grace = TimeSpan.FromMinutes(settings.OverdueGraceMinutes);

        foreach (var source in sources)
        {
            var existing = await incidents.FindActiveAsync(NotificationKeys.EpgRefreshOverdue, source.EpgSourceId, cancellationToken);
            var deadline = eligibleProviderIds.Contains(source.ProviderId!)
                ? EpgCheckSchedule.ExpectedCheckDeadlineUtc(source, scheduleHours)
                : null;

            if (deadline is null)
            {
                if (existing is not null)
                    Close(existing, "The source is no longer on a refresh schedule.");
                continue;
            }

            if (now <= deadline.Value + grace)
                continue;

            var incident = await incidents.EnsureActiveAsync(
                NotificationKeys.EpgRefreshOverdue, NotificationSubjectKinds.EpgSource, source.EpgSourceId,
                await LabelAsync(source, cancellationToken), "Warning", deadline.Value, null, cancellationToken);
            await incidents.ReconcileAsync(incident, settings, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task EvaluateCoverageAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var nowOffset = timeProvider.GetUtcNow();
        var now = nowOffset.UtcDateTime;
        var gap = TimeSpan.FromMinutes(settings.CoverageGapMinutes);
        var sources = await db.EpgSources.AsNoTracking().Where(x => x.Enabled).ToListAsync(cancellationToken);

        foreach (var source in sources)
        {
            var existing = await incidents.FindActiveAsync(NotificationKeys.EpgCoverageInsufficient, source.EpgSourceId, cancellationToken);
            var channels = await coverageFacts.LoadRelevantChannelsAsync(source.EpgSourceId, cancellationToken);

            if (channels.Count == 0)
            {
                if (existing is not null)
                    Close(existing, "No relevant channels are mapped to this source.");
                continue;
            }

            var warn = EpgWindowCoverageAnalyzer.Evaluate(channels, nowOffset, TimeSpan.FromHours(settings.CoverageWarnHours), gap);
            var critical = warn.NoUsableData;
            var below = warn.Fraction * 100 < settings.CoverageWarnPercent;

            if (existing is null)
            {
                if (!critical && !below)
                    continue;

                var incident = await incidents.EnsureActiveAsync(
                    NotificationKeys.EpgCoverageInsufficient, NotificationSubjectKinds.EpgSource, source.EpgSourceId,
                    await LabelAsync(source, cancellationToken), critical ? "Error" : "Warning", now,
                    Describe(warn, settings.CoverageWarnHours), cancellationToken);
                await incidents.ReconcileAsync(incident, settings, cancellationToken);
                continue;
            }

            existing.SafeDetail = Describe(warn, settings.CoverageWarnHours);
            if (critical)
                existing.Severity = "Error";

            var recover = EpgWindowCoverageAnalyzer.Evaluate(channels, nowOffset, TimeSpan.FromHours(settings.CoverageRecoverHours), gap);
            var healthy = !critical && recover.Fraction * 100 >= settings.CoverageRecoverPercent;
            existing.ConsecutiveHealthy = healthy ? existing.ConsecutiveHealthy + 1 : 0;
            existing.UpdatedUtc = now;

            if (existing.ConsecutiveHealthy >= 2)
                incidents.Resolve(existing, now, "Coverage recovered on two consecutive evaluations.");

            await incidents.ReconcileAsync(existing, settings, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Subjects that were deleted or disabled are no longer monitored; this is a close, never a recovery.</summary>
    public async Task CloseOrphanedIncidentsAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var active = await db.NotificationIncidents
            .Where(x => x.State == NotificationIncidentStates.Active && x.SubjectKind == NotificationSubjectKinds.EpgSource
                && EpgKeys.Contains(x.NotificationKey))
            .ToListAsync(cancellationToken);
        if (active.Count == 0)
            return;

        var ids = active.Select(x => x.SubjectId).Distinct().ToList();
        var enabled = await db.EpgSources.AsNoTracking()
            .Where(x => ids.Contains(x.EpgSourceId) && x.Enabled)
            .Select(x => x.EpgSourceId)
            .ToListAsync(cancellationToken);

        foreach (var incident in active.Where(x => !enabled.Contains(x.SubjectId)))
            Close(incident, "The source was removed or disabled.");

        await db.SaveChangesAsync(cancellationToken);
    }

    // Suppression of unclaimed work follows in the reconciler sweep, which visits closed incidents with queued deliveries.
    private void Close(NotificationIncident incident, string reason) => incidents.Close(incident, reason);

    private async Task<string> LabelAsync(EpgSource source, CancellationToken cancellationToken)
    {
        if (source.ProviderId is null)
            return source.Name;

        var provider = await db.Providers.AsNoTracking()
            .Where(p => p.ProviderId == source.ProviderId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(cancellationToken);
        return provider is null || string.Equals(provider, source.Name, StringComparison.OrdinalIgnoreCase)
            ? source.Name
            : $"{source.Name} (provider {provider})";
    }

    private static string Describe(EpgWindowCoverageResult result, int horizonHours)
    {
        var percent = (int)Math.Floor(result.Fraction * 100);
        var text = $"{result.UsableChannels} of {result.RelevantChannels} relevant channels ({percent}%) have guide data through the next {horizonHours} hours.";
        return result.NoUsableData ? text + " No relevant channel has any current or upcoming guide data." : text;
    }
}
