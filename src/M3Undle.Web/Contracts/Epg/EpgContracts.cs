namespace M3Undle.Web.Contracts.Epg;

// -------------------------------------------------------------------------
// EPG Source
// -------------------------------------------------------------------------

public sealed class EpgSourceDto
{
    public string EpgSourceId { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? UrlOrPath { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; }
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public string? LastErrorSummary { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CreateEpgSourceRequest
{
    public string? ProviderId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "xmltv_url";
    public string? UrlOrPath { get; set; }
    public int Priority { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class UpdateEpgSourceRequest
{
    public string? Name { get; set; }
    public string? UrlOrPath { get; set; }
    public int? Priority { get; set; }
    public bool? Enabled { get; set; }
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public int? TimeoutSeconds { get; set; }
}

public sealed class EpgSourceTestResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int ChannelCount { get; set; }
    public int ProgrammeCount { get; set; }
    public DateTimeOffset? EarliestProgramme { get; set; }
    public DateTimeOffset? LatestProgramme { get; set; }
    public long Bytes { get; set; }
    public int ElapsedMs { get; set; }
}

// -------------------------------------------------------------------------
// Channel mappings
// -------------------------------------------------------------------------

public sealed class EpgChannelMappingDto
{
    public string EpgChannelMappingId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderChannelDisplayName { get; set; } = string.Empty;
    public string? ProviderChannelTvgId { get; set; }
    public string EpgSourceId { get; set; } = string.Empty;
    public string EpgSourceName { get; set; } = string.Empty;
    public string XmltvChannelId { get; set; } = string.Empty;
    public string MappingMode { get; set; } = string.Empty;
    public float Confidence { get; set; }
}

public sealed class UpsertEpgMappingRequest
{
    public string ProfileId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public string EpgSourceId { get; set; } = string.Empty;
    public string XmltvChannelId { get; set; } = string.Empty;
}

// -------------------------------------------------------------------------
// EPG Source Channels (for the mapping UI picker)
// -------------------------------------------------------------------------

public sealed class EpgSourceChannelDto
{
    public string EpgSourceChannelId { get; set; } = string.Empty;
    public string XmltvChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
}

// -------------------------------------------------------------------------
// Fetch run history
// -------------------------------------------------------------------------

public sealed class EpgFetchRunDto
{
    public string EpgFetchRunId { get; set; } = string.Empty;
    public string EpgSourceId { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? Bytes { get; set; }
    public int? ChannelCount { get; set; }
    public int? ProgrammeCount { get; set; }
    public string? ErrorSummary { get; set; }
}
