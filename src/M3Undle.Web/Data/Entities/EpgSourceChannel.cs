namespace M3Undle.Web.Data.Entities;

public sealed class EpgSourceChannel
{
    public string EpgSourceChannelId { get; set; } = string.Empty;
    public string EpgSourceId { get; set; } = string.Empty;
    public string XmltvChannelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public EpgSource EpgSource { get; set; } = null!;
}
