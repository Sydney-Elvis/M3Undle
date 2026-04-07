namespace M3Undle.Web.Application.Downstream;

public enum DownstreamTrigger
{
    LineupUpdate,
    GuideUpdate,
}

public interface IDownstreamAdapter
{
    string Kind { get; }
    Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct);
}
