namespace M3Undle.Core.Epg;

/// <summary>A normalised programme interval. Zero-length and inverted intervals are invalid and ignored.</summary>
public readonly record struct EpgInterval(DateTimeOffset StartUtc, DateTimeOffset StopUtc);

/// <summary>Outcome of evaluating a set of relevant channels against a future window.</summary>
public sealed record EpgWindowCoverageResult(int RelevantChannels, int UsableChannels, int ChannelsWithCurrentOrFutureData)
{
    /// <summary>With no relevant channels there is nothing to judge; this is neither healthy nor critical.</summary>
    public bool NotMonitored => RelevantChannels == 0;

    /// <summary>No relevant channel has any programme that has not already ended.</summary>
    public bool NoUsableData => RelevantChannels > 0 && ChannelsWithCurrentOrFutureData == 0;

    public double Fraction => RelevantChannels == 0 ? 0d : (double)UsableChannels / RelevantChannels;
}

/// <summary>
/// Deterministic future-window coverage rules for guide health. Unlike <see cref="EpgCoverageAnalyzer"/>, which asks
/// whether any programme overlaps a window, this requires data to run continuously through the window.
/// </summary>
public static class EpgWindowCoverageAnalyzer
{
    /// <summary>
    /// True when valid programmes cover <c>[nowUtc, nowUtc + horizon]</c> without a gap longer than
    /// <paramref name="maxGap"/>, counting the lead-in from now and tolerating a shortfall of at most
    /// <paramref name="maxGap"/> at the end. A programme starting after the window cannot make a channel usable.
    /// </summary>
    public static bool HasUsableCoverage(
        IEnumerable<EpgInterval> intervals,
        DateTimeOffset nowUtc,
        TimeSpan horizon,
        TimeSpan maxGap)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var windowEnd = nowUtc + horizon;
        var cursor = nowUtc;
        var used = false;

        foreach (var interval in intervals
                     .Where(i => IsValid(i) && i.StopUtc > nowUtc)
                     .OrderBy(i => i.StartUtc))
        {
            if (interval.StartUtc >= windowEnd)
                break;
            if (interval.StartUtc - cursor > maxGap)
                break;

            used = true;
            if (interval.StopUtc > cursor)
                cursor = interval.StopUtc;
            if (cursor >= windowEnd)
                return true;
        }

        return used && windowEnd - cursor <= maxGap;
    }

    public static bool HasCurrentOrFutureData(IEnumerable<EpgInterval> intervals, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        return intervals.Any(i => IsValid(i) && i.StopUtc > nowUtc);
    }

    public static EpgWindowCoverageResult Evaluate(
        IEnumerable<IReadOnlyList<EpgInterval>> relevantChannels,
        DateTimeOffset nowUtc,
        TimeSpan horizon,
        TimeSpan maxGap)
    {
        ArgumentNullException.ThrowIfNull(relevantChannels);

        var relevant = 0;
        var usable = 0;
        var withData = 0;
        foreach (var channel in relevantChannels)
        {
            relevant++;
            if (HasCurrentOrFutureData(channel, nowUtc))
                withData++;
            if (HasUsableCoverage(channel, nowUtc, horizon, maxGap))
                usable++;
        }

        return new EpgWindowCoverageResult(relevant, usable, withData);
    }

    private static bool IsValid(EpgInterval interval) => interval.StopUtc > interval.StartUtc;
}
