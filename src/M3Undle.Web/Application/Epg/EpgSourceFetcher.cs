using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Epg;

/// <summary>
/// Fetches raw XMLTV content for a single <see cref="EpgSource"/>.
/// Supports xmltv_url (HTTP with ETag caching), xmltv_file, and provider_xmltv kinds.
/// Caches the last successful payload to disk so it survives restarts and source failures.
/// </summary>
public sealed class EpgSourceFetcher(
    IHttpClientFactory httpClientFactory,
    ProviderFetcher providerFetcher,
    EnvironmentVariableService envVarService,
    ILogger<EpgSourceFetcher> logger)
{
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public sealed record FetchResult(
        string? Xml,              // null = use cached (not_modified)
        string Status,            // ok | fail | not_modified
        long Bytes,
        string? ETag,
        DateTime? LastModifiedUtc,
        string? ErrorSummary);

    /// <summary>
    /// Fetch the XMLTV payload for <paramref name="source"/>.
    /// Falls back to <paramref name="cacheFilePath"/> on failure if it exists.
    /// </summary>
    public async Task<(FetchResult Result, string? EffectiveXml)> FetchAsync(
        EpgSource source,
        Provider? provider,
        string cacheFilePath,
        CancellationToken cancellationToken)
    {
        FetchResult result;
        try
        {
            result = source.Kind switch
            {
                "xmltv_file" => await FetchFileAsync(source, cancellationToken),
                "provider_xmltv" => await FetchProviderXmltvAsync(source, provider, cancellationToken),
                _ => await FetchHttpAsync(source, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "EPG source {EpgSourceId} ({Name}) fetch failed unexpectedly.", source.EpgSourceId, source.Name);
            result = new FetchResult(null, "fail", 0, null, null, ex.Message);
        }

        string? effectiveXml = null;

        if (result.Status == "ok" && result.Xml is not null)
        {
            effectiveXml = result.Xml;
            await WriteCacheAsync(cacheFilePath, effectiveXml, cancellationToken);
        }
        else if (result.Status == "not_modified")
        {
            effectiveXml = await ReadCacheAsync(cacheFilePath, cancellationToken);
        }
        else if (result.Status == "fail" && File.Exists(cacheFilePath))
        {
            logger.LogWarning("EPG source {EpgSourceId} fetch failed — using cached payload from disk.", source.EpgSourceId);
            effectiveXml = await ReadCacheAsync(cacheFilePath, cancellationToken);
        }

        return (result, effectiveXml);
    }

    // -------------------------------------------------------------------------
    // Kind-specific fetch implementations
    // -------------------------------------------------------------------------

    private static async Task<FetchResult> FetchFileAsync(EpgSource source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.UrlOrPath))
            return new FetchResult(null, "fail", 0, null, null, "No file path configured.");

        var localPath = source.UrlOrPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(source.UrlOrPath).LocalPath
            : source.UrlOrPath;

        try
        {
            var xml = await File.ReadAllTextAsync(localPath, cancellationToken);
            return new FetchResult(xml, "ok", System.Text.Encoding.UTF8.GetByteCount(xml), null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FetchResult(null, "fail", 0, null, null, $"File read failed: {ex.Message}");
        }
    }

    private async Task<FetchResult> FetchProviderXmltvAsync(
        EpgSource source,
        Provider? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null)
            return new FetchResult(null, "fail", 0, null, null, "Linked provider not found.");

        try
        {
            var xmltvResult = await providerFetcher.FetchXmltvAsync(provider, cancellationToken);
            if (xmltvResult.Bytes == 0)
                return new FetchResult(null, "fail", 0, null, null, "Provider has no XMLTV configured.");

            return new FetchResult(xmltvResult.Xml, "ok", xmltvResult.Bytes, null, null, null);
        }
        catch (ProviderFetchException ex)
        {
            return new FetchResult(null, "fail", 0, null, null, ex.Message);
        }
    }

    private async Task<FetchResult> FetchHttpAsync(EpgSource source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.UrlOrPath))
            return new FetchResult(null, "fail", 0, null, null, "No URL configured.");

        // Substitute %VAR_NAME% placeholders with values from .env / environment
        string resolvedUrl;
        try
        {
            resolvedUrl = envVarService.SubstituteEnvVars(source.UrlOrPath);
        }
        catch (InvalidOperationException ex)
        {
            return new FetchResult(null, "fail", 0, null, null,
                $"URL contains undefined variable: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds > 0 ? source.TimeoutSeconds : 30));

        try
        {
            // "epg" client has AutomaticDecompression (gzip/deflate/brotli) configured
            using var client = httpClientFactory.CreateClient("epg");
            ProviderFetcher.ApplyHeadersFromJson(client, source.HeadersJson);

            if (!string.IsNullOrWhiteSpace(source.UserAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(source.UserAgent);

            // Conditional request headers
            if (!string.IsNullOrWhiteSpace(source.ETag))
                client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", source.ETag);
            else if (source.LastModifiedUtc.HasValue)
                client.DefaultRequestHeaders.IfModifiedSince = source.LastModifiedUtc.Value;

            using var response = await client.GetAsync(resolvedUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return new FetchResult(null, "not_modified", 0, source.ETag, source.LastModifiedUtc, null);

            response.EnsureSuccessStatusCode();

            var xml = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(xml);

            var newEtag = response.Headers.ETag?.Tag;
            var newLastModified = response.Content.Headers.LastModified?.UtcDateTime;

            return new FetchResult(xml, "ok", bytes, newEtag, newLastModified, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                return new FetchResult(null, "fail", 0, null, null, $"XMLTV fetch timed out after {source.TimeoutSeconds}s.");

            return new FetchResult(null, "fail", 0, null, null, $"HTTP fetch failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Cache helpers
    // -------------------------------------------------------------------------

    private async Task WriteCacheAsync(string path, string xml, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, xml, System.Text.Encoding.UTF8, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write EPG cache file {CachePath}.", path);
        }
    }

    private static async Task<string?> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch
        {
            return null;
        }
    }
}
