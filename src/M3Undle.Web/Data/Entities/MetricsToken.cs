namespace M3Undle.Web.Data.Entities;

public sealed class MetricsToken
{
    public string MetricsTokenId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string Scope { get; set; } = "metrics:read";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
}
