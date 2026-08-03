namespace M3Undle.Web.Data.Entities;

public sealed class ProfileCatalogGroupFilter
{
    public string ProfileCatalogGroupFilterId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string ProviderGroupId { get; set; } = string.Empty;
    public string Decision { get; set; } = "include";
    public bool IsNew { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public ProviderGroup ProviderGroup { get; set; } = null!;
}
