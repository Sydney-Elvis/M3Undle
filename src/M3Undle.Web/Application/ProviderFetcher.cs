using System.Text.RegularExpressions;
using M3Undle.Core.M3u;
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
    ILogger<ProviderFetcher> logger)
{
    private static readonly Regex MetadataAttributeRegex =
        new("(?<key>[A-Za-z0-9\\-]+)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);

    private static readonly string EmptyXmltvDocument =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv generator-info-name=\"M3Undle\"></tv>";

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public async Task<PlaylistFetchResult> FetchPlaylistAsync(Provider provider, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });
        logger.LogDebug("Fetching playlist for provider {ProviderId}.", provider.ProviderId);

        string content;

        if (provider.PlaylistUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(provider.PlaylistUrl).LocalPath;
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
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));

                using var client = httpClientFactory.CreateClient();
                ApplyHeadersFromJson(client, provider.HeadersJson);
                if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
                }

                var playlistUrl = SubstituteProviderUrl(provider.PlaylistUrl);
                content = await client.GetStringAsync(playlistUrl, timeoutCts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
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

        if (string.IsNullOrWhiteSpace(provider.XmltvUrl))
        {
            return new XmltvFetchResult(Xml: EmptyXmltvDocument, Bytes: 0);
        }

        logger.LogDebug("Fetching XMLTV for provider {ProviderId}.", provider.ProviderId);

        if (provider.XmltvUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(provider.XmltvUrl).LocalPath;
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

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));

            using var client = httpClientFactory.CreateClient();
            ApplyHeadersFromJson(client, provider.HeadersJson);
            if (!string.IsNullOrWhiteSpace(provider.UserAgent))
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
            }

            var xmltvUrl = SubstituteProviderUrl(provider.XmltvUrl);
            var xml = await client.GetStringAsync(xmltvUrl, timeoutCts.Token);
            return new XmltvFetchResult(Xml: xml, Bytes: System.Text.Encoding.UTF8.GetByteCount(xml));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // XMLTV failure is non-fatal — caller logs and falls back to empty guide
            throw new ProviderFetchException($"XMLTV fetch failed: {ex.Message}", ex);
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers (also used by ProviderApiEndpoints via internal access)
    // -------------------------------------------------------------------------

    internal static ParsedProviderChannel ParseEntry(M3uEntry entry)
    {
        var metadata = entry.MetadataLines.FirstOrDefault() ?? string.Empty;
        var attributes = MetadataAttributeRegex.Matches(metadata)
            .Select(match => (Key: match.Groups["key"].Value, Value: match.Groups["value"].Value))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);

        attributes.TryGetValue("tvg-id", out var tvgId);
        attributes.TryGetValue("tvg-name", out var tvgName);
        attributes.TryGetValue("tvg-logo", out var logoUrl);
        attributes.TryGetValue("group-title", out var groupTitleAttr);

        var groupTitle = !string.IsNullOrWhiteSpace(entry.Group)
            ? entry.Group!.Trim()
            : string.IsNullOrWhiteSpace(groupTitleAttr) ? null : groupTitleAttr.Trim();

        var providerChannelKey = NormalizeProviderChannelKey(tvgId);
        var displayName = string.IsNullOrWhiteSpace(entry.Title)
            ? (string.IsNullOrWhiteSpace(tvgName) ? "Unnamed Channel" : tvgName.Trim())
            : entry.Title.Trim();

        return new ParsedProviderChannel
        {
            ProviderChannelKey = providerChannelKey,
            DisplayName = displayName,
            TvgId = string.IsNullOrWhiteSpace(tvgId) ? null : tvgId.Trim(),
            TvgName = string.IsNullOrWhiteSpace(tvgName) ? null : tvgName.Trim(),
            LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim(),
            StreamUrl = NormalizeStreamUrl(entry.Url!.Trim()),
            GroupTitle = groupTitle,
        };
    }

    internal static string NormalizeStreamUrl(string url)
    {
        // Some providers emit https:// URLs on port 80 (the plain HTTP port).
        // .NET HttpClient will attempt TLS on port 80 and fail immediately.
        // Downgrade the scheme to http so the relay can connect.
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Port == 80)
        {
            return string.Concat("http://", url.AsSpan("https://".Length));
        }

        return url;
    }

    internal static void ApplyHeadersFromJson(HttpClient client, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return;
        }

        using var document = System.Text.Json.JsonDocument.Parse(headersJson);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            client.DefaultRequestHeaders.Remove(property.Name);
            client.DefaultRequestHeaders.TryAddWithoutValidation(property.Name, value);
        }
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
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

