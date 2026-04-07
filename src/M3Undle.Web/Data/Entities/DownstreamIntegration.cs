namespace M3Undle.Web.Data.Entities;

public sealed class DownstreamIntegration
{
    public string DownstreamIntegrationId { get; set; } = string.Empty;
    public string? ProfileId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>jellyfin | emby | webhook</summary>
    public string Kind { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key encrypted via SecretEncryptionService. Used by jellyfin and emby kinds.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>Extra HTTP headers as JSON object. Used by webhook kind.</summary>
    public string? WebhookHeadersJson { get; set; }

    public bool TriggerOnLineupUpdate { get; set; } = true;
    public bool TriggerOnGuideUpdate { get; set; } = true;
    public bool Enabled { get; set; } = true;

    public DateTime? LastNotifiedUtc { get; set; }
    public string? LastNotifyError { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile? Profile { get; set; }
}
