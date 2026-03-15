namespace M3Undle.Web.Data.Entities;

public sealed class SiteSettings
{
    public int Id { get; set; }
    public bool AuthenticationEnabled { get; set; }
    public bool EndpointSecurityEnabled { get; set; }
    public bool StreamingEnabled { get; set; } = true;
}

