using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Upstream;

public sealed class UpstreamStreamConnector(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<ReconnectOptions> reconnectOptions,
    ILogger<UpstreamStreamConnector> logger)
{
    private readonly ReconnectOptions _reconnectOptions = reconnectOptions.Value;

    public async Task<UpstreamConnection> ConnectAsync(StreamSourceDescriptor source, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProviderId == source.ProviderId && x.Enabled, ct);

        if (provider is null)
        {
            logger.LogWarning(
                "Cannot start stream for '{DisplayName}' — provider '{ProviderId}' is not found or is disabled.",
                source.DisplayName,
                source.ProviderId);
            throw new UpstreamConnectException(
                $"Provider '{source.ProviderId}' is not available.",
                UpstreamFailureKind.StartupFatal);
        }

        var effectiveStreamUrl = source.StreamUrl;
        if (!string.IsNullOrWhiteSpace(source.ProviderChannelId))
        {
            var refreshedStreamUrl = await db.ProviderChannels
                .AsNoTracking()
                .Where(x => x.ProviderChannelId == source.ProviderChannelId && x.ProviderId == source.ProviderId)
                .Select(x => x.StreamUrl)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(refreshedStreamUrl))
                effectiveStreamUrl = refreshedStreamUrl;
        }

        if (source.ForceMpegTs)
            effectiveStreamUrl = RewriteUrlForMpegTs(effectiveStreamUrl);

        var client = httpClientFactory.CreateClient("stream-relay");
        ProviderFetcher.ApplyHeadersFromJson(client, provider.HeadersJson);
        if (!string.IsNullOrWhiteSpace(provider.UserAgent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_reconnectOptions.ConnectTimeout);

        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, effectiveStreamUrl);
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                connectCts.Token);

            var statusCode = (int)response.StatusCode;
            if (statusCode is 401 or 403)
            {
                response.Dispose();
                logger.LogWarning(
                    "Provider rejected the stream request for '{DisplayName}' with {StatusCode} — check your provider credentials.",
                    source.DisplayName,
                    statusCode);
                throw new UpstreamConnectException("Provider authorization rejected stream request.", UpstreamFailureKind.UpstreamAuth, statusCode);
            }
            if (statusCode == 404)
            {
                response.Dispose();
                logger.LogWarning(
                    "Provider returned 404 for '{DisplayName}' — the stream URL may be invalid or the channel is unavailable.",
                    source.DisplayName);
                throw new UpstreamConnectException("Provider stream endpoint not found.", UpstreamFailureKind.UpstreamNotFound, statusCode);
            }
            if (statusCode >= 500)
            {
                response.Dispose();
                logger.LogWarning(
                    "Provider returned a server error ({StatusCode}) for '{DisplayName}' — will retry.",
                    statusCode,
                    source.DisplayName);
                throw new UpstreamConnectException($"Upstream returned {statusCode}.", UpstreamFailureKind.UpstreamServerError, statusCode);
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new UpstreamConnectException($"Upstream returned non-success status {statusCode}.", UpstreamFailureKind.StartupFatal, statusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            logger.LogInformation(
                "Connected to upstream for '{DisplayName}' — HTTP {Status}, content type: {ContentType}.",
                source.DisplayName,
                statusCode,
                response.Content.Headers.ContentType?.ToString() ?? "unknown");

            var connection = new UpstreamConnection(client, response, stream);
            response = null; // ownership transferred to UpstreamConnection
            return connection;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            response?.Dispose();
            client.Dispose();
            throw new UpstreamConnectException("Upstream connection attempt timed out.", UpstreamFailureKind.TimeoutOrStall, null, ex);
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            client.Dispose();
            throw new UpstreamConnectException("Upstream request failed.", UpstreamFailureKind.Transport, ex.StatusCode is null ? null : (int)ex.StatusCode, ex);
        }
        catch (UpstreamConnectException)
        {
            response?.Dispose();
            client.Dispose();
            throw;
        }
    }

    internal static string RewriteUrlForMpegTs(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var path = uri.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = path[..^5];
            uri = new UriBuilder(uri) { Path = stripped }.Uri;
        }

        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
            return uri.ToString();

        var pairs = query.TrimStart('?').Split('&');
        var modified = false;
        for (var i = 0; i < pairs.Length; i++)
        {
            var kv = pairs[i].Split('=', 2);
            if (kv.Length != 2) continue;
            var key = Uri.UnescapeDataString(kv[0]);
            if (!key.Equals("output", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(kv[1]);
            if (value.Equals("m3u8", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("hls", StringComparison.OrdinalIgnoreCase))
            {
                pairs[i] = $"{kv[0]}=ts";
                modified = true;
            }
        }

        if (!modified)
            return uri.ToString();

        return new UriBuilder(uri) { Query = string.Join("&", pairs) }.Uri.ToString();
    }

    public UpstreamFailureKind Classify(Exception ex, int? statusCode = null)
    {
        if (ex is UpstreamConnectException connectException)
            return connectException.FailureKind;

        if (ex is OperationCanceledException)
            return UpstreamFailureKind.TimeoutOrStall;

        if (ex is HttpRequestException httpEx)
        {
            var code = statusCode ?? (httpEx.StatusCode is null ? null : (int)httpEx.StatusCode);
            if (code is 401 or 403)
                return UpstreamFailureKind.UpstreamAuth;
            if (code == 404)
                return UpstreamFailureKind.UpstreamNotFound;
            if (code >= 500)
                return UpstreamFailureKind.UpstreamServerError;
            return UpstreamFailureKind.Transport;
        }

        return UpstreamFailureKind.Unknown;
    }
}
