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
    // 'live' when the group carries at least one live channel (it may also carry catalog
    // items); otherwise 'vod' or 'series' by dominant catalog type. Legacy rows may still
    // hold 'mixed' until the provider's next refresh reclassifies them.
    public string ContentType { get; set; } = "live"; // 'live'|'vod'|'series'

    public Provider Provider { get; set; } = null!;
    public ICollection<ProviderChannel> ProviderChannels { get; set; } = new List<ProviderChannel>();
    public ICollection<ProfileGroupFilter> ProfileGroupFilters { get; set; } = new List<ProfileGroupFilter>();
    public ICollection<ProfileCustomGroupProviderLink> CustomGroupProviderLinks { get; set; } = new List<ProfileCustomGroupProviderLink>();
}

