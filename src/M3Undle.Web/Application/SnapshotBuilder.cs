using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using M3Undle.Core.M3u;
using M3Undle.Web.Application.Epg;
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
    EpgSourceFetcher epgSourceFetcher,
    EpgChannelMapper epgChannelMapper,
    EpgCompiler epgCompiler,
    XmltvParser xmltvParser,
    RuntimePaths runtimePaths,
    IWebHostEnvironment env,
    IOptions<SnapshotOptions> snapshotOptions,
    ILogger<SnapshotBuilder> logger)
{
    internal sealed record GroupFilterConfig(string ProfileGroupFilterId, string OutputName, int? AutoNumStart, int? AutoNumEnd, int? SortOverride);
    internal sealed record ChannelBuildData(
        string ProviderChannelId,
        string? ProviderChannelKey,
        string DisplayName,
        string? StreamUrl,
        string ContentType,
        string? GroupTitle,
        string? TvgId,
        string? TvgName,
        string? LogoUrl);
    internal sealed record ChannelOverride(string? OutputGroupName, int? ChannelNumber, string? TvgIdOverride);

    /// <summary>Full refresh: fetch from provider, sync to DB, then build snapshot.</summary>
    public async Task<(bool Succeeded, string? ErrorSummary, IReadOnlyList<ParsedProviderChannel> Channels)> RunAsync(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });

        // 1. Find active + enabled provider
        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

        if (provider is null)
        {
            logger.LogInformation("Snapshot refresh skipped — no active+enabled provider found.");
            return (false, null, []);
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
            return (false, null, []);
        }

        var profileExists = await db.Profiles
            .AsNoTracking()
            .AnyAsync(x => x.ProfileId == profileLink.ProfileId && x.Enabled, cancellationToken);

        if (!profileExists)
        {
            logger.LogInformation("Snapshot refresh skipped — profile {ProfileId} is not enabled.", profileLink.ProfileId);
            return (false, null, []);
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
        var sw = Stopwatch.StartNew();
        try
        {
            playlistResult = await fetcher.FetchPlaylistAsync(provider, cancellationToken);
        }
        catch (Exception ex) when (ex is ProviderFetchException or ProviderParseException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Playlist fetch/parse failed for provider {ProviderId} after {Elapsed}ms.", provider.ProviderId, sw.ElapsedMilliseconds);
            await FailFetchRunAsync(fetchRun, ex.Message);
            return (false, ex.Message, []);
        }

        logger.LogInformation("Playlist fetched in {Elapsed}ms — {ChannelCount} channels for provider {ProviderId}.",
            sw.ElapsedMilliseconds, playlistResult.Channels.Count, provider.ProviderId);
        sw.Restart();

        // 5 + 5b. EPG fetch (multi-source) + DB sync.
        // Individual source failures are soft — we continue with whatever sources succeed.
        // If the run CT fires it is surfaced after EPG fetch via ThrowIfCancellationRequested.
        string xmltvContent;
        long xmltvBytes = 0;
        var stage = "xmltv";
        try
        {
            xmltvContent = await FetchAndCompileEpgAsync(provider, profileId, sw, cancellationToken);
            xmltvBytes = Encoding.UTF8.GetByteCount(xmltvContent);

            // If the run CT fired during EPG fetch, surface it now before touching the DB.
            cancellationToken.ThrowIfCancellationRequested();
            sw.Restart();

            // 5b. Sync provider groups to DB (ALL content types), sync live channels only to DB, then create hold+new filter rows for new groups.
            stage = "groups";
            var groupNameToId = await SyncProviderGroupsAsync(provider.ProviderId, playlistResult.Channels, now, cancellationToken);
            logger.LogInformation("Groups synced in {Elapsed}ms for provider {ProviderId}.", sw.ElapsedMilliseconds, provider.ProviderId);
            sw.Restart();

            stage = "group-filters";
            await SyncGroupFiltersAsync(profileId, provider.ProviderId, cancellationToken);
            logger.LogInformation("Group filters synced in {Elapsed}ms for provider {ProviderId}.", sw.ElapsedMilliseconds, provider.ProviderId);
            sw.Restart();

            stage = "channels";
            await SyncProviderChannelsAsync(profileId, provider.ProviderId, fetchRun.FetchRunId, playlistResult.Channels, groupNameToId, now, cancellationToken);
            logger.LogInformation("Channels synced in {Elapsed}ms for provider {ProviderId}.", sw.ElapsedMilliseconds, provider.ProviderId);
            sw.Restart();
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Snapshot refresh timed out during stage '{Stage}' for provider {ProviderId}.", stage, provider.ProviderId);
            throw;
        }

        // 11. Mark FetchRun as ok
        fetchRun.FinishedUtc = DateTime.UtcNow;
        fetchRun.Status = "ok";
        fetchRun.ChannelCountSeen = playlistResult.Channels.Count;
        fetchRun.PlaylistBytes = (int)Math.Min(playlistResult.Bytes, int.MaxValue);
        fetchRun.XmltvBytes = (int)Math.Min(xmltvBytes, int.MaxValue); // compiled guide size
        await db.SaveChangesAsync(cancellationToken);

        // 6-10. Build snapshot from synced DB data (live) + in-memory VOD/series
        var (succeeded, errorSummary) = await BuildSnapshotFromDbAsync(provider, profileId, xmltvContent, playlistResult.Channels, cancellationToken);

        return (succeeded, errorSummary, playlistResult.Channels);
    }

    /// <summary>Build snapshot from already-synced DB data — no provider re-fetch.
    /// Pass the channels from the last full refresh so VOD/series can be included without re-fetching.</summary>
    public async Task<(bool Succeeded, string? ErrorSummary)> BuildOnlyAsync(IReadOnlyList<ParsedProviderChannel> providerChannels, CancellationToken cancellationToken)
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

        return await BuildSnapshotFromDbAsync(provider, profileLink.ProfileId, existingXmltvContent, providerChannels, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<(bool Succeeded, string? ErrorSummary)> BuildSnapshotFromDbAsync(
        Provider provider,
        string profileId,
        string xmltvContent,
        IReadOnlyList<ParsedProviderChannel> providerChannels,
        CancellationToken cancellationToken)
    {
        // Load live channels from DB (VOD/series are not persisted — sourced from in-memory providerChannels)
        var dbChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "live")
            .ToListAsync(cancellationToken);

        var channels = dbChannels.Select(ch => new ChannelBuildData(
            ch.ProviderChannelId,
            ch.ProviderChannelKey,
            ch.DisplayName,
            ch.StreamUrl,
            ch.ContentType,
            ch.GroupTitle,
            ch.TvgId,
            ch.TvgName,
            ch.LogoUrl)).ToList();

        // Append VOD/series from in-memory provider channels (not persisted to DB)
        if (provider.IncludeVod || provider.IncludeSeries)
        {
            foreach (var ch in providerChannels)
            {
                if (string.IsNullOrWhiteSpace(ch.StreamUrl)) continue;
                var contentType = LiveClassifier.ClassifyContent(ch.StreamUrl);
                if (contentType == "vod" && provider.IncludeVod)
                    channels.Add(new ChannelBuildData(string.Empty, ch.ProviderChannelKey, ch.DisplayName, ch.StreamUrl, "vod", ch.GroupTitle, ch.TvgId, ch.TvgName, ch.LogoUrl));
                else if (contentType == "series" && provider.IncludeSeries)
                    channels.Add(new ChannelBuildData(string.Empty, ch.ProviderChannelKey, ch.DisplayName, ch.StreamUrl, "series", ch.GroupTitle, ch.TvgId, ch.TvgName, ch.LogoUrl));
            }
        }

        // Load group filter config for this profile/provider
        var groupFilters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId && x.ProviderGroup.ProviderId == provider.ProviderId)
            .ToListAsync(cancellationToken);

        var includedGroups = groupFilters
            .Where(f => f.Decision != "exclude")
            .ToDictionary(
            f => f.ProviderGroup.RawName,
            f => new GroupFilterConfig(
                f.ProfileGroupFilterId,
                f.OutputName ?? f.ProviderGroup.RawName,
                f.AutoNumStart,
                f.AutoNumEnd,
                f.SortOverride),
            StringComparer.Ordinal);

        // Load per-channel selections — always "select" mode now
        var selectModeFilterIds = includedGroups.Values
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
                    g => g
                        .GroupBy(x => x.ProviderChannel.StreamUrl, StringComparer.Ordinal)
                        .ToDictionary(
                            sg => sg.Key,
                            sg => sg.Select(x => new ChannelOverride(x.OutputGroupName, x.ChannelNumber, x.TvgIdOverride)).First(),
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

        var channelIndexPath = Path.Combine(snapshotDir, "channel_index.ndjson");
        var channelIndexIdxPath = Path.Combine(snapshotDir, "channel_index.idx");
        var xmltvPath = Path.Combine(snapshotDir, "guide.xml");

        await ChannelIndexStore.WriteAsync(channelIndexPath, channelIndexIdxPath, channelIndex, cancellationToken);
        await File.WriteAllTextAsync(xmltvPath, xmltvContent, Encoding.UTF8, cancellationToken);

        int liveCount = 0, vodCount = 0, seriesCount = 0;
        foreach (var e in channelIndex)
        {
            switch (LiveClassifier.ClassifyContent(e.StreamUrl))
            {
                case "vod": vodCount++; break;
                case "series": seriesCount++; break;
                default: liveCount++; break;
            }
        }

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
            LiveChannelCount = liveCount,
            VodChannelCount = vodCount,
            SeriesChannelCount = seriesCount,
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

    internal const int OverflowRangeStart = 9000;

    internal static List<ChannelIndexEntry> BuildChannelIndex(
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

        var pending = new List<(string OutputGroup, ChannelBuildData Channel, int? ExplicitNumber, string? TvgIdOverride)>();

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

                pending.Add((fallbackGroup, channel, null, null));
                continue;
            }

            // Live channels are opt-in via explicit included groups.
            if (!hasGroup || !includedGroups.TryGetValue(groupName!, out var filter))
                continue;

            if (!channelOverridesByFilterId.TryGetValue(filter.ProfileGroupFilterId, out var overrides))
                continue;
            if (!overrides.TryGetValue(channel.StreamUrl ?? string.Empty, out var ov))
                continue;

            var effectiveGroup = string.IsNullOrWhiteSpace(ov.OutputGroupName)
                ? filter.OutputName
                : ov.OutputGroupName;
            pending.Add((effectiveGroup, channel, ov.ChannelNumber, ov.TvgIdOverride));
        }

        var result = new List<ChannelIndexEntry>();

        // Seed the globally-used set with every pinned number so auto-assignment
        // skips them regardless of which group they belong to.
        var assignedNumbers = new HashSet<int>(
            pending
                .Where(x => x.ExplicitNumber.HasValue)
                .Select(x => x.ExplicitNumber!.Value));

        int nextOverflow = OverflowRangeStart;

        // Evaluate output groups in SortOverride order (nulls last), then alphabetical.
        // This determines which group "wins" early numbers when ranges overlap.
        var byOutputGroup = pending
            .GroupBy(x => x.OutputGroup, StringComparer.Ordinal)
            .Select(g =>
            {
                var minSort = includedGroups.Values
                    .Where(f => string.Equals(f.OutputName, g.Key, StringComparison.Ordinal))
                    .Select(f => f.SortOverride)
                    .Where(s => s.HasValue)
                    .Select(s => s!.Value)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();
                return (Group: g, SortKey: minSort);
            })
            .OrderBy(x => x.SortKey)
            .ThenBy(x => x.Group.Key, StringComparer.Ordinal)
            .Select(x => x.Group);

        foreach (var group in byOutputGroup)
        {
            var outputName = group.Key;

            var parentFilter = includedGroups.Values
                .FirstOrDefault(f => string.Equals(f.OutputName, outputName, StringComparison.Ordinal));

            var withNum = group
                .Where(x => x.ExplicitNumber.HasValue)
                .OrderBy(x => x.ExplicitNumber!.Value)
                .ToList();

            var withoutNum = group
                .Where(x => !x.ExplicitNumber.HasValue)
                .OrderBy(x => x.Channel.DisplayName, StringComparer.Ordinal)
                .ThenBy(x => x.Channel.StreamUrl, StringComparer.Ordinal)
                .ToList();

            foreach (var (_, channel, num, tvgIdOverride) in withNum)
                result.Add(BuildEntry(channel, outputName, num, profileId, tvgIdOverride));

            int? nextNum = parentFilter?.AutoNumStart;
            int? maxNum = parentFilter?.AutoNumEnd;
            bool hasRange = nextNum.HasValue;

            foreach (var (_, channel, _, tvgIdOverride) in withoutNum)
            {
                int? assignedNum = null;

                if (nextNum.HasValue)
                {
                    // Skip numbers already claimed by pinned channels or earlier auto-assignments.
                    while (nextNum.HasValue && assignedNumbers.Contains(nextNum.Value))
                    {
                        nextNum++;
                        if (maxNum.HasValue && nextNum > maxNum)
                        {
                            nextNum = null;
                            break;
                        }
                    }

                    if (nextNum.HasValue)
                    {
                        assignedNum = nextNum.Value;
                        assignedNumbers.Add(nextNum.Value);
                        nextNum++;
                        if (maxNum.HasValue && nextNum > maxNum)
                            nextNum = null;
                    }
                }

                // Range was configured but is exhausted — place in overflow rather than
                // silently dropping the channel number.
                if (assignedNum is null && hasRange)
                {
                    while (assignedNumbers.Contains(nextOverflow))
                        nextOverflow++;
                    assignedNum = nextOverflow;
                    assignedNumbers.Add(nextOverflow);
                    nextOverflow++;
                }

                result.Add(BuildEntry(channel, outputName, assignedNum, profileId, tvgIdOverride));
            }
        }

        return result;
    }

    private static ChannelIndexEntry BuildEntry(
        ChannelBuildData channel,
        string? groupTitle,
        int? tvgChno,
        string profileId,
        string? tvgIdOverride = null)
    {
        // Include stream URL + display/group context to avoid collapsing distinct items
        // that share tvg-id/URL across multiple provider groups.
        var stableKey = !string.IsNullOrWhiteSpace(channel.ProviderChannelKey)
            ? $"{channel.ProviderChannelKey}\u001f{channel.StreamUrl}\u001f{groupTitle}\u001f{channel.DisplayName}"
            : $"{channel.DisplayName}\u001f{channel.StreamUrl}\u001f{groupTitle}";

        return new ChannelIndexEntry(
            StreamKey: DeriveStreamKey(stableKey, profileId),
            DisplayName: channel.DisplayName,
            TvgId: string.IsNullOrWhiteSpace(tvgIdOverride) ? channel.TvgId : tvgIdOverride,
            TvgName: channel.TvgName,
            LogoUrl: channel.LogoUrl,
            GroupTitle: groupTitle,
            TvgChno: tvgChno,
            ProviderChannelId: channel.ProviderChannelId,
            StreamUrl: channel.StreamUrl!);
    }

    // -------------------------------------------------------------------------
    // EPG fetch + compile
    // -------------------------------------------------------------------------

    private static readonly string EmptyXmltvDocument =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv generator-info-name=\"M3Undle\"></tv>";

    private async Task<string> FetchAndCompileEpgAsync(
        Provider provider,
        string profileId,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        // Load (or lazy-create) enabled EPG sources for this provider
        var sources = await db.EpgSources
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .ToListAsync(cancellationToken);

        // Lazy backfill: if no sources exist yet, create one from provider.XmltvUrl / Xtream config
        if (sources.Count == 0)
            sources = await BackfillEpgSourceAsync(provider, cancellationToken);

        if (sources.Count == 0)
        {
            logger.LogDebug("No EPG sources configured for provider {ProviderId} — using empty guide.", provider.ProviderId);
            return EmptyXmltvDocument;
        }

        sw.Restart();
        var epgCacheDir = Path.Combine(runtimePaths.DataDirectory, "epg-cache");

        // Fetch all sources in parallel (soft-fail per source)
        var fetchTasks = sources.Select(async source =>
        {
            var startedUtc = DateTime.UtcNow;
            var cacheFile = Path.Combine(epgCacheDir, $"{source.EpgSourceId}.xml");
            var (result, xml) = await epgSourceFetcher.FetchAsync(source, provider, cacheFile, cancellationToken);

            return (Source: source, Result: result, Xml: xml, StartedUtc: startedUtc);
        }).ToList();

        var fetchResults = await Task.WhenAll(fetchTasks);
        logger.LogInformation("EPG sources fetched in {Elapsed}ms ({Count} sources).", sw.ElapsedMilliseconds, sources.Count);
        sw.Restart();

        // Parse each source into a catalogue
        var catalogues = new Dictionary<string, EpgCatalogue>(StringComparer.Ordinal);
        foreach (var (source, result, xml, startedUtc) in fetchResults)
        {
            var catalogue = string.IsNullOrWhiteSpace(xml)
                ? EpgCatalogue.Empty(source.EpgSourceId)
                : xmltvParser.Parse(source.EpgSourceId, xml);

            catalogues[source.EpgSourceId] = catalogue;

            await PersistEpgFetchRunAsync(source, result, catalogue, startedUtc, cancellationToken);

            // Upsert source channels discovered from XMLTV
            if (catalogue.Channels.Count > 0)
                await UpsertEpgSourceChannelsAsync(source.EpgSourceId, catalogue.Channels, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Run auto-mapping (non-blocking on failure)
        try
        {
            await epgChannelMapper.AutoMapAsync(profileId, provider.ProviderId, [.. catalogues.Values], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "EPG auto-mapping failed — continuing without updated mappings.");
        }

        // Load mappings for compile
        var mappings = await db.EpgChannelMappings
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var mappingLookup = mappings.ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        // Build output channel list aligned to the profile's selected output channels where available.
        var configuredChannelIds = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(f => f.ProfileGroupFilter.ProfileId == profileId &&
                        f.ProfileGroupFilter.Decision == "hold")
            .Select(f => f.ProviderChannelId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var dbChannelsQuery = db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "live");

        if (configuredChannelIds.Count > 0)
            dbChannelsQuery = dbChannelsQuery.Where(x => configuredChannelIds.Contains(x.ProviderChannelId));

        var dbChannels = await dbChannelsQuery.ToListAsync(cancellationToken);

        var outputChannels = dbChannels
            .Where(ch => !string.IsNullOrWhiteSpace(ch.TvgId))
            .Select(ch => new OutputChannel(ch.ProviderChannelId, ch.TvgId!, ch.DisplayName, ch.LogoUrl))
            .ToList();

        // Compile
        var (compiledXml, report) = epgCompiler.Compile(outputChannels, sources, catalogues, mappingLookup);

        // "Don't regress" guard: if compiled guide is empty and we have a previous active snapshot,
        // re-use the previous guide rather than publishing an empty one.
        if (report.TotalProgrammes == 0 && outputChannels.Count > 0)
        {
            logger.LogWarning("EPG compile produced 0 programmes — checking for previous guide to carry forward.");
            var prevXmltvPath = await GetPreviousActiveXmltvPathAsync(profileId, cancellationToken);
            if (prevXmltvPath is not null && File.Exists(prevXmltvPath))
            {
                var prevXml = await File.ReadAllTextAsync(prevXmltvPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(prevXml) && prevXml.Contains("<programme"))
                {
                    logger.LogWarning("Carrying forward previous snapshot guide to avoid EPG regression.");
                    return prevXml;
                }
            }
        }

        logger.LogInformation("EPG compiled in {Elapsed}ms.", sw.ElapsedMilliseconds);
        return compiledXml;
    }

    private async Task<List<EpgSource>> BackfillEpgSourceAsync(
        Provider provider,
        CancellationToken cancellationToken)
    {
        // Create a default EpgSource from provider configuration
        var hasProviderXmltv = !string.IsNullOrWhiteSpace(provider.XmltvUrl) ||
                               (provider.XtreamBaseUrl is not null && provider.XtreamIncludeXmltv);

        if (!hasProviderXmltv)
            return [];

        var now = DateTime.UtcNow;
        var isXtream = provider.XtreamBaseUrl is not null;

        var source = new EpgSource
        {
            EpgSourceId = Guid.NewGuid().ToString(),
            ProviderId = provider.ProviderId,
            Name = "Provider XMLTV",
            Kind = isXtream ? "provider_xmltv" : "xmltv_url",
            UrlOrPath = isXtream ? null : provider.XmltvUrl,
            Priority = 1,
            Enabled = true,
            TimeoutSeconds = provider.TimeoutSeconds,
            HeadersJson = isXtream ? null : provider.HeadersJson,
            UserAgent = isXtream ? null : provider.UserAgent,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        db.EpgSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Auto-created EpgSource {EpgSourceId} for provider {ProviderId}.",
            source.EpgSourceId, provider.ProviderId);

        return [source];
    }

    private async Task PersistEpgFetchRunAsync(
        EpgSource source,
        EpgSourceFetcher.FetchResult result,
        EpgCatalogue catalogue,
        DateTime startedUtc,
        CancellationToken cancellationToken)
    {
        var finishedUtc = DateTime.UtcNow;

        // Update source status columns
        var tracked = await db.EpgSources.FindAsync([source.EpgSourceId], cancellationToken);
        if (tracked is not null)
        {
            if (result.Status is "ok" or "not_modified")
            {
                tracked.LastSuccessUtc = finishedUtc;
                tracked.ETag = result.ETag ?? tracked.ETag;
                tracked.LastModifiedUtc = result.LastModifiedUtc ?? tracked.LastModifiedUtc;
            }
            else
            {
                tracked.LastFailureUtc = finishedUtc;
                logger.LogWarning(
                    "EPG source {EpgSourceId} ({Name}) fetch failed: {Error}",
                    source.EpgSourceId, source.Name, result.ErrorSummary ?? "unknown error");
            }
            tracked.UpdatedUtc = finishedUtc;
        }

        var channelCount = catalogue.Channels.Count;
        var programmeCount = catalogue.ProgrammesByChannel.Values.Sum(p => p.Count);

        db.EpgFetchRuns.Add(new EpgFetchRun
        {
            EpgFetchRunId = Guid.NewGuid().ToString(),
            EpgSourceId = source.EpgSourceId,
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
            Status = result.Status,
            Bytes = result.Bytes > 0 ? (int)Math.Min(result.Bytes, int.MaxValue) : null,
            ChannelCount = channelCount > 0 ? channelCount : null,
            ProgrammeCount = programmeCount > 0 ? programmeCount : null,
            ErrorSummary = result.ErrorSummary,
        });
    }

    private async Task UpsertEpgSourceChannelsAsync(
        string epgSourceId,
        IReadOnlyList<EpgChannelRecord> channels,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var existing = await db.EpgSourceChannels
            .Where(x => x.EpgSourceId == epgSourceId)
            .ToListAsync(cancellationToken);

        var byId = existing.ToDictionary(x => x.XmltvChannelId, StringComparer.Ordinal);

        // Track IDs added during this call so XMLTV sources with duplicate channel
        // entries don't trigger a second Add for the same (epg_source_id, xmltv_channel_id).
        var addedThisRun = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ch in channels)
        {
            if (byId.TryGetValue(ch.XmltvChannelId, out var row))
            {
                row.DisplayName = ch.DisplayName;
                row.IconUrl = ch.IconUrl;
                row.LastSeenUtc = now;
            }
            else if (addedThisRun.Add(ch.XmltvChannelId))
            {
                db.EpgSourceChannels.Add(new EpgSourceChannel
                {
                    EpgSourceChannelId = Guid.NewGuid().ToString(),
                    EpgSourceId = epgSourceId,
                    XmltvChannelId = ch.XmltvChannelId,
                    DisplayName = ch.DisplayName,
                    IconUrl = ch.IconUrl,
                    LastSeenUtc = now,
                });
            }
        }

        // Deactivate channels no longer in the source (mark by setting LastSeenUtc far in past is optional;
        // here we just let them persist for UI visibility unless explicitly deleted)
    }

    private async Task<string?> GetPreviousActiveXmltvPathAsync(string profileId, CancellationToken cancellationToken)
    {
        var snap = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return snap?.XmltvPath;
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
                Decision = "hold",
                IsNew = true,
                TrackNewChannels = false,
                CreatedUtc = now,
                UpdatedUtc = now,
            })
            .ToList();

        if (newFilters.Count > 0)
        {
            db.ProfileGroupFilters.AddRange(newFilters);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created {Count} new group filter(s) (hold+new) for profile {ProfileId}.", newFilters.Count, profileId);
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

        // Purge any VOD/series rows from previous runs — only live channels are persisted.
        await db.ProviderChannels
            .Where(x => x.ProviderId == providerId && (x.ContentType == "vod" || x.ContentType == "series"))
            .ExecuteDeleteAsync(cancellationToken);

        var existingChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        var byKey = existingChannels
            .Where(x => x.ProviderChannelKey is not null)
            .ToDictionary(x => x.ProviderChannelKey!, StringComparer.Ordinal);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var occurrenceByStableIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        var toUpdate = new List<ProviderChannel>();
        var newCount = 0;
        var updatedCount = 0;
        var deactivatedCount = 0;

        foreach (var ch in channels)
        {
            if (string.IsNullOrWhiteSpace(ch.DisplayName) || string.IsNullOrWhiteSpace(ch.StreamUrl)) continue;

            // Only live channels are persisted — VOD/series are handled in-memory during snapshot build.
            var contentType = LiveClassifier.ClassifyContent(ch.StreamUrl);
            if (contentType != "live") continue;

            var groupId = ch.GroupTitle is not null && groupNameToId.TryGetValue(ch.GroupTitle, out var gid)
                ? (string?)gid : null;

            // Lazy: skip channels from excluded groups entirely.
            if (groupId is not null && excludedGroupIds.Contains(groupId)) continue;

            var stableIdentity = BuildStableIdentity(ch);
            var occurrence = occurrenceByStableIdentity.GetValueOrDefault(stableIdentity) + 1;
            occurrenceByStableIdentity[stableIdentity] = occurrence;

            var key = DeriveChannelKey(stableIdentity, occurrence);
            if (!seenKeys.Add(key)) continue;

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
                toUpdate.Add(entity);
                updatedCount++;
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
                newCount++;
            }
        }

        foreach (var entity in existingChannels.Where(x => x.ProviderChannelKey is not null && !seenKeys.Contains(x.ProviderChannelKey!)))
        {
            entity.Active = false;
            toUpdate.Add(entity);
            deactivatedCount++;
        }

        // Attach all modified untracked entities explicitly — bypasses EF change detection on large sets.
        if (toUpdate.Count > 0)
            db.ProviderChannels.UpdateRange(toUpdate);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Synced {Count} live channel(s) for provider {ProviderId} ({New} new, {Updated} updated, {Deactivated} deactivated).",
            seenKeys.Count, providerId, newCount, updatedCount, deactivatedCount);
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
