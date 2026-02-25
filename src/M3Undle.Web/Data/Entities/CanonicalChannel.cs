namespace M3Undle.Web.Data.Entities;

public sealed class CanonicalChannel
{
    public string ChannelId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int ChannelNumber { get; set; }
    public string? GroupName { get; set; }
    public string? LogoUrl { get; set; }
    public bool Enabled { get; set; }
    public bool IsEvent { get; set; }
    public string EventPolicy { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public ICollection<ChannelSource> ChannelSources { get; set; } = new List<ChannelSource>();
    public ICollection<ChannelMatchRule> TargetingMatchRules { get; set; } = new List<ChannelMatchRule>();
    public ICollection<EpgChannelMap> EpgChannelMaps { get; set; } = new List<EpgChannelMap>();
    public ICollection<StreamKey> StreamKeys { get; set; } = new List<StreamKey>();
}

