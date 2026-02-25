namespace M3Undle.Web.Data.Entities;

public sealed class ProfileProvider
{
    public string ProfileId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; }

    public Profile Profile { get; set; } = null!;
    public Provider Provider { get; set; } = null!;
}

