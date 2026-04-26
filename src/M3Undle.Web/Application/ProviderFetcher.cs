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

        var effectivePlaylistUrl = ResolvePlaylistUrl(provider);
        string content;

        if (effectivePlaylistUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(effectivePlaylistUrl).LocalPath;
            try
            {
                content = await File.ReadAllTextAsync(localPath, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProviderFetchException($"Local file read failed: {ex.Message}", ex);
            }
        }
        else
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));

            try
            {
                using var client = httpClientFactory.CreateClient();
                if (provider.XtreamBaseUrl is null)
                {
                    ApplyHeadersFromJson(client, provider.HeadersJson);
                    if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                        client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
                }

                var resolvedUrl = provider.XtreamBaseUrl is null
                    ? SubstituteProviderUrl(effectivePlaylistUrl)
                    : effectivePlaylistUrl;
                content = await client.GetStringAsync(resolvedUrl, timeoutCts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new ProviderFetchException($"Playlist fetch timed out after {provider.TimeoutSeconds}s.", ex);
                throw new ProviderFetchException($"Playlist fetch failed: {ex.Message}", ex);
            }
        }

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
            Bytes: System.Text.Encoding.UTF8.GetByteCount(content));
    }

    public async Task<XmltvFetchResult> FetchXmltvAsync(Provider provider, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });

        var effectiveXmltvUrl = ResolveXmltvUrl(provider);
        if (string.IsNullOrWhiteSpace(effectiveXmltvUrl))
        {
            return new XmltvFetchResult(Xml: EmptyXmltvDocument, Bytes: 0);
        }

        logger.LogDebug("Fetching XMLTV for provider {ProviderId}.", provider.ProviderId);

        if (effectiveXmltvUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(effectiveXmltvUrl).LocalPath;
            try
            {
                var xml = await File.ReadAllTextAsync(localPath, cancellationToken);
                return new XmltvFetchResult(Xml: xml, Bytes: System.Text.Encoding.UTF8.GetByteCount(xml));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProviderFetchException($"Local XMLTV file read failed: {ex.Message}", ex);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));

        try
        {
            using var client = httpClientFactory.CreateClient();
            if (provider.XtreamBaseUrl is null)
            {
                ApplyHeadersFromJson(client, provider.HeadersJson);
                if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
            }

            var resolvedUrl = provider.XtreamBaseUrl is null
                ? SubstituteProviderUrl(effectiveXmltvUrl)
                : effectiveXmltvUrl;
            var xml = await client.GetStringAsync(resolvedUrl, timeoutCts.Token);
            return new XmltvFetchResult(Xml: xml, Bytes: System.Text.Encoding.UTF8.GetByteCount(xml));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new ProviderFetchException($"XMLTV fetch timed out after {provider.TimeoutSeconds}s.", ex);
            throw new ProviderFetchException($"XMLTV fetch failed: {ex.Message}", ex);
        }
    }

    // Probes player_api.php for an M3U provider whose URL embeds credentials.
    // Returns null when the endpoint is unreachable, auth fails, or the URL has no embedded credentials.
    // Never throws — all failures are swallowed and logged at Debug level.
    public async Task<XtreamAccountInfo?> TryProbeXtreamAsync(Provider provider, CancellationToken cancellationToken)
    {
        if (!XtreamProviderUrls.TryExtractCredentials(provider.PlaylistUrl, out var baseUrl, out var username, out var password))
            return null;

        var probeUrl = XtreamProviderUrls.BuildPlayerApiUrl(baseUrl, username, password);
        var timeoutSeconds = Math.Clamp(provider.TimeoutSeconds, 1, 10);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var client = httpClientFactory.CreateClient();
            var json = await client.GetStringAsync(probeUrl, timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("user_info", out var userInfo))
                return null;
            if (!userInfo.TryGetProperty("auth", out var auth) || auth.GetInt32() != 1)
                return null;

            DateTime? expiresUtc = null;
            if (userInfo.TryGetProperty("exp_date", out var expEl)
                && expEl.ValueKind == JsonValueKind.String
                && long.TryParse(expEl.GetString(), out var expUnix))
            {
                expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
            }

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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
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

    private string ResolvePlaylistUrl(Provider provider)
    {
        if (provider.XtreamBaseUrl is null)
            return provider.PlaylistUrl;

        if (string.IsNullOrWhiteSpace(provider.XtreamEncryptedPassword))
            throw new ProviderFetchException("Xtream Codes provider has no stored password. Update the password in provider settings.");

        string password;
        try
        {
            password = secretEncryption.Decrypt(provider.XtreamEncryptedPassword);
        }
        catch (InvalidOperationException ex)
        {
            throw new ProviderFetchException($"Failed to decrypt Xtream password: {ex.Message}", ex);
        }

        return XtreamProviderUrls.BuildPlaylistUrl(provider.XtreamBaseUrl, provider.XtreamUsername, password);
    }

    private string? ResolveXmltvUrl(Provider provider)
    {
        if (provider.XtreamBaseUrl is null)
            return provider.XmltvUrl;

        if (!provider.XtreamIncludeXmltv)
            return null;

        if (string.IsNullOrWhiteSpace(provider.XtreamEncryptedPassword))
            return null;

        string password;
        try
        {
            password = secretEncryption.Decrypt(provider.XtreamEncryptedPassword);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return XtreamProviderUrls.BuildXmltvUrl(provider.XtreamBaseUrl, provider.XtreamUsername, password);
    }

    private string SubstituteProviderUrl(string url)
    {
        try
        {
            return envVarService.SubstituteEnvVars(url);
        }
        catch (InvalidOperationException ex)
        {
            throw new ProviderFetchException(
                $"Provider URL contains undefined environment variables: {ex.Message}", ex);
        }
    }

    internal static string? NormalizeProviderChannelKey(string? value)
        => ProviderChannelNormalizer.NormalizeProviderChannelKey(value);
}

// -------------------------------------------------------------------------
// Result types
// -------------------------------------------------------------------------

public sealed record PlaylistFetchResult(
    IReadOnlyList<ParsedProviderChannel> Channels,
    long Bytes);

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
}

// -------------------------------------------------------------------------
// Exceptions
// -------------------------------------------------------------------------

public sealed class ProviderFetchException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class ProviderParseException(string message, Exception? inner = null)
    : Exception(message, inner);
