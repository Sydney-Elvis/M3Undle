using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Epg;

/// <summary>
/// Pure description of when the runtime really consults an EPG source. The scheduler itself is unchanged:
/// <c>SnapshotBuilder</c> fetches a provider-linked source on a refresh only once its cadence has elapsed, and
/// refreshes happen on the active profile's schedule. This type states that rule once so the cadence gate and
/// the overdue evaluator cannot drift apart.
/// </summary>
public static class EpgCheckSchedule
{
    /// <summary>The cadence gate: true while cached data is recent enough to skip the upstream request.</summary>
    public static bool IsCacheFresh(DateTime? lastSuccessUtc, int intervalHours, DateTimeOffset utcNow)
        => lastSuccessUtc.HasValue
           && (utcNow.UtcDateTime - lastSuccessUtc.Value).TotalHours < intervalHours;

    public static int? EffectiveIntervalHours(int? sourceOverrideHours, int? scheduleIntervalHours)
        => sourceOverrideHours ?? scheduleIntervalHours;

    /// <summary>
    /// True only when the runtime itself will fetch this source. Standalone sources (no provider) are fetched
    /// by hand, and a manual or absent active-profile schedule never triggers a refresh, so neither can be late.
    /// </summary>
    public static bool IsRuntimeScheduled(EpgSource source, int? scheduleIntervalHours)
        => source.Enabled && source.ProviderId is not null && scheduleIntervalHours is > 0;

    /// <summary>
    /// Latest time a real check should have completed, before any grace period. A source due at or before the next
    /// refresh is expected on that refresh; a source with a longer cadence is expected at the first refresh at or
    /// after its cadence elapses, which can be one schedule interval later. Null means no deadline applies: the
    /// source is not runtime-scheduled, or its only evidence predates real-check tracking and so is unknown.
    /// </summary>
    public static DateTime? ExpectedCheckDeadlineUtc(EpgSource source, int? scheduleIntervalHours)
    {
        if (!IsRuntimeScheduled(source, scheduleIntervalHours))
            return null;

        var schedule = scheduleIntervalHours!.Value;
        var effective = EffectiveIntervalHours(source.RefreshIntervalHours, schedule)!.Value;

        DateTime reference;
        if (source.LastCheckedUtc is { } checkedUtc)
            reference = checkedUtc;
        else if (source.LastSuccessUtc is not null || source.LastFailureUtc is not null)
            return null;
        else
            reference = source.CreatedUtc;

        var hours = effective <= schedule ? schedule : effective + schedule;
        return reference.AddHours(hours);
    }
}
