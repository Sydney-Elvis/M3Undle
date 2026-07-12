using System.Text;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Streaming.Compatibility;

/// <summary>
/// Fetches HLS manifests from upstream providers, rewrites segment URIs to go through
/// M3Undle's segment proxy, and proxies raw segment bytes to clients.
/// </summary>
public sealed class HlsProxyService(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    HlsManifestRewriter manifestRewriter,
    ILogger<HlsProxyService> logger)
{
    /// <summary>
    /// Tries each URL in <paramref name="candidates"/> in order, fetching and rewriting the first
    /// that returns a valid M3U8 manifest.  Returns null if all candidates fail.
    /// </summary>
    public async Task<string?> FetchAndRewriteManifestAsync(
        IReadOnlyList<string> candidates,
        StreamSourceDescriptor sourceDescriptor,
        string segmentProxyBaseUrl,
        CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("stream-relay");
        await ApplyProviderHeadersAsync(client, sourceDescriptor.ProviderId, ct);

        foreach (var upstreamUrl in candidates)
        {
            var result = await TryFetchManifestAsync(client, upstreamUrl, sourceDescriptor, segmentProxyBaseUrl, ct);
            if (result is not null)
                return result;
        }

        return null;
    }

    private async Task<string?> TryFetchManifestAsync(
        HttpClient client,
        string upstreamM3u8Url,
        StreamSourceDescriptor sourceDescriptor,
        string segmentProxyBaseUrl,
        CancellationToken ct)
    {
        try
        {
            using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fetchCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await client.GetAsync(
                upstreamM3u8Url,
                HttpCompletionOption.ResponseHeadersRead,
                fetchCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "HLS manifest fetch returned {Status} for '{Channel}' — upstream: {Url}",
                    (int)response.StatusCode,
                    sourceDescriptor.DisplayName,
                    upstreamM3u8Url);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            if (!content.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Upstream response for '{Channel}' is not an M3U8 manifest (no #EXTM3U header).",
                    sourceDescriptor.DisplayName);
                return null;
            }

            logger.LogInformation(
                "HLS manifest fetched for '{Channel}' via {Url}",
                sourceDescriptor.DisplayName,
                upstreamM3u8Url);

            var manifestUri = response.RequestMessage?.RequestUri ?? new Uri(upstreamM3u8Url);
            return manifestRewriter.Rewrite(content, manifestUri, segUri =>
                BuildProxyUrl(segmentProxyBaseUrl, segUri.ToString()));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("HLS manifest fetch timed out for '{Channel}' — {Url}",
                sourceDescriptor.DisplayName, upstreamM3u8Url);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HLS manifest fetch failed for '{Channel}' — {Url}",
                sourceDescriptor.DisplayName, upstreamM3u8Url);
            return null;
        }
    }

    /// <summary>
    /// Proxies a single upstream URL (segment or sub-manifest) to the client response.
    /// If the upstream returns an M3U8, it is rewritten before forwarding.
    /// When <paramref name="providerId"/> is supplied the same provider-specific headers
    /// (User-Agent, custom headers) that are applied during manifest fetch are also applied
    /// here, so segment/sub-manifest requests succeed for providers that require them.
    /// </summary>
    public async Task ProxyAsync(
        HttpContext context,
        string upstreamUrl,
        string segmentProxyBaseUrl,
        string? providerId,
        CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("stream-relay");
        await ApplyProviderHeadersAsync(client, providerId, ct);

        try
        {
            using var response = await client.GetAsync(
                upstreamUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                return;
            }

            var contentType = response.Content.Headers.ContentType?.ToString();

            // Sub-manifest (e.g. variant playlist or media playlist from master) — rewrite it too
            if (HlsDetection.IsHlsContentType(contentType)
                || upstreamUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                if (content.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
                {
                    var manifestUri = response.RequestMessage?.RequestUri ?? new Uri(upstreamUrl);
                    var rewritten = manifestRewriter.Rewrite(content, manifestUri, segUri =>
                        BuildProxyUrl(segmentProxyBaseUrl, segUri.ToString()));

                    context.Response.ContentType = "application/vnd.apple.mpegurl";
                    context.Response.Headers.CacheControl = "no-cache";
                    await context.Response.WriteAsync(rewritten, ct);
                    return;
                }
            }

            // Raw segment — stream bytes through unchanged
            context.Response.StatusCode = (int)response.StatusCode;
            if (contentType is not null)
                context.Response.ContentType = contentType;
            if (response.Content.Headers.ContentLength.HasValue)
                context.Response.ContentLength = response.Content.Headers.ContentLength.Value;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(context.Response.Body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected — normal
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "HLS segment proxy request failed for {Url}", upstreamUrl);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    private static string BuildProxyUrl(string segmentProxyBaseUrl, string upstreamUri)
    {
        var encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(upstreamUri));
        return QueryHelpers.AddQueryString(segmentProxyBaseUrl, "u", encoded);
    }

    private async Task ApplyProviderHeadersAsync(HttpClient client, string? providerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        var (userAgent, headersJson) = await GetProviderMetaAsync(providerId, ct);
        if (!string.IsNullOrWhiteSpace(userAgent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        if (!string.IsNullOrWhiteSpace(headersJson))
            ProviderFetcher.ApplyHeadersFromJson(client, headersJson);
    }

    private async Task<(string? UserAgent, string? HeadersJson)> GetProviderMetaAsync(
        string providerId,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.Enabled)
            .Select(x => new { x.UserAgent, x.HeadersJson })
            .FirstOrDefaultAsync(ct);

        return provider is null ? (null, null) : (provider.UserAgent, provider.HeadersJson);
    }
}
