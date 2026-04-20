namespace M3Undle.Web.Contracts;

public sealed class DownstreamIntegrationDto
{
    public string DownstreamIntegrationId { get; set; } = string.Empty;
    public string? ProfileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public string? WebhookHeadersJson { get; set; }
    public bool TriggerOnLineupUpdate { get; set; }
    public bool TriggerOnGuideUpdate { get; set; }
    public bool Enabled { get; set; }
    public DateTime? LastNotifiedUtc { get; set; }
    public string? LastNotifyError { get; set; }
}

public sealed class CreateDownstreamIntegrationRequest
{
    public string? ProfileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? WebhookHeadersJson { get; set; }
    public bool TriggerOnLineupUpdate { get; set; } = true;
    public bool TriggerOnGuideUpdate { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public sealed class UpdateDownstreamIntegrationRequest
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public bool ClearApiKey { get; set; }
    public string? WebhookHeadersJson { get; set; }
    public bool TriggerOnLineupUpdate { get; set; }
    public bool TriggerOnGuideUpdate { get; set; }
    public bool Enabled { get; set; }
}
