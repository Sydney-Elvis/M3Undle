using System.ComponentModel.DataAnnotations;

namespace M3Undle.Web.Contracts.Providers;

public sealed class ProfileListItemDto
{
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public string MergeMode { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public sealed class ProviderLastRefreshDto
{
    public string Status { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string? ErrorSummary { get; set; }
    public int? ChannelCountSeen { get; set; }
}

public sealed class ProviderLatestSnapshotDto
{
    public string SnapshotId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class ProviderDto
{
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PlaylistUrl { get; set; } = string.Empty;
    public string? XmltvUrl { get; set; }
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public bool Enabled { get; set; }
    public bool IsActive { get; set; }
    public int TimeoutSeconds { get; set; }
    public bool IncludeVod { get; set; }
    public bool IncludeSeries { get; set; }
    public List<string> AssociatedProfileIds { get; set; } = [];
    public ProviderLastRefreshDto? LastRefresh { get; set; }
    public List<ProviderLatestSnapshotDto> LatestSnapshots { get; set; } = [];
}

public sealed class ProviderStatusDto
{
    public string ProviderId { get; set; } = string.Empty;
    public ProviderLastRefreshDto? LastRefresh { get; set; }
    public List<ProviderLatestSnapshotDto> LatestSnapshots { get; set; } = [];
}

public sealed class CreateProviderRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string PlaylistUrl { get; set; } = string.Empty;

    public string? XmltvUrl { get; set; }
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IncludeVod { get; set; }
    public bool IncludeSeries { get; set; }

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 20;

    public List<string>? AssociateToProfileIds { get; set; }
}

public sealed class UpdateProviderRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string PlaylistUrl { get; set; } = string.Empty;

    public string? XmltvUrl { get; set; }
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public bool Enabled { get; set; }
    public bool IncludeVod { get; set; }
    public bool IncludeSeries { get; set; }

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 20;

    public List<string>? AssociateToProfileIds { get; set; }
}

public sealed class SetProviderEnabledRequest
{
    public bool Enabled { get; set; }
}

public sealed class ProviderEnabledResponse
{
    public string ProviderId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ProviderPreviewTotalsDto
{
    public int GroupCount { get; set; }
    public int ChannelCount { get; set; }
}

public sealed class ProviderPreviewSourceDto
{
    public string Kind { get; set; } = "latest-successful-provider-refresh";
    public string FetchRunId { get; set; } = string.Empty;
    public DateTime FetchStartedUtc { get; set; }
}

public sealed class ProviderPreviewSampleChannelDto
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? TvgId { get; set; }
    public bool HasStreamUrl { get; set; }
    public string? StreamUrlRedacted { get; set; }
}

public sealed class ProviderPreviewGroupDto
{
    public string GroupName { get; set; } = string.Empty;
    public int ChannelCount { get; set; }
    public List<ProviderPreviewSampleChannelDto> SampleChannels { get; set; } = [];
}

public sealed class ProviderPreviewDto
{
    public string ProviderId { get; set; } = string.Empty;
    public DateTime PreviewGeneratedUtc { get; set; }
    public ProviderPreviewSourceDto Source { get; set; } = new();
    public ProviderPreviewTotalsDto Totals { get; set; } = new();
    public List<ProviderPreviewGroupDto> Groups { get; set; } = [];
}

public sealed class RefreshPreviewRequest
{
    public int? SampleSize { get; set; }
    public string? GroupContains { get; set; }
}

public sealed class SetProviderActiveRequest
{
    public bool IsActive { get; set; }
}

public sealed class ProviderActiveResponse
{
    public string ProviderId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ConfigYamlProviderDto
{
    public string Name { get; set; } = string.Empty;
    public string PlaylistUrl { get; set; } = string.Empty;
    public string? XmltvUrl { get; set; }
    public bool RequiresEnvVars { get; set; }
    public List<string> MissingEnvVars { get; set; } = [];
}

public sealed class ImportConfigProviderRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
    public bool IncludeVod { get; set; }
    public bool IncludeSeries { get; set; }
}

public sealed class CreateProfileRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
}

public sealed class ProviderHealthDto
{
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool CanFetch { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<string> MissingEnvVars { get; set; } = [];
    public string? LastError { get; set; }
    public DateTime? LastSuccessFetch { get; set; }
}

public sealed class ProbeConfigProviderRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
}

public sealed class ProbeConfigProviderResultDto
{
    public bool Ok { get; set; }
    public int? ChannelCount { get; set; }
    public string? Error { get; set; }
}

