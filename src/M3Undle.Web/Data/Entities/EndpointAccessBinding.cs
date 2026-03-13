namespace M3Undle.Web.Data.Entities;

public sealed class EndpointAccessBinding
{
    public string EndpointAccessBindingId { get; set; } = string.Empty;
    public string EndpointCredentialId { get; set; } = string.Empty;
    public string? ActiveProfileId { get; set; }
    public string? DefaultProfileId { get; set; }
    public string? VirtualTunerId { get; set; }
    public bool Enabled { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public EndpointCredential Credential { get; set; } = null!;
    public Profile? ActiveProfile { get; set; }
    public Profile? DefaultProfile { get; set; }
}
