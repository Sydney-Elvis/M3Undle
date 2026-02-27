namespace M3Undle.Web.Data.Entities;

public sealed class ProfileGroupChannelFilter
{
    public string ProfileGroupChannelFilterId { get; set; } = string.Empty;
    public string ProfileGroupFilterId { get; set; } = string.Empty;
    public string ProviderChannelId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    public ProfileGroupFilter ProfileGroupFilter { get; set; } = null!;
    public ProviderChannel ProviderChannel { get; set; } = null!;
}
