namespace M3Undle.Web.Data.Entities;

public sealed class ProviderGroup
{
    public string ProviderGroupId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string RawName { get; set; } = string.Empty;
    public string? NormalizedName { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool Active { get; set; }
    public int? ChannelCount { get; set; }
    public string ContentType { get; set; } = "live"; // 'live'|'vod'|'series'|'mixed'

    public Provider Provider { get; set; } = null!;
    public ICollection<ProviderChannel> ProviderChannels { get; set; } = new List<ProviderChannel>();
    public ICollection<ProfileGroupFilter> ProfileGroupFilters { get; set; } = new List<ProfileGroupFilter>();
}

