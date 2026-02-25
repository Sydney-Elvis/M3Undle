namespace M3Undle.Web.Data.Entities;

public sealed class StreamKey
{
    public string Value { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public bool Revoked { get; set; }

    public Profile Profile { get; set; } = null!;
    public CanonicalChannel Channel { get; set; } = null!;
}

