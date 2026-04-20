namespace M3Undle.Web.Data.Entities;

public sealed class ProfileGroupChannelFilter
{
    public string ProfileGroupChannelFilterId { get; set; } = string.Empty;
    public string ProfileGroupFilterId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public string State { get; set; } = "included"; // pending | included | excluded
    public string? DisplayNameOverride { get; set; }
    public string? OutputGroupName { get; set; }
    public int? ChannelNumber { get; set; }
    public string? TvgIdOverride { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ProfileGroupFilter ProfileGroupFilter { get; set; } = null!;
    public ProviderChannel ProviderChannel { get; set; } = null!;
}
