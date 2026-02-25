namespace M3Undle.Web.Data.Entities;

public sealed class ChannelSource
{
    public string ChannelSourceId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; }
    public string? OverrideStreamUrl { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public int FailureCountRolling { get; set; }
    public string HealthState { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public CanonicalChannel Channel { get; set; } = null!;
    public Provider Provider { get; set; } = null!;
    public ProviderChannel ProviderChannel { get; set; } = null!;
}

