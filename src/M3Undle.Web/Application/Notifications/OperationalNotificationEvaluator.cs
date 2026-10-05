using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Observability;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

public sealed record ActiveChannelStream(
    string ProviderId,
    string ProviderChannelId,
    bool IsLive,
    DateTimeOffset? LastUpstreamByteUtc,
    DateTimeOffset? LastRecoveryStartedUtc);

/// <summary>The in-memory view of currently relayed channels. Reading it never touches the relay path or the database.</summary>
public interface IActiveStreamSource
{
    IReadOnlyList<ActiveChannelStream> GetActive();
}

public sealed class RegistryActiveStreamSource(StreamingRegistry registry) : IActiveStreamSource
{
    public IReadOnlyList<ActiveChannelStream> GetActive() =>
        registry.GetActiveSessions()
            .Select(s => new ActiveChannelStream(
                s.ProviderId, s.ProviderChannelId, s.State == Streaming.Models.SessionState.Live,
                s.LastUpstreamByteUtc, s.LastRecoveryStartedUtc))
            .ToList();
}

/// <summary>
/// Provider fetch, downstream integration and stream-instability conditions. Provider and downstream outcomes arrive as
/// observations committed with their results; stream instability is read from the health events the streaming pipeline already
/// persists asynchronously, so the relay loops gain no new database or network waits.
/// </summary>
public sealed class OperationalNotificationEvaluator(
    ApplicationDbContext db,
    NotificationIncidentService incidents,
    IActiveStreamSource activeStreams,
    TimeProvider timeProvider)
{
    private const int BatchSize = 200;

    public static readonly TimeSpan StreamFailureWindow = TimeSpan.FromMinutes(5);
    public const int StreamFailureThreshold = 3;
    public static readonly TimeSpan StreamHealthyFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StreamOutputFreshness = TimeSpan.FromSeconds(20);
    private const string UpstreamFailureKind = "UpstreamFailure";

    public async Task ConsumeObservationsAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        while (true)
        {
            var batch = await db.NotificationConditionObservations
                .Where(x => !x.Consumed
                    && (x.EvidenceKey == NotificationEvidenceKeys.ProviderFetch || x.EvidenceKey == NotificationEvidenceKeys.DownstreamCommand))
                .OrderBy(x => x.ObservationId)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return;

            foreach (var observation in batch)
            {
                if (observation.EvidenceKey == NotificationEvidenceKeys.ProviderFetch)
                    await ApplyAsync(observation, NotificationKeys.ProviderFetchFailed, NotificationSubjectKinds.Provider, settings, await ProviderNameAsync(observation.SubjectId, cancellationToken), cancellationToken);
                else
                    await ApplyAsync(observation, NotificationKeys.DownstreamRefreshFailed, NotificationSubjectKinds.DownstreamIntegration, settings, await IntegrationNameAsync(observation.SubjectId, cancellationToken), cancellationToken);

                observation.Consumed = true;
                observation.ConsumedUtc = timeProvider.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task ApplyAsync(
        NotificationConditionObservation observation, string key, string subjectKind, NotificationSettings settings, string? label, CancellationToken cancellationToken)
    {
        // A subject deleted since the outcome was recorded has nothing left to monitor.
        if (label is null)
            return;

        if (observation.Outcome == NotificationObservationOutcomes.Failed)
        {
            var incident = await incidents.EnsureActiveAsync(key, subjectKind, observation.SubjectId, label, "Warning", observation.CompletedUtc, observation.SafeDetail, cancellationToken);
            await incidents.ReconcileAsync(incident, settings, cancellationToken);
            return;
        }

        var active = await incidents.FindActiveAsync(key, observation.SubjectId, cancellationToken);
        if (active is null)
            return;

        incidents.Resolve(active, observation.CompletedUtc, "A later outcome succeeded.");
        await incidents.ReconcileAsync(active, settings, cancellationToken);
    }

    /// <summary>Deleted or disabled providers and integrations are no longer monitored. That is a close, never a recovery.</summary>
    public async Task CloseOrphanedAsync(CancellationToken cancellationToken)
    {
        var active = await db.NotificationIncidents
            .Where(x => x.State == NotificationIncidentStates.Active
                && (x.NotificationKey == NotificationKeys.ProviderFetchFailed || x.NotificationKey == NotificationKeys.DownstreamRefreshFailed))
            .ToListAsync(cancellationToken);
        if (active.Count == 0)
            return;

        var providers = await db.Providers.AsNoTracking().Where(p => p.Enabled).Select(p => p.ProviderId).ToListAsync(cancellationToken);
        var integrations = await db.DownstreamIntegrations.AsNoTracking().Where(i => i.Enabled).Select(i => i.DownstreamIntegrationId).ToListAsync(cancellationToken);

        foreach (var incident in active)
        {
            var monitored = incident.NotificationKey == NotificationKeys.ProviderFetchFailed
                ? providers.Contains(incident.SubjectId)
                : integrations.Contains(incident.SubjectId);
            if (!monitored)
                incidents.Close(incident, "The item was removed or disabled.");
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A channel is unstable after at least three recorded upstream failures within five minutes that stay unresolved for two
    /// minutes. It recovers only after two minutes of healthy output from a live session on that same channel. When the
    /// session ends, monitoring ends: the incident closes without a success notice. One channel never clears another.
    /// </summary>
    public async Task EvaluateStreamsAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        var bursts = await db.StreamChannelHealthEvents.AsNoTracking()
            .Where(e => e.EventKind == UpstreamFailureKind && e.EventUtc >= nowUtc - StreamFailureWindow)
            .GroupBy(e => new { e.ProviderId, e.ProviderChannelId })
            .Select(g => new { g.Key.ProviderId, g.Key.ProviderChannelId, Count = g.Count(), First = g.Min(e => e.EventUtc), Name = g.Max(e => e.DisplayName) })
            .Where(g => g.Count >= StreamFailureThreshold)
            .ToListAsync(cancellationToken);

        foreach (var burst in bursts)
        {
            var subject = SubjectId(burst.ProviderId, burst.ProviderChannelId);
            var label = await ChannelLabelAsync(burst.ProviderId, burst.Name ?? burst.ProviderChannelId, cancellationToken);
            var incident = await incidents.EnsureActiveAsync(
                NotificationKeys.StreamUnstable, NotificationSubjectKinds.Channel, subject, label, "Warning", burst.First,
                $"{burst.Count} upstream failures were recorded within {(int)StreamFailureWindow.TotalMinutes} minutes.", cancellationToken);
            await incidents.ReconcileAsync(incident, settings, cancellationToken);
        }

        var open = await db.NotificationIncidents
            .Where(x => x.State == NotificationIncidentStates.Active && x.NotificationKey == NotificationKeys.StreamUnstable)
            .ToListAsync(cancellationToken);
        if (open.Count > 0)
        {
            var sessions = activeStreams.GetActive();
            foreach (var incident in open)
                await EvaluateStreamIncidentAsync(incident, sessions, now, settings, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EvaluateStreamIncidentAsync(
        NotificationIncident incident, IReadOnlyList<ActiveChannelStream> sessions, DateTimeOffset now, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var (providerId, channelId) = ParseSubject(incident.SubjectId);
        var session = sessions.FirstOrDefault(s => s.ProviderId == providerId && s.ProviderChannelId == channelId);
        var nowUtc = now.UtcDateTime;

        if (session is null)
        {
            // Monitoring needs a session. Give a reconnecting viewer a moment before deciding the channel is no longer watched.
            if (nowUtc - incident.LastObservedUtc >= StreamHealthyFor)
            {
                incidents.Close(incident, "No session is relaying this channel any more.");
                await incidents.ReconcileAsync(incident, settings, cancellationToken);
            }

            return;
        }

        incident.LastObservedUtc = nowUtc;
        var lastFailure = await db.StreamChannelHealthEvents.AsNoTracking()
            .Where(e => e.ProviderId == providerId && e.ProviderChannelId == channelId && e.EventKind == UpstreamFailureKind)
            .MaxAsync(e => (DateTime?)e.EventUtc, cancellationToken);

        var outputFresh = session.IsLive && session.LastUpstreamByteUtc is { } lastByte && now - lastByte <= StreamOutputFreshness;
        var healthySince = new[] { lastFailure is { } f ? new DateTimeOffset(f, TimeSpan.Zero) : (DateTimeOffset?)null, session.LastRecoveryStartedUtc }
            .Where(x => x is not null).Select(x => x!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

        if (outputFresh && now - healthySince >= StreamHealthyFor)
        {
            incidents.Resolve(incident, nowUtc, "Sustained healthy output.");
            await incidents.ReconcileAsync(incident, settings, cancellationToken);
        }
    }

    public static string SubjectId(string providerId, string providerChannelId) => $"{providerId}/{providerChannelId}";

    private static (string ProviderId, string ChannelId) ParseSubject(string subject)
    {
        var slash = subject.IndexOf('/');
        return slash < 0 ? (subject, string.Empty) : (subject[..slash], subject[(slash + 1)..]);
    }

    private async Task<string> ChannelLabelAsync(string providerId, string channelName, CancellationToken cancellationToken)
    {
        var provider = await ProviderNameAsync(providerId, cancellationToken);
        return provider is null ? channelName : $"{channelName} (provider {provider})";
    }

    private Task<string?> ProviderNameAsync(string providerId, CancellationToken cancellationToken) =>
        db.Providers.AsNoTracking().Where(p => p.ProviderId == providerId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken);

    private Task<string?> IntegrationNameAsync(string integrationId, CancellationToken cancellationToken) =>
        db.DownstreamIntegrations.AsNoTracking().Where(i => i.DownstreamIntegrationId == integrationId).Select(i => i.Name).FirstOrDefaultAsync(cancellationToken);
}
