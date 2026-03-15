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

        var client = httpClientFactory.CreateClient("stream-relay");
        ProviderFetcher.ApplyHeadersFromJson(client, provider.HeadersJson);
        if (!string.IsNullOrWhiteSpace(provider.UserAgent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_reconnectOptions.ConnectTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, effectiveStreamUrl);
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                connectCts.Token);

            var statusCode = (int)response.StatusCode;
            if (statusCode is 401 or 403)
                throw new UpstreamConnectException("Provider authorization rejected stream request.", UpstreamFailureKind.UpstreamAuth, statusCode);
            if (statusCode == 404)
                throw new UpstreamConnectException("Provider stream endpoint not found.", UpstreamFailureKind.UpstreamNotFound, statusCode);
            if (statusCode >= 500)
                throw new UpstreamConnectException($"Upstream returned {statusCode}.", UpstreamFailureKind.UpstreamServerError, statusCode);
            if (!response.IsSuccessStatusCode)
                throw new UpstreamConnectException($"Upstream returned non-success status {statusCode}.", UpstreamFailureKind.StartupFatal, statusCode);

            var stream = await response.Content.ReadAsStreamAsync(ct);
            logger.LogDebug(
                "Connected upstream stream for {ProviderId}/{ProviderChannelId} with status {Status}.",
                source.ProviderId,
                source.ProviderChannelId,
                statusCode);

            return new UpstreamConnection(client, response, stream);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new UpstreamConnectException("Upstream connection attempt timed out.", UpstreamFailureKind.TimeoutOrStall, null, ex);
        }
        catch (HttpRequestException ex)
        {
            client.Dispose();
            throw new UpstreamConnectException("Upstream request failed.", UpstreamFailureKind.Transport, ex.StatusCode is null ? null : (int)ex.StatusCode, ex);
        }
        catch (UpstreamConnectException)
        {
            client.Dispose();
            throw;
        }
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
