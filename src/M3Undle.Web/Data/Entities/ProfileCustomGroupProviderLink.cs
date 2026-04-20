namespace M3Undle.Web.Data.Entities;

public sealed class ProfileCustomGroupProviderLink
{
    public string LinkId { get; set; } = string.Empty;
    public string CustomGroupId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    public ProfileCustomGroup CustomGroup { get; set; } = null!;
    public ProviderGroup ProviderGroup { get; set; } = null!;
}
