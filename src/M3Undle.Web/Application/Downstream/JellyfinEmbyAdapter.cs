using System.Net.Http.Headers;
using System.Text.Json;

namespace M3Undle.Web.Application.Downstream;

/// <summary>
/// Notifies Jellyfin or Emby after a lineup or guide update.
///
/// Lineup update:  POST {baseUrl}/Library/Refresh  (triggers a full library scan)
/// Guide update:   POST {baseUrl}/ScheduledTasks/Running/{taskId}
///                 where taskId is the id of the "Refresh Guide" scheduled task.
///
/// Auth header:
///   Jellyfin: Authorization: MediaBrowser Token={apiKey}
///   Emby:     X-Emby-Authorization: MediaBrowser Token={apiKey}
///             (also accepts the same Authorization header in recent builds)
/// </summary>
public sealed class JellyfinEmbyAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<JellyfinEmbyAdapter> logger) : IDownstreamAdapter
{
    public string Kind => "jellyfin_emby";

    public async Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("downstream");
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token={apiKey}");

            if (trigger == DownstreamTrigger.LineupUpdate)
            {
                var response = await client.PostAsync($"{baseUrl}/Library/Refresh", null, ct);
                if (!response.IsSuccessStatusCode)
                    return $"Library/Refresh returned {(int)response.StatusCode} {response.ReasonPhrase}";

                return null;
            }

            // Guide update — discover the scheduled task id
            var taskId = await FindGuideTaskIdAsync(client, baseUrl, ct);
            if (taskId is null)
                return "Could not find a 'Refresh Guide' scheduled task on the server.";

            var guideResponse = await client.PostAsync($"{baseUrl}/ScheduledTasks/Running/{taskId}", null, ct);
            if (!guideResponse.IsSuccessStatusCode)
                return $"ScheduledTasks/Running returned {(int)guideResponse.StatusCode} {guideResponse.ReasonPhrase}";

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Downstream notify failed for {BaseUrl}", baseUrl);
            return ex.Message;
        }
    }

    private static async Task<string?> FindGuideTaskIdAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        var response = await client.GetAsync($"{baseUrl}/ScheduledTasks", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Array of task objects with Id and Name fields
        foreach (var task in root.EnumerateArray())
        {
            if (task.TryGetProperty("Name", out var nameProp))
            {
                var name = nameProp.GetString() ?? string.Empty;
                if (name.Contains("guide", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("epg", StringComparison.OrdinalIgnoreCase))
                {
                    if (task.TryGetProperty("Id", out var idProp))
                        return idProp.GetString();
                }
            }
        }

        return null;
    }
}

/// <summary>Adapter registered for "jellyfin" kind.</summary>
public sealed class JellyfinAdapter(IHttpClientFactory f, ILogger<JellyfinEmbyAdapter> l) : IDownstreamAdapter
{
    private readonly JellyfinEmbyAdapter _inner = new(f, l);
    public string Kind => "jellyfin";
    public Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        => _inner.NotifyAsync(baseUrl, apiKey, webhookHeadersJson, trigger, ct);
}

/// <summary>Adapter registered for "emby" kind.</summary>
public sealed class EmbyAdapter(IHttpClientFactory f, ILogger<JellyfinEmbyAdapter> l) : IDownstreamAdapter
{
    private readonly JellyfinEmbyAdapter _inner = new(f, l);
    public string Kind => "emby";
    public Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        => _inner.NotifyAsync(baseUrl, apiKey, webhookHeadersJson, trigger, ct);
}
