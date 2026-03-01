using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M3Undle.Core.M3u;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Application;

/// <summary>
/// Scoped service that executes one full snapshot refresh cycle.
/// Created per run by <see cref="SnapshotRefreshService"/> via IServiceScopeFactory.
/// </summary>
public sealed class SnapshotBuilder(
    ApplicationDbContext db,
    ProviderFetcher fetcher,
    IWebHostEnvironment env,
    IOptions<SnapshotOptions> snapshotOptions,
    ILogger<SnapshotBuilder> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record GroupFilterConfig(string ProfileGroupFilterId, string ChannelMode, string OutputName, int? AutoNumStart, int? AutoNumEnd);
    private sealed record ChannelOverride(string? OutputGroupName, int? ChannelNumber);

    // In-memory channel data used by BuildChannelIndex — sourced from DB provider_channels
    private sealed record ChannelBuildData(
        string? ProviderChannelKey,
        string DisplayName,
        string? StreamUrl,
        string ContentType,
        string? GroupTitle,
        string? TvgId,
        string? TvgName,
        string? LogoUrl);

    /// <summary>Full refresh: fetch from provider, sync to DB, then build snapshot.</summary>
    public async Task<(bool Succeeded, string? ErrorSummary)> RunAsync(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });

        // 1. Find active + enabled provider
        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

        if (provider is null)
        {
            logger.LogInformation("Snapshot refresh skipped — no active+enabled provider found.");
            return (false, null);
        }

        // 2. Find associated profile (first enabled, lowest priority number)
        var profileLink = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (profileLink is null)
        {
            logger.LogInformation("Snapshot refresh skipped — active provider {ProviderId} is not linked to any enabled profile.", provider.ProviderId);
            return (false, null);
        }

        var profileExists = await db.Profiles
            .AsNoTracking()
            .AnyAsync(x => x.ProfileId == profileLink.ProfileId && x.Enabled, cancellationToken);

        if (!profileExists)
        {
            logger.LogInformation("Snapshot refresh skipped — profile {ProfileId} is not enabled.", profileLink.ProfileId);
            return (false, null);
        }

        var profileId = profileLink.ProfileId;

        // 3. Create FetchRun pre-saved as "running" (crash leaves it as "running", not "fail")
        var now = DateTime.UtcNow;
        var fetchRun = new FetchRun
        {
            FetchRunId = Guid.NewGuid().ToString(),
            ProviderId = provider.ProviderId,
            StartedUtc = now,
            Status = "running",
            Type = "snapshot",
        };
        db.FetchRuns.Add(fetchRun);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Starting snapshot refresh for provider {ProviderId}, profile {ProfileId}.", provider.ProviderId, profileId);

        // 4. Fetch playlist — failure is fatal (preserve last-known-good)
        PlaylistFetchResult playlistResult;
        try
        {
            playlistResult = await fetcher.FetchPlaylistAsync(provider, cancellationToken);
        }
        catch (Exception ex) when (ex is ProviderFetchException or ProviderParseException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Playlist fetch/parse failed for provider {ProviderId}.", provider.ProviderId);
            await FailFetchRunAsync(fetchRun, ex.Message);
            return (false, ex.Message);
        }

        logger.LogInformation("Fetched playlist: {ChannelCount} channels for provider {ProviderId}.",
            playlistResult.Channels.Count, provider.ProviderId);

        // 5. Fetch XMLTV — failure is non-fatal
        string xmltvContent;
        long xmltvBytes = 0;
        try
        {
            var xmltvResult = await fetcher.FetchXmltvAsync(provider, cancellationToken);
            xmltvContent = xmltvResult.Xml;
            xmltvBytes = xmltvResult.Bytes;
            logger.LogInformation("Fetched XMLTV: {XmltvBytes} bytes for provider {ProviderId}.",
                xmltvBytes, provider.ProviderId);
        }
        catch (ProviderFetchException ex)
        {
            logger.LogWarning(ex, "XMLTV fetch failed for provider {ProviderId} — using empty guide.", provider.ProviderId);
            xmltvContent = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv generator-info-name=\"M3Undle\"></tv>";
        }

        // 5b. Sync provider groups + channels to DB (ALL content types), then create pending filter rows for new groups.
        var groupNameToId = await SyncProviderGroupsAsync(provider.ProviderId, playlistResult.Channels, now, cancellationToken);
        await SyncGroupFiltersAsync(profileId, provider.ProviderId, cancellationToken);
        await SyncProviderChannelsAsync(profileId, provider.ProviderId, fetchRun.FetchRunId, playlistResult.Channels, groupNameToId, now, cancellationToken);

        // 11. Mark FetchRun as ok
        fetchRun.FinishedUtc = DateTime.UtcNow;
        fetchRun.Status = "ok";
        fetchRun.ChannelCountSeen = playlistResult.Channels.Count;
        fetchRun.PlaylistBytes = (int)Math.Min(playlistResult.Bytes, int.MaxValue);
        fetchRun.XmltvBytes = (int)Math.Min(xmltvBytes, int.MaxValue);
        await db.SaveChangesAsync(cancellationToken);

        // 6-10. Build snapshot from synced DB data
        var (succeeded, errorSummary) = await BuildSnapshotFromDbAsync(provider, profileId, xmltvContent, cancellationToken);

        return (succeeded, errorSummary);
    }

    /// <summary>Build snapshot from already-synced DB data — no provider re-fetch.</summary>
    public async Task<(bool Succeeded, string? ErrorSummary)> BuildOnlyAsync(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });

        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

        if (provider is null)
        {
            logger.LogInformation("Snapshot build skipped — no active+enabled provider found.");
            return (false, null);
        }

        var profileLink = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        if (profileLink is null)
        {
            logger.LogInformation("Snapshot build skipped — active provider {ProviderId} has no enabled profile.", provider.ProviderId);
            return (false, null);
        }

        var profileExists = await db.Profiles
            .AsNoTracking()
            .AnyAsync(x => x.ProfileId == profileLink.ProfileId && x.Enabled, cancellationToken);

        if (!profileExists)
        {
            logger.LogInformation("Snapshot build skipped — profile {ProfileId} is not enabled.", profileLink.ProfileId);
            return (false, null);
        }

        // Load latest XMLTV from most recent active snapshot (reuse guide; a full refresh will update it)
        var existingXmltvContent = "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv generator-info-name=\"M3Undle\"></tv>";
        var latestSnapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileLink.ProfileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSnapshot is not null && !string.IsNullOrEmpty(latestSnapshot.XmltvPath) && File.Exists(latestSnapshot.XmltvPath))
            existingXmltvContent = await File.ReadAllTextAsync(latestSnapshot.XmltvPath, cancellationToken);

        logger.LogInformation("Starting snapshot build-only for provider {ProviderId}, profile {ProfileId}.", provider.ProviderId, profileLink.ProfileId);

        return await BuildSnapshotFromDbAsync(provider, profileLink.ProfileId, existingXmltvContent, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<(bool Succeeded, string? ErrorSummary)> BuildSnapshotFromDbAsync(
        Provider provider,
        string profileId,
        string xmltvContent,
        CancellationToken cancellationToken)
    {
        // Load channels from DB (respects provider content type settings)
        var dbChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId
                     && x.Active
                     && (x.ContentType == "live"
                         || (provider.IncludeVod && x.ContentType == "vod")
                         || (provider.IncludeSeries && x.ContentType == "series")))
            .ToListAsync(cancellationToken);

        var channels = dbChannels.Select(ch => new ChannelBuildData(
            ch.ProviderChannelKey,
            ch.DisplayName,
            ch.StreamUrl,
            ch.ContentType,
            ch.GroupTitle,
            ch.TvgId,
            ch.TvgName,
            ch.LogoUrl)).ToList();

        // Load group filter config for this profile/provider
        var groupFilters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId && x.ProviderGroup.ProviderId == provider.ProviderId)
            .ToListAsync(cancellationToken);

        var includedGroups = groupFilters
            .Where(f => f.Decision == "include")
            .ToDictionary(
            f => f.ProviderGroup.RawName,
            f => new GroupFilterConfig(
                f.ProfileGroupFilterId,
                f.ChannelMode,
                f.OutputName ?? f.ProviderGroup.RawName,
                f.AutoNumStart,
                f.AutoNumEnd),
            StringComparer.Ordinal);

        // Load per-channel selections for groups in "select" mode
        var selectModeFilterIds = includedGroups.Values
            .Where(g => g.ChannelMode == "select")
            .Select(g => g.ProfileGroupFilterId)
            .ToList();

        Dictionary<string, Dictionary<string, ChannelOverride>> channelOverridesByFilterId = [];
        if (selectModeFilterIds.Count > 0)
        {
            var selections = await db.ProfileGroupChannelFilters
                .AsNoTracking()
                .Include(x => x.ProviderChannel)
                .Where(x => selectModeFilterIds.Contains(x.ProfileGroupFilterId))
                .ToListAsync(cancellationToken);

            channelOverridesByFilterId = selections
                .GroupBy(x => x.ProfileGroupFilterId)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(
                        x => x.ProviderChannel.StreamUrl,
                        x => new ChannelOverride(x.OutputGroupName, x.ChannelNumber),
                        StringComparer.Ordinal));
        }

        var channelIndex = BuildChannelIndex(
            channels,
            profileId,
            includedGroups,
            channelOverridesByFilterId,
            provider.IncludeVod,
            provider.IncludeSeries);

        // Write snapshot files
        var snapshotId = Guid.NewGuid().ToString();
        var snapshotDir = GetSnapshotDir(snapshotId);
        Directory.CreateDirectory(snapshotDir);

        var channelIndexPath = Path.Combine(snapshotDir, "channel_index.json");
        var xmltvPath = Path.Combine(snapshotDir, "guide.xml");

        await File.WriteAllTextAsync(channelIndexPath, JsonSerializer.Serialize(channelIndex, JsonOptions), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(xmltvPath, xmltvContent, Encoding.UTF8, cancellationToken);

        var snapshot = new Snapshot
        {
            SnapshotId = snapshotId,
            ProfileId = profileId,
            CreatedUtc = DateTime.UtcNow,
            Status = "staged",
            PlaylistPath = string.Empty,
            XmltvPath = xmltvPath,
            ChannelIndexPath = channelIndexPath,
            StatusJsonPath = string.Empty,
            ChannelCountPublished = channelIndex.Count,
        };
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);

        await PromoteSnapshotAsync(snapshot, profileId, cancellationToken);
        await PurgeOldSnapshotsAsync(profileId, cancellationToken);

        using var snapshotScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Snapshot" });
        logger.LogInformation(
            "Snapshot {SnapshotId} promoted to active — {ChannelCount} channels published.",
            snapshotId, channelIndex.Count);

        return (true, null);
    }

    private static List<ChannelIndexEntry> BuildChannelIndex(
        IReadOnlyList<ChannelBuildData> channels,
        string profileId,
        IReadOnlyDictionary<string, GroupFilterConfig> includedGroups,
        IReadOnlyDictionary<string, Dictionary<string, ChannelOverride>> channelOverridesByFilterId,
        bool includeVod,
        bool includeSeries)
    {
        // No included live groups and no VOD/series passthrough enabled.
        if (includedGroups.Count == 0 && !includeVod && !includeSeries)
            return [];

        var pending = new List<(string OutputGroup, ChannelBuildData Channel, int? ExplicitNumber)>();

        foreach (var channel in channels.Where(x => !string.IsNullOrWhiteSpace(x.StreamUrl)))
        {
            var contentType = channel.ContentType switch
            {
                "vod" => "vod",
                "series" => "series",
                _ => "live",
            };

            var groupName = channel.GroupTitle;
            var hasGroup = !string.IsNullOrWhiteSpace(groupName);

            // VOD/Series bypass group mapping completely.
            // They are controlled only by provider IncludeVod/IncludeSeries flags.
            if (contentType == "vod" || contentType == "series")
            {
                if ((contentType == "vod" && !includeVod) || (contentType == "series" && !includeSeries))
                    continue;

                var fallbackGroup = hasGroup
                    ? groupName!
                    : contentType == "series" ? "Series"
                    : contentType == "vod" ? "Movies"
                    : "Live";

                pending.Add((fallbackGroup, channel, null));
                continue;
            }

            // Live channels are opt-in via explicit included groups.
            if (!hasGroup || !includedGroups.TryGetValue(groupName!, out var filter))
                continue;

            if (filter.ChannelMode == "select")
            {
                if (!channelOverridesByFilterId.TryGetValue(filter.ProfileGroupFilterId, out var overrides))
                    continue;
                if (!overrides.TryGetValue(channel.StreamUrl ?? string.Empty, out var ov))
                    continue;

                var effectiveGroup = string.IsNullOrWhiteSpace(ov.OutputGroupName)
                    ? filter.OutputName
                    : ov.OutputGroupName;
                pending.Add((effectiveGroup, channel, ov.ChannelNumber));
            }
            else
            {
                pending.Add((filter.OutputName, channel, null));
            }
        }

        var result = new List<ChannelIndexEntry>();

        var byOutputGroup = pending
            .GroupBy(x => x.OutputGroup, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byOutputGroup)
        {
            var outputName = group.Key;

            var parentFilter = includedGroups.Values
                .FirstOrDefault(f => f.OutputName == outputName);

            var withNum = group
                .Where(x => x.ExplicitNumber.HasValue)
                .OrderBy(x => x.ExplicitNumber!.Value)
                .ToList();

            var withoutNum = group
                .Where(x => !x.ExplicitNumber.HasValue)
                .OrderBy(x => x.Channel.DisplayName, StringComparer.Ordinal)
                .ThenBy(x => x.Channel.StreamUrl, StringComparer.Ordinal)
                .ToList();

            foreach (var (_, channel, num) in withNum)
                result.Add(BuildEntry(channel, outputName, num, profileId));

            int? nextNum = parentFilter?.AutoNumStart;
            int? maxNum = parentFilter?.AutoNumEnd;

            foreach (var (_, channel, _) in withoutNum)
            {
                int? assignedNum = null;
                if (nextNum.HasValue)
                {
                    assignedNum = nextNum;
                    nextNum++;
                    if (maxNum.HasValue && nextNum > maxNum)
                        nextNum = null;
                }
                result.Add(BuildEntry(channel, outputName, assignedNum, profileId));
            }
        }

        return result;
    }

    private static ChannelIndexEntry BuildEntry(
        ChannelBuildData channel,
        string? groupTitle,
        int? tvgChno,
        string profileId)
    {
        // Include stream URL + display/group context to avoid collapsing distinct items
        // that share tvg-id/URL across multiple provider groups.
        var stableKey = !string.IsNullOrWhiteSpace(channel.ProviderChannelKey)
            ? $"{channel.ProviderChannelKey}\u001f{channel.StreamUrl}\u001f{groupTitle}\u001f{channel.DisplayName}"
            : $"{channel.DisplayName}\u001f{channel.StreamUrl}\u001f{groupTitle}";

        return new ChannelIndexEntry(
            StreamKey: DeriveStreamKey(stableKey, profileId),
            DisplayName: channel.DisplayName,
            TvgId: channel.TvgId,
            TvgName: channel.TvgName,
            LogoUrl: channel.LogoUrl,
            GroupTitle: groupTitle,
            TvgChno: tvgChno,
            ProviderChannelId: string.Empty,
            StreamUrl: channel.StreamUrl!);
    }

    private async Task<Dictionary<string, string>> SyncProviderGroupsAsync(
        string providerId,
        IReadOnlyList<ParsedProviderChannel> channels,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Include ALL channels (live, vod, series) — determine dominant content type per group
        var groupData = channels
            .Where(x => !string.IsNullOrWhiteSpace(x.GroupTitle) && !string.IsNullOrWhiteSpace(x.StreamUrl))
            .GroupBy(x => x.GroupTitle!, StringComparer.Ordinal)
            .Select(g =>
            {
                int live = 0, vod = 0, series = 0;
                foreach (var ch in g)
                {
                    switch (LiveClassifier.ClassifyContent(ch.StreamUrl))
                    {
                        case "vod": vod++; break;
                        case "series": series++; break;
                        default: live++; break;
                    }
                }
                int total = live + vod + series;
                string contentType = total == 0 ? "live"
                    : live == total ? "live"
                    : vod == total ? "vod"
                    : series == total ? "series"
                    : "mixed";

                return new { GroupName = g.Key, Count = total, ContentType = contentType };
            })
            .ToDictionary(x => x.GroupName, StringComparer.Ordinal);

        var groupNames = groupData.Keys.ToList();

        var existingGroups = await db.ProviderGroups
            .Where(x => x.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        var byName = existingGroups.ToDictionary(x => x.RawName, StringComparer.Ordinal);

        foreach (var groupName in groupNames)
        {
            var info = groupData[groupName];
            if (byName.TryGetValue(groupName, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.Active = true;
                existing.ChannelCount = info.Count;
                existing.ContentType = info.ContentType;
                continue;
            }

            db.ProviderGroups.Add(new ProviderGroup
            {
                ProviderGroupId = Guid.NewGuid().ToString(),
                ProviderId = providerId,
                RawName = groupName,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Active = true,
                ChannelCount = info.Count,
                ContentType = info.ContentType,
            });
        }

        foreach (var group in existingGroups)
        {
            if (!groupData.ContainsKey(group.RawName))
            {
                group.Active = false;
                group.ChannelCount = 0;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return await db.ProviderGroups
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .ToDictionaryAsync(x => x.RawName, x => x.ProviderGroupId, StringComparer.Ordinal, cancellationToken);
    }

    private async Task SyncGroupFiltersAsync(
        string profileId,
        string providerId,
        CancellationToken cancellationToken)
    {
        var allGroupIds = await db.ProviderGroups
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .Select(x => x.ProviderGroupId)
            .ToListAsync(cancellationToken);

        var existingFilterGroupIds = await db.ProfileGroupFilters
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .Select(x => x.ProviderGroupId)
            .ToHashSetAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var newFilters = allGroupIds
            .Where(id => !existingFilterGroupIds.Contains(id))
            .Select(id => new ProfileGroupFilter
            {
                ProfileGroupFilterId = Guid.NewGuid().ToString(),
                ProfileId = profileId,
                ProviderGroupId = id,
                Decision = "pending",
                TrackNewChannels = false,
                CreatedUtc = now,
                UpdatedUtc = now,
            })
            .ToList();

        if (newFilters.Count > 0)
        {
            db.ProfileGroupFilters.AddRange(newFilters);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created {Count} new pending group filter(s) for profile {ProfileId}.", newFilters.Count, profileId);
        }
    }

    private async Task SyncProviderChannelsAsync(
        string profileId,
        string providerId,
        string fetchRunId,
        IReadOnlyList<ParsedProviderChannel> channels,
        IReadOnlyDictionary<string, string> groupNameToId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        static string BuildStableIdentity(ParsedProviderChannel ch)
        {
            // Include stream URL + display/group context to avoid collapsing distinct items
            // that share tvg-id/URL across multiple provider groups.
            return !string.IsNullOrWhiteSpace(ch.ProviderChannelKey)
                ? $"{ch.ProviderChannelKey}\u001f{ch.StreamUrl}\u001f{ch.GroupTitle}\u001f{ch.DisplayName}"
                : $"{ch.DisplayName}\u001f{ch.StreamUrl}\u001f{ch.GroupTitle}";
        }

        static string DeriveChannelKey(string stableIdentity, int occurrence)
        {
            // Preserve exact duplicate lines from provider feeds by adding an occurrence suffix.
            // Most channels are occurrence=1 and keep a stable key derived from identity fields.
            var keyedIdentity = occurrence > 1 ? $"{stableIdentity}\u001fdup:{occurrence}" : stableIdentity;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(keyedIdentity));
            return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..16];
        }

        // Skip syncing channels for excluded groups — they get deactivated below via unseen-key sweep.
        var excludedGroupIds = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId
                     && x.Decision == "exclude"
                     && x.ProviderGroup.ContentType == "live")
            .Select(x => x.ProviderGroupId)
            .ToHashSetAsync(cancellationToken);

        var existingChannels = await db.ProviderChannels
            .Where(x => x.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        var byKey = existingChannels
            .Where(x => x.ProviderChannelKey is not null)
            .ToDictionary(x => x.ProviderChannelKey!, StringComparer.Ordinal);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var occurrenceByStableIdentity = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var ch in channels)
        {
            if (string.IsNullOrWhiteSpace(ch.DisplayName) || string.IsNullOrWhiteSpace(ch.StreamUrl)) continue;

            var groupId = ch.GroupTitle is not null && groupNameToId.TryGetValue(ch.GroupTitle, out var gid)
                ? (string?)gid : null;

            // Lazy: skip channels from excluded groups entirely.
            if (groupId is not null && excludedGroupIds.Contains(groupId)) continue;

            var stableIdentity = BuildStableIdentity(ch);
            var occurrence = occurrenceByStableIdentity.GetValueOrDefault(stableIdentity) + 1;
            occurrenceByStableIdentity[stableIdentity] = occurrence;

            var key = DeriveChannelKey(stableIdentity, occurrence);
            if (!seenKeys.Add(key)) continue;

            var contentType = LiveClassifier.ClassifyContent(ch.StreamUrl);

            if (byKey.TryGetValue(key, out var entity))
            {
                entity.DisplayName = ch.DisplayName;
                entity.TvgId = ch.TvgId;
                entity.TvgName = ch.TvgName;
                entity.LogoUrl = ch.LogoUrl;
                entity.StreamUrl = ch.StreamUrl;
                entity.GroupTitle = ch.GroupTitle;
                entity.ProviderGroupId = groupId;
                entity.ContentType = contentType;
                entity.LastSeenUtc = now;
                entity.Active = true;
                entity.LastFetchRunId = fetchRunId;
            }
            else
            {
                db.ProviderChannels.Add(new ProviderChannel
                {
                    ProviderChannelId = Guid.NewGuid().ToString(),
                    ProviderId = providerId,
                    ProviderChannelKey = key,
                    DisplayName = ch.DisplayName,
                    TvgId = ch.TvgId,
                    TvgName = ch.TvgName,
                    LogoUrl = ch.LogoUrl,
                    StreamUrl = ch.StreamUrl,
                    GroupTitle = ch.GroupTitle,
                    ProviderGroupId = groupId,
                    ContentType = contentType,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    Active = true,
                    LastFetchRunId = fetchRunId,
                });
            }
        }

        foreach (var entity in existingChannels.Where(x => x.ProviderChannelKey is not null && !seenKeys.Contains(x.ProviderChannelKey!)))
            entity.Active = false;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Synced {Count} provider channel(s) for provider {ProviderId}.", seenKeys.Count, providerId);
    }

    private static string DeriveStreamKey(string stableKey, string profileId)
    {
        var input = Encoding.UTF8.GetBytes($"{stableKey}:{profileId}");
        var hash = SHA256.HashData(input);
        return Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=')[..16];
    }

    private async Task PromoteSnapshotAsync(Snapshot newSnapshot, string profileId, CancellationToken cancellationToken)
    {
        var previousActives = await db.Snapshots
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .ToListAsync(cancellationToken);

        foreach (var old in previousActives)
            old.Status = "archived";

        newSnapshot.Status = "active";
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PurgeOldSnapshotsAsync(string profileId, CancellationToken cancellationToken)
    {
        var retention = snapshotOptions.Value.RetentionCount;

        var allSnapshots = await db.Snapshots
            .Where(x => x.ProfileId == profileId)
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        var toDelete = allSnapshots.Skip(retention).ToList();
        if (toDelete.Count == 0)
            return;

        foreach (var snapshot in toDelete)
        {
            var dir = GetSnapshotDir(snapshot.SnapshotId);
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete snapshot directory {Dir} — skipping file cleanup.", dir);
            }
        }

        db.Snapshots.RemoveRange(toDelete);
        await db.SaveChangesAsync(cancellationToken);
    }

    private string GetSnapshotDir(string snapshotId)
    {
        var baseDir = snapshotOptions.Value.Directory;
        if (!Path.IsPathRooted(baseDir))
            baseDir = Path.Combine(env.ContentRootPath, baseDir);
        return Path.Combine(baseDir, "m3undle", snapshotId);
    }

    private async Task FailFetchRunAsync(FetchRun fetchRun, string errorSummary)
    {
        fetchRun.FinishedUtc = DateTime.UtcNow;
        fetchRun.Status = "fail";
        fetchRun.ErrorSummary = errorSummary;
        // Use CancellationToken.None — must persist even if run was cancelled
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
