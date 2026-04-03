namespace M3Undle.Web.Data.Entities;

public sealed class ProfileCustomGroupChannel
{
    public string CustomGroupChannelId { get; set; } = string.Empty;
    public string CustomGroupId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public string State { get; set; } = "included"; // pending | included | excluded
    public int? ChannelNumber { get; set; }
    public string? DisplayNameOverride { get; set; }
    public string? TvgIdOverride { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ProfileCustomGroup CustomGroup { get; set; } = null!;
    public ProviderChannel ProviderChannel { get; set; } = null!;
}
