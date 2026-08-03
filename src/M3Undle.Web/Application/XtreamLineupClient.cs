using System.Text.Json;
using M3Undle.Core.Providers;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

/// <summary>
/// Singleton service that builds a provider lineup from the Xtream Codes player_api.php
/// instead of fetching the monolithic get.php M3U playlist.
/// </summary>
public sealed class XtreamLineupClient(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    SecretEncryptionService secretEncryption,
    RefreshActivityTracker activityTracker,
    IXtreamSeriesExpansionQueue seriesExpansionQueue,
    ILogger<XtreamLineupClient> logger)
{
    // Called for Xtream-mode providers (XtreamBaseUrl is set).
    public Task<PlaylistFetchResult> BuildLineupAsync(Provider provider, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.XtreamEncryptedPassword))
            throw new ProviderFetchException("Xtream Codes provider has no stored password.");

        string password;
        try { password = secretEncryption.Decrypt(provider.XtreamEncryptedPassword); }
        catch (InvalidOperationException ex) { throw new ProviderFetchException($"Failed to decrypt Xtream password: {ex.Message}", ex); }

        var baseUrl = provider.XtreamBaseUrl!.TrimEnd('/');
        var username = provider.XtreamUsername?.Trim() ?? string.Empty;
        return BuildLineupCoreAsync(provider, baseUrl, username, password, cancellationToken);
    }

    // Called when a URL-mode provider's get.php fails and we fall back to API assembly.
    public Task<PlaylistFetchResult> BuildLineupFromCredentialsAsync(
        Provider provider,
        string baseUrl, string username, string password,
        CancellationToken cancellationToken)
        => BuildLineupCoreAsync(provider, baseUrl, username, password, cancellationToken);

    private async Task<PlaylistFetchResult> BuildLineupCoreAsync(
        Provider provider,
        string baseUrl, string username, string password,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        // Headers/UA apply to URL-mode providers only; Xtream API calls don't need them.
        if (provider.XtreamBaseUrl is null)
        {
            ProviderFetcher.ApplyHeadersFromJson(client, provider.HeadersJson);
            if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
        }

        try
        {
            long totalBytes = 0;
            var channels = new List<ParsedProviderChannel>();

            // Authenticate and get expiry info.
            activityTracker.Set("Connecting to provider…");
            var (authBytes, accountInfo) = await FetchAccountInfoAsync(
                client, baseUrl, username, password, provider.TimeoutSeconds, cancellationToken);
            totalBytes += authBytes;

            // Live — always fetched.
            activityTracker.Set("Fetching live channels…");
            var (liveBytes, liveChannels) = await FetchLiveAsync(
                client, baseUrl, username, password, provider.TimeoutSeconds, cancellationToken);
            channels.AddRange(liveChannels);
            totalBytes += liveBytes;

            // VOD — only if opted in.
            if (provider.IncludeVod)
            {
                activityTracker.Set("Fetching VOD…");
                var (vodBytes, vodChannels) = await FetchVodAsync(
                    client, baseUrl, username, password, provider.TimeoutSeconds, cancellationToken);
                channels.AddRange(vodChannels);
                totalBytes += vodBytes;
            }

            // Series — only if opted in.
            if (provider.IncludeSeries)
            {
                activityTracker.Set("Fetching series…");
                var (seriesBytes, seriesChannels) = await FetchSeriesAsync(
                    client, baseUrl, username, password, provider,
                    provider.TimeoutSeconds, cancellationToken);
                channels.AddRange(seriesChannels);
                totalBytes += seriesBytes;
            }

            return new PlaylistFetchResult(channels, totalBytes, accountInfo);
        }
        finally
        {
            activityTracker.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Per-content-type fetchers
    // -------------------------------------------------------------------------

    private async Task<(long Bytes, XtreamAccountInfo? Info)> FetchAccountInfoAsync(
        HttpClient client, string baseUrl, string username, string password,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}";
        try
        {
            var json = await HttpFetchHelper.FetchStringAsync(client, url, timeoutSeconds, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var info = ParseAccountInfo(doc.RootElement);
            return (System.Text.Encoding.UTF8.GetByteCount(json), info);
        }
        catch (Exception ex) when (ex is HttpRequestException or ProviderFetchException)
        {
            // Auth check failure is non-fatal for lineup building — log and continue.
            logger.LogDebug("Xtream auth probe failed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return (0, null);
        }
        catch (JsonException ex)
        {
            logger.LogDebug("Xtream auth response malformed for {BaseUrl}: {Message}", baseUrl, ex.Message);
            return (0, null);
        }
    }

    private async Task<(long Bytes, List<ParsedProviderChannel> Channels)> FetchLiveAsync(
        HttpClient client, string baseUrl, string username, string password,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var catUrl = BuildActionUrl(baseUrl, username, password, "get_live_categories");
        var streamUrl = BuildActionUrl(baseUrl, username, password, "get_live_streams");

        var (catJson, streamJson) = await FetchPairAsync(client, catUrl, streamUrl, timeoutSeconds, cancellationToken);

        long bytes = System.Text.Encoding.UTF8.GetByteCount(catJson) + System.Text.Encoding.UTF8.GetByteCount(streamJson);
        var categories = ParseCategories(catJson);
        var channels = ParseLiveStreams(streamJson, categories, baseUrl, username, password);
        return (bytes, channels);
    }

    private async Task<(long Bytes, List<ParsedProviderChannel> Channels)> FetchVodAsync(
        HttpClient client, string baseUrl, string username, string password,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var catUrl = BuildActionUrl(baseUrl, username, password, "get_vod_categories");
        var streamUrl = BuildActionUrl(baseUrl, username, password, "get_vod_streams");

        var (catJson, streamJson) = await FetchPairAsync(client, catUrl, streamUrl, timeoutSeconds, cancellationToken);

        long bytes = System.Text.Encoding.UTF8.GetByteCount(catJson) + System.Text.Encoding.UTF8.GetByteCount(streamJson);
        var categories = ParseCategories(catJson);
        var channels = ParseVodStreams(streamJson, categories, baseUrl, username, password);
        return (bytes, channels);
    }

    private async Task<(long Bytes, List<ParsedProviderChannel> Channels)> FetchSeriesAsync(
        HttpClient client, string baseUrl, string username, string password,
        Provider provider, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var providerId = provider.ProviderId;
        var catUrl = BuildActionUrl(baseUrl, username, password, "get_series_categories");
        var listUrl = BuildActionUrl(baseUrl, username, password, "get_series");

        var (catJson, listJson) = await FetchPairAsync(client, catUrl, listUrl, timeoutSeconds, cancellationToken);
        long bytes = System.Text.Encoding.UTF8.GetByteCount(catJson) + System.Text.Encoding.UTF8.GetByteCount(listJson);

        var categories = ParseCategories(catJson);
        var seriesList = ParseSeriesList(listJson);
        if (seriesList.Count == 0)
            return (bytes, []);

        // Load existing cache.
        var cache = await LoadSeriesCacheAsync(providerId, cancellationToken);

        // New/changed series: expand inline within a time budget so fast providers load
        // completely on the first sync; whatever doesn't fit moves to the background worker.
        // Changed-but-cached series keep publishing their old episodes until re-expanded.
        var toExpand = seriesList
            .Where(s => !cache.TryGetValue(s.SeriesId, out var cached) || cached.LastModifiedEpoch != s.LastModifiedEpoch)
            .Select(s => new XtreamSeriesStub(s.SeriesId, s.LastModifiedEpoch))
            .ToList();

        if (toExpand.Count > 0)
        {
            var priority = await GetExpansionPriorityAsync(providerId, cancellationToken);
            var job = new XtreamSeriesExpansionJob(
                providerId, provider.Name, baseUrl, username, password, timeoutSeconds, toExpand, priority);

            var budget = TimeSpan.FromSeconds(Math.Clamp(provider.TimeoutSeconds, 30, 180));
            activityTracker.Set($"Loading series ({toExpand.Count:N0} to fetch)…");
            var inlineResults = await seriesExpansionQueue.TryExpandInlineAsync(job, budget, cancellationToken);

            foreach (var item in inlineResults)
            {
                bytes += System.Text.Encoding.UTF8.GetByteCount(item.EpisodesJson);
                cache[item.SeriesId] = new XtreamSeriesCache
                {
                    ProviderId = providerId,
                    SeriesId = item.SeriesId,
                    LastModifiedEpoch = item.LastModifiedEpoch,
                    EpisodesJson = item.EpisodesJson,
                };
            }
        }

        // Remove cache entries for series that no longer exist.
        var activeSeriesIds = seriesList.Select(s => s.SeriesId).ToHashSet();
        var staleIds = cache.Keys.Except(activeSeriesIds).ToList();
        if (staleIds.Count > 0)
            await DeleteStaleCacheAsync(providerId, staleIds, cancellationToken);

        var channels = BuildSeriesChannels(seriesList, cache, categories, baseUrl, username, password);
        return (bytes, channels);
    }

    // -------------------------------------------------------------------------
    // Channel builders
    // -------------------------------------------------------------------------

    private static List<ParsedProviderChannel> ParseLiveStreams(
        string json, Dictionary<int, string> categories,
        string baseUrl, string username, string password)
    {
        var channels = new List<ParsedProviderChannel>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return channels;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var streamId = ReadInt(el, "stream_id");
                if (streamId <= 0) continue;

                var name = ReadString(el, "name") ?? string.Empty;
                var icon = ReadString(el, "stream_icon");
                var epgId = ReadString(el, "epg_channel_id");
                var streamUrl = $"{baseUrl}/live/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{streamId}.ts";

                foreach (var (groupTitle, catId) in ResolveCategories(el, categories))
                {
                    channels.Add(new ParsedProviderChannel
                    {
                        ProviderChannelKey = ProviderFetcher.NormalizeProviderChannelKey(epgId),
                        DisplayName = name,
                        TvgId = string.IsNullOrWhiteSpace(epgId) ? null : epgId,
                        TvgName = name,
                        LogoUrl = icon,
                        StreamUrl = streamUrl,
                        GroupTitle = groupTitle,
                    });
                }
            }
        }
        catch (JsonException) { }
        return channels;
    }

    private static List<ParsedProviderChannel> ParseVodStreams(
        string json, Dictionary<int, string> categories,
        string baseUrl, string username, string password)
    {
        var channels = new List<ParsedProviderChannel>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return channels;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var streamId = ReadInt(el, "stream_id");
                if (streamId <= 0) continue;

                var name = ReadString(el, "name") ?? string.Empty;
                var icon = ReadString(el, "stream_icon");
                var ext = ReadString(el, "container_extension") ?? "mkv";
                var streamUrl = $"{baseUrl}/movie/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{streamId}.{ext}";

                foreach (var (groupTitle, _) in ResolveCategories(el, categories))
                {
                    channels.Add(new ParsedProviderChannel
                    {
                        DisplayName = name,
                        TvgName = name,
                        LogoUrl = icon,
                        StreamUrl = streamUrl,
                        GroupTitle = groupTitle,
                        CatalogItemId = streamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        CatalogTitle = name,
                    });
                }
            }
        }
        catch (JsonException) { }
        return channels;
    }

    private static List<ParsedProviderChannel> BuildSeriesChannels(
        List<(int SeriesId, string Name, string? Cover, int CategoryId, long LastModifiedEpoch)> seriesList,
        Dictionary<int, XtreamSeriesCache> cache,
        Dictionary<int, string> categories,
        string baseUrl, string username, string password)
    {
        var channels = new List<ParsedProviderChannel>();

        foreach (var series in seriesList)
        {
            if (!cache.TryGetValue(series.SeriesId, out var cached) || string.IsNullOrEmpty(cached.EpisodesJson))
                continue;

            var groupTitle = categories.GetValueOrDefault(series.CategoryId);

            try
            {
                using var doc = JsonDocument.Parse(cached.EpisodesJson);
                if (!doc.RootElement.TryGetProperty("episodes", out var episodesObj)
                    || episodesObj.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var season in episodesObj.EnumerateObject())
                {
                    if (season.Value.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var ep in season.Value.EnumerateArray())
                    {
                        var epId = ReadInt(ep, "id");
                        if (epId <= 0) continue;

                        var epTitle = ReadString(ep, "title") ?? string.Empty;
                        var ext = ReadString(ep, "container_extension") ?? "mkv";
                        var epNum = ReadInt(ep, "episode_num");

                        var episodeMarker = $"S{season.Name.PadLeft(2, '0')}E{epNum:D2}";
                        var displayName = string.IsNullOrWhiteSpace(epTitle)
                            ? $"{series.Name} {episodeMarker}"
                            : $"{series.Name} {episodeMarker} — {epTitle}";

                        var streamUrl = $"{baseUrl}/series/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{epId}.{ext}";

                        channels.Add(new ParsedProviderChannel
                        {
                            DisplayName = displayName,
                            TvgName = series.Name,
                            LogoUrl = series.Cover,
                            StreamUrl = streamUrl,
                            GroupTitle = groupTitle,
                            CatalogItemId = series.SeriesId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            CatalogTitle = series.Name,
                        });
                    }
                }
            }
            catch (JsonException) { }
        }

        return channels;
    }

    // -------------------------------------------------------------------------
    // JSON parsing helpers
    // -------------------------------------------------------------------------

    private static Dictionary<int, string> ParseCategories(string json)
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return map;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = ReadInt(el, "category_id");
                var name = ReadString(el, "category_name");
                if (id > 0 && name is not null)
                    map[id] = name;
            }
        }
        catch (JsonException) { }
        return map;
    }

    private static List<(int SeriesId, string Name, string? Cover, int CategoryId, long LastModifiedEpoch)> ParseSeriesList(string json)
    {
        var list = new List<(int, string, string?, int, long)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = ReadInt(el, "series_id");
                if (id <= 0) continue;
                var name = ReadString(el, "name") ?? string.Empty;
                var cover = ReadString(el, "cover");
                var catId = ReadInt(el, "category_id");
                var lastMod = ReadLong(el, "last_modified");
                list.Add((id, name, cover, catId, lastMod));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private static XtreamAccountInfo? ParseAccountInfo(JsonElement root)
    {
        if (!root.TryGetProperty("user_info", out var userInfo))
            return null;
        if (!userInfo.TryGetProperty("auth", out var auth) || ReadInt(auth) != 1)
            return null;

        DateTime? expiresUtc = null;
        if (userInfo.TryGetProperty("exp_date", out var expEl))
        {
            var expVal = ReadLong(expEl);
            if (expVal > 0)
                expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expVal).UtcDateTime;
        }

        string? status = null;
        if (userInfo.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            status = statusEl.GetString();

        int? maxConnections = null;
        if (userInfo.TryGetProperty("max_connections", out var maxEl))
            maxConnections = ReadInt(maxEl) is > 0 and var mc ? mc : null;

        return new XtreamAccountInfo(expiresUtc, status, maxConnections);
    }

    // Returns (groupTitle, categoryId) pairs — multiple entries if category_ids array present.
    private static IEnumerable<(string GroupTitle, int CategoryId)> ResolveCategories(
        JsonElement el, Dictionary<int, string> categories)
    {
        // Try category_ids array first (some newer panels).
        if (el.TryGetProperty("category_ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
        {
            var yielded = false;
            foreach (var idEl in idsEl.EnumerateArray())
            {
                var id = ReadInt(idEl);
                if (id > 0)
                {
                    yielded = true;
                    yield return (categories.GetValueOrDefault(id, string.Empty), id);
                }
            }
            if (yielded) yield break;
        }

        // Fall back to single category_id.
        var catId = ReadInt(el, "category_id");
        yield return (catId > 0 ? categories.GetValueOrDefault(catId, string.Empty) : string.Empty, catId);
    }

    // -------------------------------------------------------------------------
    // Series cache DB helpers
    // -------------------------------------------------------------------------

    private async Task<Dictionary<int, XtreamSeriesCache>> LoadSeriesCacheAsync(
        string providerId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.XtreamSeriesCache
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.SeriesId);
    }

    // Providers linked to the active profile expand before standby providers.
    private async Task<int> GetExpansionPriorityAsync(string providerId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var isActiveProfileProvider = await db.ProfileProviders
            .AsNoTracking()
            .AnyAsync(x => x.ProviderId == providerId
                           && x.Enabled
                           && x.Profile.Enabled
                           && x.Profile.IsActive,
                cancellationToken);
        return isActiveProfileProvider ? 0 : 1;
    }

    private async Task DeleteStaleCacheAsync(
        string providerId, List<int> staleIds, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.XtreamSeriesCache
            .Where(x => x.ProviderId == providerId && staleIds.Contains(x.SeriesId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Primitive helpers
    // -------------------------------------------------------------------------

    private static string BuildActionUrl(string baseUrl, string username, string password, string action)
        => $"{baseUrl}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action={action}";

    private static async Task<(string Cat, string Streams)> FetchPairAsync(
        HttpClient client, string catUrl, string streamUrl,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var catTask = HttpFetchHelper.FetchStringAsync(client, catUrl, timeoutSeconds, cancellationToken);
        var streamTask = HttpFetchHelper.FetchStringAsync(client, streamUrl, timeoutSeconds, cancellationToken);
        await Task.WhenAll(catTask, streamTask);
        return (catTask.Result, streamTask.Result);
    }

    private static int ReadInt(JsonElement el, string property)
        => el.TryGetProperty(property, out var v) ? ReadInt(v) : 0;

    private static int ReadInt(JsonElement el)
        => el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var n) ? n : 0,
            JsonValueKind.String => int.TryParse(el.GetString(), out var n) ? n : 0,
            _ => 0
        };

    private static long ReadLong(JsonElement el, string property)
        => el.TryGetProperty(property, out var v) ? ReadLong(v) : 0;

    private static long ReadLong(JsonElement el)
        => el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(el.GetString(), out var n) ? n : 0,
            _ => 0
        };

    private static string? ReadString(JsonElement el, string property)
        => el.TryGetProperty(property, out var v) ? ReadString(v) : null;

    private static string? ReadString(JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
