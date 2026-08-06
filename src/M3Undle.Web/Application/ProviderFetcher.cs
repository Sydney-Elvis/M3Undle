using System.Text;
using System.Text.Json;
using M3Undle.Core.M3u;
using M3Undle.Core.Providers;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application;

/// <summary>
/// Singleton service that fetches and parses provider playlists and XMLTV guides.
/// Stateless — safe to use from background services.
/// </summary>
public sealed class ProviderFetcher(
    IHttpClientFactory httpClientFactory,
    PlaylistParser playlistParser,
    EnvironmentVariableService envVarService,
    SecretEncryptionService secretEncryption,
    XtreamLineupClient xtreamLineupClient,
    RefreshActivityTracker activityTracker,
    ILogger<ProviderFetcher> logger)
{
    private static readonly string EmptyXmltvDocument =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv generator-info-name=\"M3Undle\"></tv>";

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public async Task<PlaylistFetchResult> FetchPlaylistAsync(Provider provider, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });
        logger.LogDebug("Fetching playlist for provider {ProviderId}.", provider.ProviderId);

        // Xtream-mode: use player_api.php JSON assembly instead of get.php monolithic M3U.
        if (provider.XtreamBaseUrl is not null)
            return await xtreamLineupClient.BuildLineupAsync(provider, cancellationToken);

        var effectivePlaylistUrl = SubstituteProviderUrl(provider.PlaylistUrl);

        if (effectivePlaylistUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(effectivePlaylistUrl).LocalPath;
            try
            {
                var content = await File.ReadAllTextAsync(localPath, cancellationToken);
                return ParseM3uContent(content, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProviderFetchException($"Local file read failed: {ex.Message}", ex);
            }
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            ApplyHeadersFromJson(client, provider.HeadersJson);
            if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);

            string content;
            try
            {
                content = await HttpFetchHelper.FetchStringAsync(client, effectivePlaylistUrl, provider.TimeoutSeconds, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } code && (int)code >= 500)
            {
                // If the failing URL is an Xtream get.php URL, fall back to player_api.php assembly.
                if (XtreamProviderUrls.TryExtractCredentials(effectivePlaylistUrl, out var baseUrl, out var username, out var password))
                {
                    logger.LogWarning(
                        "URL-mode get.php returned {StatusCode} for provider {ProviderId} — falling back to player_api.php assembly.",
                        (int)code, provider.ProviderId);
                    return await xtreamLineupClient.BuildLineupFromCredentialsAsync(provider, baseUrl, username, password, cancellationToken);
                }
                throw;
            }

            activityTracker.Clear();
            return ParseM3uContent(content, cancellationToken);
        }
        catch (ProviderFetchException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderFetchException($"Playlist fetch failed: {ex.Message}", ex);
        }
    }

    public async Task<XmltvFetchResult> FetchXmltvAsync(Provider provider, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });

        var effectiveXmltvUrl = ResolveXmltvUrl(provider);
        if (string.IsNullOrWhiteSpace(effectiveXmltvUrl))
            return new XmltvFetchResult(Xml: EmptyXmltvDocument, Bytes: 0);

        logger.LogDebug("Fetching XMLTV for provider {ProviderId}.", provider.ProviderId);

        if (effectiveXmltvUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(effectiveXmltvUrl).LocalPath;
            try
            {
                var xml = await File.ReadAllTextAsync(localPath, cancellationToken);
                return new XmltvFetchResult(Xml: xml, Bytes: Encoding.UTF8.GetByteCount(xml));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProviderFetchException($"Local XMLTV file read failed: {ex.Message}", ex);
            }
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            if (provider.XtreamBaseUrl is null)
            {
                ApplyHeadersFromJson(client, provider.HeadersJson);
                if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
            }

            var xml = await HttpFetchHelper.FetchStringAsync(client, effectiveXmltvUrl, provider.TimeoutSeconds, cancellationToken);
            return new XmltvFetchResult(Xml: xml, Bytes: Encoding.UTF8.GetByteCount(xml));
        }
        catch (ProviderFetchException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProviderFetchException($"XMLTV fetch failed: {ex.Message}", ex);
        }
    }

    // Probes player_api.php for Xtream account metadata.
    // Returns null when the endpoint is unreachable, auth fails, or credentials cannot be resolved.
    // Never throws — all failures are swallowed and logged at Debug level.
    public async Task<XtreamAccountInfo?> TryProbeXtreamAsync(Provider provider, CancellationToken cancellationToken)
    {
        if (!TryResolveXtreamProbeCredentials(provider, out var baseUrl, out var username, out var password))
            return null;

        var probeUrl = XtreamProviderUrls.BuildPlayerApiUrl(baseUrl, username, password);
        var timeoutSeconds = Math.Clamp(provider.TimeoutSeconds, 1, 10);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var client = httpClientFactory.CreateClient();
            if (provider.XtreamBaseUrl is null)
            {
                ApplyHeadersFromJson(client, provider.HeadersJson);
                if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
            }

            var json = await client.GetStringAsync(probeUrl, timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("user_info", out var userInfo))
                return null;
            if (!userInfo.TryGetProperty("auth", out var auth) || auth.GetInt32() != 1)
                return null;

            DateTime? expiresUtc = null;
            if (TryGetUnixSeconds(userInfo, "exp_date", out var expUnix))
                expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;

            string? status = null;
            if (userInfo.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                status = statusEl.GetString();

            int? maxConnections = null;
            if (userInfo.TryGetProperty("max_connections", out var maxEl))
            {
                if (maxEl.ValueKind == JsonValueKind.Number && maxEl.TryGetInt32(out var mc))
                    maxConnections = mc;
                else if (maxEl.ValueKind == JsonValueKind.String && int.TryParse(maxEl.GetString(), out var mcs))
                    maxConnections = mcs;
            }

            return new XtreamAccountInfo(expiresUtc, status, maxConnections);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            logger.LogDebug("Xtream capability probe failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers (also used by ProviderApiEndpoints via internal access)
    // -------------------------------------------------------------------------

    internal static ParsedProviderChannel ParseEntry(M3uEntry entry)
    {
        var channel = ProviderChannelNormalizer.ParseEntry(entry);

        return new ParsedProviderChannel
        {
            ProviderChannelKey = channel.ProviderChannelKey,
            DisplayName = channel.DisplayName,
            TvgId = channel.TvgId,
            TvgName = channel.TvgName,
            LogoUrl = channel.LogoUrl,
            StreamUrl = channel.StreamUrl,
            GroupTitle = channel.GroupTitle,
        };
    }

    internal static string NormalizeStreamUrl(string url)
        => ProviderChannelNormalizer.NormalizeStreamUrl(url);

    internal static void ApplyHeadersFromJson(HttpClient client, string? headersJson)
        => ProviderRequestHeaders.ApplyTo(client, headersJson);

    internal static string? NormalizeProviderChannelKey(string? value)
        => ProviderChannelNormalizer.NormalizeProviderChannelKey(value);

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private PlaylistFetchResult ParseM3uContent(string content, CancellationToken cancellationToken)
    {
        List<ParsedProviderChannel> channels;
        try
        {
            var document = playlistParser.Parse(content, cancellationToken);
            channels = document.Entries
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .Select(ParseEntry)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProviderParseException($"Playlist parse failed: {ex.Message}", ex);
        }

        return new PlaylistFetchResult(
            Channels: channels,
            Bytes: Encoding.UTF8.GetByteCount(content));
    }

    private bool TryResolveXtreamProbeCredentials(
        Provider provider,
        out string baseUrl,
        out string username,
        out string password)
    {
        baseUrl = username = password = string.Empty;

        if (!string.IsNullOrWhiteSpace(provider.XtreamBaseUrl))
        {
            if (string.IsNullOrWhiteSpace(provider.XtreamEncryptedPassword))
                return false;

            try
            {
                password = secretEncryption.Decrypt(provider.XtreamEncryptedPassword);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogDebug(
                    "Xtream capability probe skipped for provider {ProviderId}: password decrypt failed: {Message}",
                    provider.ProviderId, ex.Message);
                return false;
            }

            baseUrl = provider.XtreamBaseUrl.TrimEnd('/');
            username = provider.XtreamUsername?.Trim() ?? string.Empty;
            return true;
        }

        string playlistUrl;
        try { playlistUrl = SubstituteProviderUrl(provider.PlaylistUrl); }
        catch (ProviderFetchException ex)
        {
            logger.LogDebug(
                "Xtream capability probe skipped for provider {ProviderId}: {Message}",
                provider.ProviderId, ex.Message);
            return false;
        }

        return XtreamProviderUrls.TryExtractCredentials(playlistUrl, out baseUrl, out username, out password);
    }

    private string? ResolveXmltvUrl(Provider provider)
    {
        if (provider.XtreamBaseUrl is null)
            return string.IsNullOrWhiteSpace(provider.XmltvUrl)
                ? provider.XmltvUrl
                : SubstituteProviderUrl(provider.XmltvUrl);

        if (!provider.XtreamIncludeXmltv)
            return null;

        if (string.IsNullOrWhiteSpace(provider.XtreamEncryptedPassword))
            return null;

        string password;
        try { password = secretEncryption.Decrypt(provider.XtreamEncryptedPassword); }
        catch (InvalidOperationException) { return null; }

        return XtreamProviderUrls.BuildXmltvUrl(provider.XtreamBaseUrl, provider.XtreamUsername, password);
    }

    private string SubstituteProviderUrl(string url)
    {
        try { return envVarService.SubstituteEnvVars(url); }
        catch (InvalidOperationException ex)
        {
            throw new ProviderFetchException(
                $"Provider URL contains undefined environment variables: {ex.Message}", ex);
        }
    }

    private static bool TryGetUnixSeconds(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetInt64(out value);

        return property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), out value);
    }
}

