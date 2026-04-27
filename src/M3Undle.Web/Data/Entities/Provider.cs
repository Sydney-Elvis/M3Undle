namespace M3Undle.Web.Data.Entities;

public sealed class Provider
{
    public string ProviderId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string PlaylistUrl { get; set; } = string.Empty;
    public string? XmltvUrl { get; set; }
    public string? HeadersJson { get; set; }
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int? MaxConcurrentStreams { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public bool IncludeVod { get; set; }
    public bool IncludeSeries { get; set; }

    public bool ForceMpegTs { get; set; }

    // Xtream Codes API provider fields
    public string? XtreamBaseUrl { get; set; }
    public string? XtreamUsername { get; set; }
    public string? XtreamEncryptedPassword { get; set; }
    public bool XtreamIncludeXmltv { get; set; }

    // Set true when player_api.php probe succeeds for an M3U URL with embedded credentials
    public bool XtreamDetectedCapable { get; set; }

    // UTC expiry from the Xtream player_api.php user_info.exp_date; null if unknown or not Xtream-capable
    public DateTime? PlaylistExpiresUtc { get; set; }

    public ICollection<ProfileProvider> ProfileProviders { get; set; } = new List<ProfileProvider>();
    public ICollection<FetchRun> FetchRuns { get; set; } = new List<FetchRun>();
    public ICollection<ProviderGroup> ProviderGroups { get; set; } = new List<ProviderGroup>();
    public ICollection<ProviderChannel> ProviderChannels { get; set; } = new List<ProviderChannel>();
    public ICollection<ChannelSource> ChannelSources { get; set; } = new List<ChannelSource>();
}

