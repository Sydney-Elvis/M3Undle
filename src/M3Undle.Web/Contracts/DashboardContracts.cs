namespace M3Undle.Web.Contracts;

public enum ProfileHealthStatus
{
    Ok,
    Degraded,
    NoOutput
}

public sealed class DashboardProfileSummary
{
    public string ProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastPublishedUtc { get; set; }
    public int LiveCount { get; set; }
    public ProfileHealthStatus HealthStatus { get; set; }
}

public sealed class DashboardStatsDto
{
    public int PublishedLiveCount { get; set; }
    public int PublishedMovieCount { get; set; }
    public int PublishedSeriesCount { get; set; }
    public int ChannelsPendingReview { get; set; }
    public int GroupsPendingReview { get; set; }
    public List<DashboardProfileSummary> ProfileSummaries { get; set; } = [];
    public DateTime? LastPublishedUtc { get; set; }
    public bool RefreshFailed { get; set; }
}
