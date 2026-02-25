namespace M3Undle.Web.Data.Entities;

public sealed class Profile
{
    public string ProfileId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
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
}

