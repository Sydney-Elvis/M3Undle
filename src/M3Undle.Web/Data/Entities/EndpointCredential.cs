namespace M3Undle.Web.Data.Entities;

public sealed class EndpointCredential
{
    public string EndpointCredentialId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string NormalizedUsername { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string AuthType { get; set; } = "password";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public ICollection<EndpointAccessBinding> Bindings { get; set; } = new List<EndpointAccessBinding>();
}
