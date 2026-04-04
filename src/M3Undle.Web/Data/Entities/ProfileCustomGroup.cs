namespace M3Undle.Web.Data.Entities;

public sealed class ProfileCustomGroup
{
    public string CustomGroupId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Decision { get; set; } = "include"; // include | exclude
    public string ChannelMode { get; set; } = "select"; // select (manual_review) | all (auto_update)
    public string TrackingPolicy { get; set; } = "review"; // review | notify | auto_add_all | auto_add_matching
    public string? TrackingKeywords { get; set; }
    public int? AutoNumStart { get; set; }
    public int? AutoNumEnd { get; set; }
    public bool TrackNewChannels { get; set; } // notify (true) | mute (false)
    public int? SortOverride { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public ICollection<ProfileCustomGroupChannel> Channels { get; set; } = new List<ProfileCustomGroupChannel>();
    public ICollection<ProfileCustomGroupProviderLink> ProviderLinks { get; set; } = new List<ProfileCustomGroupProviderLink>();
}
