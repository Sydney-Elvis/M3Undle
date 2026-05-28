namespace M3Undle.Web.Contracts;

public sealed class ProfileStubDto
{
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class ProfileProviderInfoDto
{
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; }
    public DateTime? PlaylistExpiresUtc { get; set; }
}

public sealed class ProfilePageItemDto
{
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public string MergeMode { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<ProfileProviderInfoDto> Providers { get; set; } = [];
    public DateTime? LastPublishedUtc { get; set; }
    public int LiveCount { get; set; }
    public int MovieCount { get; set; }
    public int SeriesCount { get; set; }
    public ProfileHealthStatus HealthStatus { get; set; }
    public int GroupsPendingReview { get; set; }
    public int ChannelsPendingReview { get; set; }
}

public sealed class ProfileSnapshotHistoryDto
{
    public string SnapshotId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public int ChannelCountPublished { get; set; }
    public int LiveChannelCount { get; set; }
    public int VodChannelCount { get; set; }
    public int SeriesChannelCount { get; set; }
    public string? ErrorSummary { get; set; }
}

public sealed class ProfileDetailDto
{
    public ProfilePageItemDto Profile { get; set; } = new();
    public List<ProfileSnapshotHistoryDto> History { get; set; } = [];
}

public sealed class ProfileActiveResponse
{
    public string ProfileId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool RefreshTriggered { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
