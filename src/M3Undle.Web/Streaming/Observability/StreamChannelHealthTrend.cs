namespace M3Undle.Web.Streaming.Observability;

public enum StreamChannelHealthTrend
{
    Unknown = 0,
    Improving = 1,
    Stable = 2,
    Worsening = 3,
}

public sealed record StreamChannelHealthTrendResult(
    StreamChannelHealthTrend Trend,
    string Reason,
    int RecentAdverseCount,
    int ComparisonAdverseCount,
    TimeSpan RecentCleanWatchDuration,
    TimeSpan ComparisonCleanWatchDuration,
    TimeSpan CleanWatchSinceLastAdverse,
    StreamChannelHealthProfile? RecentProfile,
    StreamChannelHealthProfile? ComparisonProfile)
{
    public static StreamChannelHealthTrendResult Insufficient(string reason)
        => new(StreamChannelHealthTrend.Unknown, reason, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null, null);
}
