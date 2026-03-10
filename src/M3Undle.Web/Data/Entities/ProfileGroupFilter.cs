namespace M3Undle.Web.Data.Entities;

public sealed class ProfileGroupFilter
{
    public string ProfileGroupFilterId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string Decision { get; set; } = "hold"; // 'hold' | 'exclude'
    public bool IsNew { get; set; } = true;
    public string ChannelMode { get; set; } = "select"; // always 'select'
    public string? OutputName { get; set; }
    public int? AutoNumStart { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool TrackNewChannels { get; set; }
    public int? SortOverride { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public ProviderGroup ProviderGroup { get; set; } = null!;
    public ICollection<ProfileGroupChannelFilter> ChannelFilters { get; set; } = new List<ProfileGroupChannelFilter>();
}
