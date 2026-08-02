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
    // Group identity includes this value, so same-named live, VOD and series categories are
    // stored separately. Legacy rows may hold 'mixed' until the provider's next refresh.
    public string ContentType { get; set; } = "live"; // 'live'|'vod'|'series'

    public Provider Provider { get; set; } = null!;
    public ICollection<ProviderChannel> ProviderChannels { get; set; } = new List<ProviderChannel>();
    public ICollection<ProfileGroupFilter> ProfileGroupFilters { get; set; } = new List<ProfileGroupFilter>();
    public ICollection<ProfileCustomGroupProviderLink> CustomGroupProviderLinks { get; set; } = new List<ProfileCustomGroupProviderLink>();
}