// -------------------------------------------------------------------------
// Result types
// -------------------------------------------------------------------------

public sealed record PlaylistFetchResult(
    IReadOnlyList<ParsedProviderChannel> Channels,
    long Bytes,
    XtreamAccountInfo? AccountInfo = null,
    IReadOnlyList<ParsedCatalogItem>? CatalogItems = null);

public sealed record XmltvFetchResult(
    string Xml,
    long Bytes);

public sealed record XtreamAccountInfo(
    DateTime? ExpiresUtc,
    string? Status,
    int? MaxConnections);

// -------------------------------------------------------------------------
// Channel record (replaces private ParsedChannel in ProviderApiEndpoints)
// -------------------------------------------------------------------------

public sealed class ParsedProviderChannel
{
    public string? ProviderChannelKey { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? TvgId { get; init; }
    public string? TvgName { get; init; }
    public string? LogoUrl { get; init; }
    public string StreamUrl { get; init; } = string.Empty;
    public string? GroupTitle { get; init; }
    public string? CatalogItemId { get; init; }
    public string? CatalogTitle { get; init; }
}

public sealed class ParsedCatalogItem
{
    public string ProviderItemId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string? GroupTitle { get; init; }
    public string? ArtworkUrl { get; init; }
}

// -------------------------------------------------------------------------
// Exceptions
// -------------------------------------------------------------------------

public sealed class ProviderFetchException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class ProviderParseException(string message, Exception? inner = null)
    : Exception(message, inner);
