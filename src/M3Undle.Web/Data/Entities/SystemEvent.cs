namespace M3Undle.Web.Data.Entities;

public sealed class SystemEvent
{
    public string SystemEventId { get; set; } = null!;
    public string EventType { get; set; } = null!;
    public string Severity { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Detail { get; set; }
    public string? ProviderId { get; set; }
    public string? IntegrationId { get; set; }
    public DateTime OccurredAt { get; set; }
    public int OccurrenceCount { get; set; } = 1;
}
