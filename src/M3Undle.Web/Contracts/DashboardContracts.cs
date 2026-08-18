namespace M3Undle.Web.Contracts;

public enum ProfileHealthStatus
{
    Ok,
    Degraded,
    NoOutput
}

public sealed record DashboardProviderSummary(
    string ProviderId,
    string ProviderName,
    int? MaxConcurrentStreams,
    DateTime? ExpiresUtc);

public sealed class DashboardProfileSummary
{
    public string ProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsActive { get; set; }
    public bool IsPublished { get; set; }
    public DateTime? LastPublishedUtc { get; set; }
    public int LiveCount { get; set; }
    public int MovieCount { get; set; }
    public int SeriesCount { get; set; }
    public ProfileHealthStatus HealthStatus { get; set; }
    public List<DashboardProviderSummary> Providers { get; set; } = [];
}

public sealed record ExpiringProviderWarning(string ProviderId, string ProviderName, DateTime ExpiresUtc);

public sealed class DashboardStatsDto
{
    public int PublishedLiveCount { get; set; }
    public int PublishedMovieCount { get; set; }
    public int PublishedSeriesCount { get; set; }
    public int ChannelsPendingReview { get; set; }
    public int GroupsPendingReview { get; set; }
    public List<DashboardProfileSummary> ProfileSummaries { get; set; } = [];
    public DateTime? LastPublishedUtc { get; set; }
    public DateTime? LastEpgUpdateUtc { get; set; }
    public bool RefreshFailed { get; set; }
    public string? LastChangeClass { get; set; }
    public DateTime? ActiveProfileProviderExpiresUtc { get; set; }
    public List<ExpiringProviderWarning> ExpiringProviders { get; set; } = [];
}
