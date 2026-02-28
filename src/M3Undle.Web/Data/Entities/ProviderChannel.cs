namespace M3Undle.Web.Data.Entities;

public sealed class ProviderChannel
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderChannelKey { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? TvgId { get; set; }
    public string? TvgName { get; set; }
    public string? LogoUrl { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string? GroupTitle { get; set; }
    public string? ProviderGroupId { get; set; }
    public bool IsEvent { get; set; }
    public DateTime? EventStartUtc { get; set; }
    public DateTime? EventEndUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool Active { get; set; }
    public string ContentType { get; set; } = "live"; // 'live'|'vod'|'series'
    public string LastFetchRunId { get; set; } = string.Empty;

    public Provider Provider { get; set; } = null!;
    public ProviderGroup? ProviderGroup { get; set; }
    public FetchRun LastFetchRun { get; set; } = null!;
    public ICollection<ChannelSource> ChannelSources { get; set; } = new List<ChannelSource>();
    public ICollection<ProfileGroupChannelFilter> ChannelFilters { get; set; } = new List<ProfileGroupChannelFilter>();
}

