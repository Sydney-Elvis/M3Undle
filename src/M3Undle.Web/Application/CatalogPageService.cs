using M3Undle.Web.Contracts;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace M3Undle.Web.Application;

public sealed class CatalogPageService(
    IServiceScopeFactory scopeFactory,
    AppEventBus eventBus,
    IHttpClientFactory? httpClientFactory = null,
    SecretEncryptionService? secretEncryption = null)
{
    public async Task<string?> GetDefaultProfileIdAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Profiles
            .AsNoTracking()
            .Where(x => x.Enabled && x.IsActive)
            .Select(x => x.ProfileId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<CatalogGroupDto>> ListGroupsAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfileProviders
            .AsNoTracking()
            .Where(link => link.ProfileId == profileId)
            .SelectMany(
                link => link.Provider.ProviderGroups
                    .Where(group => group.Active
                                    && (group.ContentType == "vod" || group.ContentType == "series")),
                (link, group) => new CatalogGroupDto
                {
                    ProviderGroupId = group.ProviderGroupId,
                    ProviderId = link.ProviderId,
                    ProviderName = link.Provider.Name,
                    Name = group.RawName,
                    ContentType = group.ContentType,
                    ItemCount = group.ChannelCount ?? 0,
                    TitleCount = group.CatalogItems.Count(item => item.Active),
                    FirstSeenUtc = group.FirstSeenUtc,
                    LastSeenUtc = group.LastSeenUtc,
                    ProviderEnabled = link.Provider.Enabled,
                    ProfileProviderEnabled = link.Enabled,
                    ContentTypeEnabled = group.ContentType == "vod"
                        ? link.Provider.IncludeVod
                        : link.Provider.IncludeSeries,
                    Decision = group.ProfileCatalogGroupFilters
                        .Where(filter => filter.ProfileId == profileId)
                        .Select(filter => filter.Decision)
                        .FirstOrDefault() ?? LineupReviewSemantics.GroupDecisionInclude,
                    IsNew = group.ProfileCatalogGroupFilters
                        .Any(filter => filter.ProfileId == profileId && filter.IsNew),
                })
            .OrderBy(x => x.Name)
            .ThenBy(x => x.ProviderName)
            .ThenBy(x => x.ContentType)
            .ToListAsync(cancellationToken);
    }

    public async Task<CatalogItemsResponse?> GetItemsAsync(
        string profileId,
        string providerGroupId,
        int page,
        int pageSize,
        string? search,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var group = await db.ProviderGroups
            .AsNoTracking()
            .Where(x => x.ProviderGroupId == providerGroupId
                        && x.Provider.ProfileProviders.Any(link => link.ProfileId == profileId)
                        && (x.ContentType == "vod" || x.ContentType == "series"))
            .Select(x => new
            {
                x.ProviderGroupId,
                GroupName = x.RawName,
                ProviderName = x.Provider.Name,
                x.ContentType,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (group is null)
            return null;

        var query = db.CatalogItems
            .AsNoTracking()
            .Where(x => x.ProviderGroupId == providerGroupId && x.Active);
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var escaped = term
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            query = query.Where(x => EF.Functions.Like(x.Title, $"%{escaped}%", "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Title)
            .ThenBy(x => x.CatalogItemId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new CatalogItemDto
            {
                CatalogItemId = x.CatalogItemId,
                Title = x.Title,
                ContentType = x.ContentType,
                EpisodeCount = x.EpisodeCount,
                LastSeenUtc = x.LastSeenUtc,
            })
            .ToListAsync(cancellationToken);

        return new CatalogItemsResponse
        {
            ProviderGroupId = group.ProviderGroupId,
            GroupName = group.GroupName,
            ProviderName = group.ProviderName,
            ContentType = group.ContentType,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items,
        };
    }

    public async Task<CatalogTitleSearchResponse> SearchItemsAsync(
        string profileId,
        string? contentType,
        int page,
        int pageSize,
        string? search,
        string? groupSearch,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = db.CatalogItems
            .AsNoTracking()
            .Where(item => item.Active
                           && item.ProviderGroup.Active
                           && item.Provider.ProfileProviders.Any(link => link.ProfileId == profileId));
        if (contentType is "vod" or "series")
            query = query.Where(item => item.ContentType == contentType);

        var groupTerm = groupSearch?.Trim();
        if (!string.IsNullOrWhiteSpace(groupTerm))
        {
            var escapedGroup = EscapeLikePattern(groupTerm);
            query = query.Where(item =>
                EF.Functions.Like(item.ProviderGroup.RawName, $"%{escapedGroup}%", "\\")
                || EF.Functions.Like(item.Provider.Name, $"%{escapedGroup}%", "\\"));
        }

        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var escaped = EscapeLikePattern(term);
            query = query.Where(item => EF.Functions.Like(item.Title, $"%{escaped}%", "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.Title)
            .ThenBy(item => item.ContentType)
            .ThenBy(item => item.ProviderGroup.RawName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new CatalogItemDto
            {
                CatalogItemId = item.CatalogItemId,
                Title = item.Title,
                ContentType = item.ContentType,
                EpisodeCount = item.EpisodeCount,
                LastSeenUtc = item.LastSeenUtc,
                ProviderGroupId = item.ProviderGroupId,
                GroupName = item.ProviderGroup.RawName,
                ProviderName = item.Provider.Name,
            })
            .ToListAsync(cancellationToken);

        return new CatalogTitleSearchResponse
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items,
        };
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<CatalogItemDetailDto?> GetItemDetailAsync(
        string profileId,
        string catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = await db.CatalogItems
            .AsNoTracking()
            .Include(x => x.Provider)
            .Include(x => x.ProviderGroup)
            .SingleOrDefaultAsync(x => x.CatalogItemId == catalogItemId
                && x.Active
                && x.Provider.ProfileProviders.Any(link => link.ProfileId == profileId), cancellationToken);
        if (item is null)
            return null;

        var detail = new CatalogItemDetailDto
        {
            CatalogItemId = item.CatalogItemId,
            Title = item.Title,
            ContentType = item.ContentType,
            GroupName = item.ProviderGroup.RawName,
            ProviderName = item.Provider.Name,
            HasArtwork = !string.IsNullOrWhiteSpace(item.ArtworkUrl),
        };

        if (!TryReadProviderItemId(item.ProviderItemKey, out var providerItemId))
        {
            detail.MetadataNotice = "This provider did not supply an item ID, so only indexed playlist details are available.";
            return detail;
        }

        string? json = null;
        if (item.ContentType == "series")
        {
            json = await db.XtreamSeriesCache
                .AsNoTracking()
                .Where(x => x.ProviderId == item.ProviderId && x.SeriesId == providerItemId)
                .Select(x => x.EpisodesJson)
                .SingleOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                detail.MetadataNotice = "Series details are not cached yet. Background episode discovery may still be in progress.";
        }
        else if (item.ContentType == "vod")
        {
            json = await FetchVodDetailsAsync(item.Provider, providerItemId, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                detail.MetadataNotice = "The provider did not return movie details; indexed title and category data are shown.";
        }

        if (!string.IsNullOrWhiteSpace(json))
            PopulateDetail(detail, json, item.ContentType == "series");
        return detail;
    }

    public async Task<(byte[] Bytes, string ContentType)?> GetArtworkAsync(
        string profileId,
        string catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artworkUrl = await db.CatalogItems.AsNoTracking()
            .Where(x => x.CatalogItemId == catalogItemId
                && x.Active
                && x.Provider.ProfileProviders.Any(link => link.ProfileId == profileId))
            .Select(x => x.ArtworkUrl)
            .SingleOrDefaultAsync(cancellationToken);
        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        const int maxBytes = 5 * 1024 * 1024;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (httpClientFactory is null)
            return null;
        using var response = await httpClientFactory.CreateClient().SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength > maxBytes)
            return null;

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType)
            || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return null;

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, timeout.Token);
            if (read == 0)
                break;
            if (output.Length + read > maxBytes)
                return null;
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        return (output.ToArray(), contentType);
    }

    private async Task<string?> FetchVodDetailsAsync(Provider provider, int itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.XtreamBaseUrl)
            || string.IsNullOrWhiteSpace(provider.XtreamUsername)
            || string.IsNullOrWhiteSpace(provider.XtreamEncryptedPassword)
            || httpClientFactory is null
            || secretEncryption is null)
            return null;

        string password;
        try { password = secretEncryption.Decrypt(provider.XtreamEncryptedPassword); }
        catch (InvalidOperationException) { return null; }
        var url = $"{provider.XtreamBaseUrl.TrimEnd('/')}/player_api.php?username={Uri.EscapeDataString(provider.XtreamUsername)}&password={Uri.EscapeDataString(password)}&action=get_vod_info&vod_id={itemId.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            return await HttpFetchHelper.FetchStringAsync(
                httpClientFactory.CreateClient(), url, Math.Clamp(provider.TimeoutSeconds, 10, 60), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or ProviderFetchException or OperationCanceledException)
        {
            return null;
        }
    }

    private static void PopulateDetail(CatalogItemDetailDto detail, string json, bool includeEpisodes)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var info = root.TryGetProperty("info", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object
                ? infoElement : root;
            detail.Plot = ReadText(info, "plot", "description");
            detail.Genre = ReadText(info, "genre");
            detail.ReleaseDate = ReadText(info, "releaseDate", "release_date", "releasedate");
            detail.Director = ReadText(info, "director");
            detail.Cast = ReadText(info, "cast", "actors");
            detail.Rating = ReadText(info, "rating", "rating_5based");
            detail.Duration = ReadText(info, "duration", "duration_secs");
            if (!includeEpisodes || !root.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Object)
                return;

            foreach (var seasonProperty in episodes.EnumerateObject())
            {
                if (seasonProperty.Value.ValueKind != JsonValueKind.Array)
                    continue;
                _ = int.TryParse(seasonProperty.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonNumber);
                var season = new CatalogSeasonDto { SeasonNumber = seasonNumber, Name = $"Season {seasonProperty.Name}" };
                foreach (var episodeElement in seasonProperty.Value.EnumerateArray())
                {
                    var episodeInfo = episodeElement.TryGetProperty("info", out var nestedInfo) && nestedInfo.ValueKind == JsonValueKind.Object
                        ? nestedInfo : episodeElement;
                    var episodeNumber = ReadInt(episodeElement, "episode_num");
                    season.Episodes.Add(new CatalogEpisodeDto
                    {
                        EpisodeNumber = episodeNumber,
                        Title = ReadText(episodeElement, "title") ?? $"Episode {episodeNumber}",
                        Plot = ReadText(episodeInfo, "plot", "description"),
                        ReleaseDate = ReadText(episodeInfo, "release_date", "releasedate", "air_date"),
                        Duration = ReadText(episodeInfo, "duration", "duration_secs"),
                    });
                }
                detail.Seasons.Add(season);
            }
            detail.Seasons = detail.Seasons.OrderBy(x => x.SeasonNumber).ToList();
        }
        catch (JsonException)
        {
            detail.MetadataNotice = "The provider returned malformed item details; indexed data are shown.";
        }
    }

    private static bool TryReadProviderItemId(string key, out int itemId)
    {
        itemId = 0;
        return key.StartsWith("id:", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId);
    }

    private static string? ReadText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text) && text != "0")
                return text;
        }
        return null;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric)
            ? numeric
            : int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric) ? numeric : 0;
    }

    public async Task<bool> UpdateDecisionAsync(
        string profileId,
        string providerGroupId,
        string decision,
        CancellationToken cancellationToken)
    {
        if (decision is not (LineupReviewSemantics.GroupDecisionInclude or LineupReviewSemantics.GroupDecisionExclude))
            return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var validGroup = await db.ProviderGroups
            .AsNoTracking()
            .AnyAsync(group => group.ProviderGroupId == providerGroupId
                               && (group.ContentType == "vod" || group.ContentType == "series")
                               && group.Provider.ProfileProviders.Any(link => link.ProfileId == profileId),
                cancellationToken);
        if (!validGroup)
            return false;

        var filter = await db.ProfileCatalogGroupFilters
            .SingleOrDefaultAsync(x => x.ProfileId == profileId && x.ProviderGroupId == providerGroupId,
                cancellationToken);
        var now = DateTime.UtcNow;
        if (filter is null)
        {
            filter = new ProfileCatalogGroupFilter
            {
                ProfileCatalogGroupFilterId = Guid.NewGuid().ToString(),
                ProfileId = profileId,
                ProviderGroupId = providerGroupId,
                Decision = decision,
                IsNew = false,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.ProfileCatalogGroupFilters.Add(filter);
        }
        else
        {
            filter.Decision = decision;
            filter.IsNew = false;
            filter.UpdatedUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        eventBus.Publish(AppEventKind.GroupFiltersChanged);
        return true;
    }
}
