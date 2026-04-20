namespace M3Undle.Web.Data.Entities;

public sealed class ProfileGroupFilter
{
    public string ProfileGroupFilterId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string Decision { get; set; } = "include"; // include | exclude
    public bool IsNew { get; set; } = true;
    public string ChannelMode { get; set; } = "select"; // select (manual_review) | all (auto_update)
    public string TrackingPolicy { get; set; } = "review"; // review | notify | auto_add_all | auto_add_matching
    public string? TrackingKeywords { get; set; } // team/league/fighter/race keyword lines for auto_add_matching
    public string? OutputName { get; set; }
    public int? AutoNumStart { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool TrackNewChannels { get; set; } // notify (true) | mute (false)
    public int? SortOverride { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public ProviderGroup ProviderGroup { get; set; } = null!;
    public ICollection<ProfileGroupChannelFilter> ChannelFilters { get; set; } = new List<ProfileGroupChannelFilter>();
}
