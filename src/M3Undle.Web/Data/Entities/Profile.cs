namespace M3Undle.Web.Data.Entities;

public sealed class Profile
{
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool IsActive { get; set; }
    public string OutputName { get; set; } = string.Empty;
    public string MergeMode { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ICollection<ProfileProvider> ProfileProviders { get; set; } = new List<ProfileProvider>();
    public ICollection<CanonicalChannel> CanonicalChannels { get; set; } = new List<CanonicalChannel>();
    public ICollection<ChannelMatchRule> ChannelMatchRules { get; set; } = new List<ChannelMatchRule>();
    public ICollection<EpgChannelMap> EpgChannelMaps { get; set; } = new List<EpgChannelMap>();
    public ICollection<Snapshot> Snapshots { get; set; } = new List<Snapshot>();
    public ICollection<StreamKey> StreamKeys { get; set; } = new List<StreamKey>();
    public ICollection<ProfileGroupFilter> ProfileGroupFilters { get; set; } = new List<ProfileGroupFilter>();
    public ICollection<ProfileEventInterestRule> EventInterestRules { get; set; } = new List<ProfileEventInterestRule>();
    public ICollection<ProfileCustomGroup> CustomGroups { get; set; } = new List<ProfileCustomGroup>();
    public ICollection<EndpointAccessBinding> ActiveEndpointAccessBindings { get; set; } = new List<EndpointAccessBinding>();
    public ICollection<EndpointAccessBinding> DefaultEndpointAccessBindings { get; set; } = new List<EndpointAccessBinding>();
}

