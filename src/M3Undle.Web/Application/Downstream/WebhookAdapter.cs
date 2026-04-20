using System.Net.Http.Json;
using System.Text.Json;

namespace M3Undle.Web.Application.Downstream;

/// <summary>
/// Sends a POST request to the configured webhook URL.
/// Optional extra headers can be provided as a JSON object (key→value string pairs).
/// The body is a small JSON payload describing the trigger kind.
/// </summary>
public sealed class WebhookAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookAdapter> logger) : IDownstreamAdapter
{
    public string Kind => "webhook";

    public async Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("downstream");

            if (!string.IsNullOrWhiteSpace(webhookHeadersJson))
            {
                try
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(webhookHeadersJson);
                    if (headers is not null)
                    {
                        foreach (var (key, value) in headers)
                        {
                            if (key.AsSpan().ContainsAny('\r', '\n') || value.AsSpan().ContainsAny('\r', '\n'))
                                continue;
                            client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
                        }
                    }
                }
                catch (JsonException) { }
            }

            var payload = new
            {
                trigger = trigger == DownstreamTrigger.LineupUpdate ? "lineup_update" : "guide_update",
                source = "m3undle",
                timestamp = DateTime.UtcNow,
            };

            var response = await client.PostAsJsonAsync(baseUrl, payload, ct);
            if (!response.IsSuccessStatusCode)
                return $"Webhook returned {(int)response.StatusCode} {response.ReasonPhrase}";

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook notify failed for {BaseUrl}", baseUrl);
            return ex.Message;
        }
    }
}
