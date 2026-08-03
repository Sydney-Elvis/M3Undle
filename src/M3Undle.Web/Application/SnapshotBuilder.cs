using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using M3Undle.Core.Epg;
using M3Undle.Core.Events;
using M3Undle.Core.M3u;
using M3Undle.Web.Application.Epg;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Observability;
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
    CustomGroupPageService customGroupService,
    IRefreshScheduleService refreshScheduleService,
    IEventService eventService,
    TimeProvider timeProvider,
    ILogger<SnapshotBuilder> logger,
    M3UndleMetrics? metrics = null)
{
    internal sealed record InterestRuleConfig(
        string MatchType,
        string MatchValue,
        string Action);

    internal sealed record GroupFilterConfig(
        string ProfileGroupFilterId,
        string OutputName,
        int? AutoNumStart,
        int? AutoNumEnd,
        int? SortOverride,
        string GroupMode,
        string TrackingPolicy,
        string? TrackingKeywords,
        IReadOnlyList<InterestRuleConfig>? InterestRules = null)
    {
        public GroupFilterConfig(
            string ProfileGroupFilterId,
            string OutputName,
            int? AutoNumStart,
            int? AutoNumEnd,
            int? SortOverride)
            : this(
                ProfileGroupFilterId,
                OutputName,
                AutoNumStart,
                AutoNumEnd,
                SortOverride,
                LineupReviewSemantics.GroupModeManualReview,
                LineupReviewSemantics.TrackingPolicyReview,
                null)
        {
        }

        public GroupFilterConfig(
            string ProfileGroupFilterId,
            string OutputName,
            int? AutoNumStart,
            int? AutoNumEnd,
            int? SortOverride,
            string GroupMode)
            : this(
                ProfileGroupFilterId,
                OutputName,
                AutoNumStart,
                AutoNumEnd,
                SortOverride,
                GroupMode,
                LineupReviewSemantics.TrackingPolicyReview,
                null)
        {
        }
    }
    internal sealed record ChannelBuildData(
        string ProviderChannelId,
        string? ProviderChannelKey,
        string DisplayName,
        string? StreamUrl,
        string ContentType,
        string? GroupTitle,
        string? TvgId,
        string? TvgName,
        string? LogoUrl,
        bool IsEvent = false,
        bool IsPlaceholder = false,
        string? EventContentKey = null,
        string? EventTitle = null,
        string? EventSport = null,
        string? EventLeague = null,
        string? EventParticipantsJson = null);
    internal sealed record ChannelOverride(
        string State,
        string? DisplayNameOverride,
        string? OutputGroupName,
        int? ChannelNumber,
        string? TvgIdOverride)
    {
        public ChannelOverride(string? OutputGroupName, int? ChannelNumber, string? TvgIdOverride)
            : this(LineupReviewSemantics.ChannelStateIncluded, null, OutputGroupName, ChannelNumber, TvgIdOverride)
        {
        }
    }

    private sealed record LineupMetricDeltas(int Added, int Removed, int Renamed, int MappingChanges);

    private sealed record ProviderRefreshLink(
        string ProviderId,
        int Priority,
        bool IsActiveProfile);

    /// <summary>Full refresh: fetch all enabled providers, sync to DB, then build snapshots.</summary>
    public async Task<(bool Succeeded, string? ErrorSummary, IReadOnlyDictionary<string, IReadOnlyList<ParsedProviderChannel>> ChannelsByProvider, string? ChangeClass, IReadOnlySet<string> AffectedProfileIds)> RunAsync(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });

        var providers = await GetOrderedEnabledProvidersAsync(cancellationToken);

        if (providers.Count == 0)
        {
            logger.LogInformation("Snapshot refresh skipped — no enabled providers found.");
            return (false, null, new Dictionary<string, IReadOnlyList<ParsedProviderChannel>>(), null, new HashSet<string>());
        }

        bool anySucceeded = false;
        string? lastErrorSummary = null;
        string? aggregateChangeClass = ChangeClasses.None;
        var channelsByProvider = new Dictionary<string, IReadOnlyList<ParsedProviderChannel>>();
        var aggregateProfileIds = new HashSet<string>();

        var scheduleSettings = await refreshScheduleService.GetActiveProfileSettingsAsync(cancellationToken);
        var defaultIntervalHours = scheduleSettings?.Settings.IntervalHours;

        foreach (var provider in providers)
        {
            var (s, e, channels, cc, profileIds) = await RunForProviderAsync(provider, defaultIntervalHours, cancellationToken);
            if (s)
            {
                anySucceeded = true;
                await PublishProviderBackOnlineIfNeededAsync(provider, cancellationToken);
            }
            if (e is not null) lastErrorSummary = e;
            if (channels.Count > 0) channelsByProvider[provider.ProviderId] = channels;
            aggregateChangeClass = SnapshotChangeClassifier.Aggregate(aggregateChangeClass, cc);
            aggregateProfileIds.UnionWith(profileIds);
        }

        return (anySucceeded, lastErrorSummary, channelsByProvider, aggregateChangeClass, aggregateProfileIds);
    }

    /// <summary>Build snapshots from cached channels for all enabled providers — no re-fetch.</summary>
    public async Task<(bool Succeeded, string? ErrorSummary, string? ChangeClass, IReadOnlySet<string> AffectedProfileIds)> BuildOnlyAsync(
        IReadOnlyDictionary<string, IReadOnlyList<ParsedProviderChannel>> channelsByProvider,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });

        var providers = await GetOrderedEnabledProvidersAsync(cancellationToken);

        if (providers.Count == 0)
        {
            logger.LogInformation("Snapshot build skipped — no enabled providers found.");
            return (false, null, null, new HashSet<string>());
        }

        bool anySucceeded = false;
        string? lastErrorSummary = null;
        string? aggregateChangeClass = ChangeClasses.None;
        var aggregateProfileIds = new HashSet<string>();

        foreach (var provider in providers)
        {
            channelsByProvider.TryGetValue(provider.ProviderId, out var cachedChannels);
            var (s, e, cc, profileIds) = await BuildOnlyForProviderAsync(provider, cachedChannels ?? [], cancellationToken);
            if (s) anySucceeded = true;
            if (e is not null) lastErrorSummary = e;
            aggregateChangeClass = SnapshotChangeClassifier.Aggregate(aggregateChangeClass, cc);
            aggregateProfileIds.UnionWith(profileIds);
        }

        return (anySucceeded, lastErrorSummary, aggregateChangeClass, aggregateProfileIds);
    }

    private async Task<List<Provider>> GetOrderedEnabledProvidersAsync(CancellationToken cancellationToken)
    {
        var providers = await db.Providers
            .AsNoTracking()
            .Where(x => x.Enabled)
            .ToListAsync(cancellationToken);

        if (providers.Count <= 1)
            return providers;

        var links = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.Enabled && x.Provider.Enabled && x.Profile.Enabled)
            .Select(x => new ProviderRefreshLink(x.ProviderId, x.Priority, x.Profile.IsActive))
            .ToListAsync(cancellationToken);

        var linkLookup = links.ToLookup(x => x.ProviderId, StringComparer.Ordinal);

        return providers
            .OrderBy(provider => GetProviderRefreshRank(linkLookup[provider.ProviderId]))
            .ThenBy(provider => GetProviderRefreshPriority(linkLookup[provider.ProviderId]))
            .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .ToList();
    }

    private static int GetProviderRefreshRank(IEnumerable<ProviderRefreshLink> links)
    {
        var materialized = links as IReadOnlyCollection<ProviderRefreshLink> ?? links.ToArray();
        if (materialized.Any(x => x.IsActiveProfile))
            return 0;
        if (materialized.Count > 0)
            return 1;
        return 2;
    }

    private static int GetProviderRefreshPriority(IEnumerable<ProviderRefreshLink> links)
    {
        var priorities = links.Select(x => x.Priority);
        return priorities.Any() ? priorities.Min() : int.MaxValue;
    }

    private async Task<(bool Succeeded, string? ErrorSummary, IReadOnlyList<ParsedProviderChannel> Channels, string? ChangeClass, IReadOnlySet<string> AffectedProfileIds)> RunForProviderAsync(
        Provider provider, int? globalIntervalHours, CancellationToken cancellationToken)
    {
        // 1. Find all enabled profiles linked to this provider, ordered by priority
        var allProfileLinks = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .ToListAsync(cancellationToken);

        if (allProfileLinks.Count == 0)
        {
            logger.LogInformation("Snapshot refresh skipped — provider {ProviderId} is not linked to any enabled profile.", provider.ProviderId);
            return (false, null, [], null, new HashSet<string>());
        }

        var enabledProfileIds = await db.Profiles
            .AsNoTracking()
            .Where(x => allProfileLinks.Select(l => l.ProfileId).Contains(x.ProfileId) && x.Enabled)
            .Select(x => x.ProfileId)
            .ToHashSetAsync(cancellationToken);

        var activeLinks = allProfileLinks.Where(l => enabledProfileIds.Contains(l.ProfileId)).ToList();
        if (activeLinks.Count == 0)
        {
            logger.LogInformation("Snapshot refresh skipped — no enabled profiles linked to provider {ProviderId}.", provider.ProviderId);
            return (false, null, [], null, new HashSet<string>());
        }

        var initialProfileProviderSyncProfileIds = await GetInitialProfileProviderSyncProfileIdsAsync(
            provider.ProviderId,
            activeLinks.Select(l => l.ProfileId).ToList(),
            cancellationToken);

        var profileId = activeLinks[0].ProfileId;

        // 2. Create FetchRun pre-saved as "running" (crash leaves it as "running", not "fail")
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

        logger.LogInformation("Starting snapshot refresh for provider {ProviderId}, {ProfileCount} profile(s).", provider.ProviderId, activeLinks.Count);

        // 3. Fetch playlist — failure is fatal (preserve last-known-good)
        PlaylistFetchResult playlistResult;
        var sw = Stopwatch.StartNew();
        try
        {
            playlistResult = await fetcher.FetchPlaylistAsync(provider, cancellationToken);
        }
        catch (Exception ex) when (ex is ProviderFetchException or ProviderParseException or OperationCanceledException)
        {
            logger.LogWarning(ex, "Playlist fetch/parse failed for provider \"{ProviderName}\" after {Elapsed}ms.", provider.Name, sw.ElapsedMilliseconds);
            metrics?.RecordProviderRefresh(provider.ProviderId, success: false, sw.Elapsed);
            await FailFetchRunAsync(fetchRun, ex.Message);
            await PublishSystemEventBestEffortAsync(
                SystemEventSeverity.Error,
                SystemEventTypes.ProviderFetchFailed,
                $"Provider '{provider.Name}' fetch failed",
                ex.Message,
                providerId: provider.ProviderId);
            return (false, ex.Message, [], null, new HashSet<string>());
        }

        logger.LogInformation("Playlist fetched in {Elapsed}ms — {ChannelCount} channels for provider {ProviderId}.",
            sw.ElapsedMilliseconds, playlistResult.Channels.Count, provider.ProviderId);
        metrics?.RecordProviderRefresh(provider.ProviderId, success: true, sw.Elapsed);
        sw.Restart();

        // 4. Probe Xtream API for capability/expiry (non-fatal, updates fields in place)
        if (provider.XtreamBaseUrl is null)
            await UpdateXtreamCapabilityAsync(provider, cancellationToken);
        else
            await UpdateXtreamExpiryAsync(provider, playlistResult.AccountInfo, cancellationToken);

        // 5. EPG fetch + DB sync
        string xmltvContent;
        long xmltvBytes = 0;
        var stage = "xmltv";
        try
        {
            xmltvContent = await FetchAndCompileEpgAsync(provider, profileId, sw, globalIntervalHours, cancellationToken);
            xmltvBytes = Encoding.UTF8.GetByteCount(xmltvContent);
            cancellationToken.ThrowIfCancellationRequested();
            sw.Restart();

            stage = "groups";
            var groupNameToId = await SyncProviderGroupsAsync(provider.ProviderId, playlistResult.Channels, now, cancellationToken);
            logger.LogInformation("Groups synced in {Elapsed}ms for provider {ProviderId}.", sw.ElapsedMilliseconds, provider.ProviderId);
            sw.Restart();

            stage = "group-filters";
            foreach (var link in activeLinks)
            {
                var isInitialProfileProviderSync = initialProfileProviderSyncProfileIds.Contains(link.ProfileId);
                await SyncGroupFiltersAsync(link.ProfileId, provider.ProviderId, !isInitialProfileProviderSync, cancellationToken);
            }
            logger.LogInformation("Group filters synced in {Elapsed}ms for {Count} profile(s), provider {ProviderId}.", sw.ElapsedMilliseconds, activeLinks.Count, provider.ProviderId);
            sw.Restart();

            stage = "channels";
            await SyncProviderChannelsAsync(profileId, provider.ProviderId, fetchRun.FetchRunId, playlistResult.Channels, groupNameToId, now, cancellationToken);
            logger.LogInformation("Channels synced in {Elapsed}ms for provider {ProviderId}.", sw.ElapsedMilliseconds, provider.ProviderId);
            sw.Restart();

            stage = "pending-review";
            foreach (var link in activeLinks)
            {
                var isInitialProfileProviderSync = initialProfileProviderSyncProfileIds.Contains(link.ProfileId);
                await SyncPendingChannelReviewsAsync(link.ProfileId, provider.ProviderId, now, !isInitialProfileProviderSync, cancellationToken);
            }
            logger.LogInformation("Pending channel review sync completed in {Elapsed}ms for {Count} profile(s), provider {ProviderId}.", sw.ElapsedMilliseconds, activeLinks.Count, provider.ProviderId);
            sw.Restart();

            stage = "custom-group-sync";
            foreach (var link in activeLinks)
                await customGroupService.SyncPendingChannelReviewsAsync(link.ProfileId, provider.ProviderId, now, cancellationToken);
            logger.LogInformation("Custom group sync completed in {Elapsed}ms for {Count} profile(s), provider {ProviderId}.", sw.ElapsedMilliseconds, activeLinks.Count, provider.ProviderId);
            sw.Restart();
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Snapshot refresh timed out during stage '{Stage}' for provider {ProviderId}.", stage, provider.ProviderId);
            throw;
        }

        fetchRun.FinishedUtc = DateTime.UtcNow;
        fetchRun.Status = "ok";
        fetchRun.ChannelCountSeen = playlistResult.Channels.Count;
        fetchRun.PlaylistBytes = (int)Math.Min(playlistResult.Bytes, int.MaxValue);
        fetchRun.XmltvBytes = (int)Math.Min(xmltvBytes, int.MaxValue);
        await db.SaveChangesAsync(cancellationToken);

        // 6. Build snapshot for each linked profile
        bool anySucceeded = false;
        string? lastErrorSummary = null;
        string? aggregateChangeClass = ChangeClasses.None;
        foreach (var link in activeLinks)
        {
            var (s, e, cc) = await BuildSnapshotFromDbAsync(provider, link.ProfileId, xmltvContent, playlistResult.Channels, cancellationToken);
            if (s) anySucceeded = true;
            if (e is not null) lastErrorSummary = e;
            aggregateChangeClass = SnapshotChangeClassifier.Aggregate(aggregateChangeClass, cc);
        }

        var builtProfileIds = activeLinks.Select(l => l.ProfileId).ToHashSet();
        return (anySucceeded, lastErrorSummary, playlistResult.Channels, aggregateChangeClass, builtProfileIds);
    }

    private async Task<(bool Succeeded, string? ErrorSummary, string? ChangeClass, IReadOnlySet<string> AffectedProfileIds)> BuildOnlyForProviderAsync(
        Provider provider, IReadOnlyList<ParsedProviderChannel> providerChannels, CancellationToken cancellationToken)
    {
        var allProfileLinks = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Enabled)
            .OrderBy(x => x.Priority)
            .ToListAsync(cancellationToken);

        if (allProfileLinks.Count == 0)
        {
            logger.LogInformation("Snapshot build skipped — provider {ProviderId} has no enabled profile link.", provider.ProviderId);
            return (false, null, null, new HashSet<string>());
        }

        var enabledProfileIds = await db.Profiles
            .AsNoTracking()
            .Where(x => allProfileLinks.Select(l => l.ProfileId).Contains(x.ProfileId) && x.Enabled)
            .Select(x => x.ProfileId)
            .ToHashSetAsync(cancellationToken);

        var activeLinks = allProfileLinks.Where(l => enabledProfileIds.Contains(l.ProfileId)).ToList();
        if (activeLinks.Count == 0)
        {
            logger.LogInformation("Snapshot build skipped — no enabled profiles linked to provider {ProviderId}.", provider.ProviderId);
            return (false, null, null, new HashSet<string>());
        }

        bool anySucceeded = false;
        string? lastErrorSummary = null;
        string? aggregateChangeClass = ChangeClasses.None;

        var scheduleSettings = await refreshScheduleService.GetActiveProfileSettingsAsync(cancellationToken);
        var defaultIntervalHours = scheduleSettings?.Settings.IntervalHours;

        foreach (var link in activeLinks)
        {
            var xmltvContent = EmptyXmltvDocument;
            var latestSnapshot = await db.Snapshots
                .AsNoTracking()
                .Where(x => x.ProfileId == link.ProfileId && x.Status == "active")
                .OrderByDescending(x => x.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (latestSnapshot is not null && !string.IsNullOrEmpty(latestSnapshot.XmltvPath) && File.Exists(latestSnapshot.XmltvPath))
                xmltvContent = await File.ReadAllTextAsync(latestSnapshot.XmltvPath, cancellationToken);

            // If the carried-forward guide has no programme data, recompile from cached EPG sources.
            // Covers the case where a prior empty-guide snapshot is carried forward after a selection change.
            if (!xmltvContent.Contains("<programme", StringComparison.Ordinal))
            {
                logger.LogInformation("Carried-forward guide is empty — recompiling EPG from cache for provider {ProviderId}, profile {ProfileId}.", provider.ProviderId, link.ProfileId);
                xmltvContent = await FetchAndCompileEpgAsync(provider, link.ProfileId, Stopwatch.StartNew(), defaultIntervalHours, cancellationToken);
            }

            logger.LogInformation("Starting snapshot build-only for provider {ProviderId}, profile {ProfileId}.", provider.ProviderId, link.ProfileId);

            var (s, e, cc) = await BuildSnapshotFromDbAsync(provider, link.ProfileId, xmltvContent, providerChannels, cancellationToken);
            if (s) anySucceeded = true;
            if (e is not null) lastErrorSummary = e;
            aggregateChangeClass = SnapshotChangeClassifier.Aggregate(aggregateChangeClass, cc);
        }

        var builtProfileIds = activeLinks.Select(l => l.ProfileId).ToHashSet();
        return (anySucceeded, lastErrorSummary, aggregateChangeClass, builtProfileIds);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task UpdateXtreamCapabilityAsync(Provider provider, CancellationToken cancellationToken)
    {
        var info = await fetcher.TryProbeXtreamAsync(provider, cancellationToken);
        var capable = info is not null;
        var newExpiry = info is not null ? info.ExpiresUtc : provider.PlaylistExpiresUtc;

        var capableChanged = capable != provider.XtreamDetectedCapable;
        var expiryChanged = newExpiry != provider.PlaylistExpiresUtc;
        if (!capableChanged && !expiryChanged)
            return;

        await db.Providers
            .Where(p => p.ProviderId == provider.ProviderId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.XtreamDetectedCapable, capable)
                .SetProperty(p => p.PlaylistExpiresUtc, newExpiry),
                CancellationToken.None);

        if (capableChanged)
        {
            if (capable)
                logger.LogInformation(
                    "Xtream API capability detected for provider {ProviderId} (expires: {Expires}, status: {Status}, maxConn: {MaxConn}).",
                    provider.ProviderId,
                    info!.ExpiresUtc?.ToString("yyyy-MM-dd") ?? "unknown",
                    info.Status ?? "unknown",
                    info.MaxConnections?.ToString() ?? "unknown");
            else
                logger.LogInformation("Xtream API capability no longer detected for provider {ProviderId}.", provider.ProviderId);
        }
    }

    private async Task<HashSet<string>> GetInitialProfileProviderSyncProfileIdsAsync(
        string providerId,
        IReadOnlyCollection<string> profileIds,
        CancellationToken cancellationToken)
    {
        if (profileIds.Count == 0)
            return [];

        var profileIdsWithExistingFilters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Where(x => profileIds.Contains(x.ProfileId)
                        && x.ProviderGroup.ProviderId == providerId)
            .Select(x => x.ProfileId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var result = profileIds.ToHashSet(StringComparer.Ordinal);
        result.ExceptWith(profileIdsWithExistingFilters);
        return result;
    }

    private async Task UpdateXtreamExpiryAsync(Provider provider, XtreamAccountInfo? accountInfo, CancellationToken cancellationToken)
    {
        // Use AccountInfo carried from the lineup fetch; fall back to a dedicated probe only when not available.
        var info = accountInfo ?? await fetcher.TryProbeXtreamAsync(provider, cancellationToken);
        if (info is null || info.ExpiresUtc == provider.PlaylistExpiresUtc)
            return;

        await db.Providers
            .Where(p => p.ProviderId == provider.ProviderId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.PlaylistExpiresUtc, info.ExpiresUtc),
                CancellationToken.None);
    }

    private async Task<(bool Succeeded, string? ErrorSummary, string? ChangeClass)> BuildSnapshotFromDbAsync(
        Provider provider,
        string profileId,
        string xmltvContent,
        IReadOnlyList<ParsedProviderChannel> providerChannels,
        CancellationToken cancellationToken)
    {
        // Load the current active snapshot for this profile so we can classify the change
        var prevSnapshot = await db.Snapshots
            .AsNoTracking()
            .Where(s => s.ProfileId == profileId && s.Status == "active")
            .OrderByDescending(s => s.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

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
            ch.LogoUrl,
            ch.IsEvent,
            ch.IsPlaceholder,
            ch.EventContentKey,
            ch.EventTitle,
            ch.EventSport,
            ch.EventLeague,
            ch.EventParticipantsJson)).ToList();

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
            .Where(x => x.ProfileId == profileId
                        && x.ProviderGroup.ProviderId == provider.ProviderId
                        && (x.ProviderGroup.ContentType == "live" || x.ProviderGroup.ContentType == "mixed"))
            .ToListAsync(cancellationToken);

        // Load structured event interest rules for this profile (profile-wide + group-scoped)
        var allInterestRules = await db.ProfileEventInterestRules
            .AsNoTracking()
            .Where(r => r.ProfileId == profileId && r.Enabled)
            .OrderBy(r => r.Priority)
            .ToListAsync(cancellationToken);

        // Keyed by raw name, which is only unique because group filters exist for live groups
        // alone — a name may now also exist as separate VOD/series rows. If catalog filtering
        // is ever added to this same table, this key must become (raw name, content type) or
        // it will throw on duplicates.
        var includedGroups = groupFilters
            .Where(f => LineupReviewSemantics.IsGroupIncluded(f.Decision))
            .ToDictionary(
            f => f.ProviderGroup.RawName,
            f =>
            {
                // Include profile-wide rules + rules scoped to this specific group
                var groupRules = allInterestRules
                    .Where(r => r.ProviderGroupId is null || r.ProviderGroupId == f.ProviderGroupId)
                    .Select(r => new InterestRuleConfig(r.MatchType, r.MatchValue, r.Action))
                    .ToList();

                return new GroupFilterConfig(
                    f.ProfileGroupFilterId,
                    f.OutputName ?? f.ProviderGroup.RawName,
                    f.AutoNumStart,
                    f.AutoNumEnd,
                    f.SortOverride,
                    LineupReviewSemantics.NormalizeGroupMode(f.ChannelMode),
                    LineupReviewSemantics.NormalizeTrackingPolicy(f.TrackingPolicy),
                    f.TrackingKeywords,
                    groupRules);
            },
            StringComparer.Ordinal);

        // Load per-channel overrides/state for all included filters.
        var includedFilterIds = includedGroups.Values
            .Select(g => g.ProfileGroupFilterId)
            .ToList();

        Dictionary<string, Dictionary<string, ChannelOverride>> channelOverridesByFilterId = [];
        if (includedFilterIds.Count > 0)
        {
            var selections = await db.ProfileGroupChannelFilters
                .AsNoTracking()
                .Include(x => x.ProviderChannel)
                .Where(x => includedFilterIds.Contains(x.ProfileGroupFilterId))
                .ToListAsync(cancellationToken);

            channelOverridesByFilterId = selections
                .GroupBy(x => x.ProfileGroupFilterId)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .Where(x => !string.IsNullOrWhiteSpace(x.ProviderChannel.StreamUrl))
                        .GroupBy(x => x.ProviderChannel.StreamUrl, StringComparer.Ordinal)
                        .ToDictionary(
                            sg => sg.Key!,
                            sg => sg.Select(x => new ChannelOverride(
                                LineupReviewSemantics.NormalizeChannelState(x.State),
                                x.DisplayNameOverride,
                                x.OutputGroupName,
                                x.ChannelNumber,
                                x.TvgIdOverride)).First(),
                            StringComparer.Ordinal));
        }

        // Load included custom groups for this profile and append their channels
        var customGroups = await db.ProfileCustomGroups
            .AsNoTracking()
            .Include(x => x.Channels).ThenInclude(c => c.ProviderChannel)
            .Where(x => x.ProfileId == profileId
                        && x.Decision == LineupReviewSemantics.GroupDecisionInclude)
            .ToListAsync(cancellationToken);

        // Use a separate key prefix so custom group output names never collide with provider group raw names
        const string CustomGroupKeyPrefix = "\u0002cg\u001f";
        var customGroupOverrides = new Dictionary<string, Dictionary<string, ChannelOverride>>(StringComparer.Ordinal);

        foreach (var cg in customGroups)
        {
            var cgKey = CustomGroupKeyPrefix + cg.CustomGroupId;
            var mode = LineupReviewSemantics.NormalizeGroupMode(cg.ChannelMode);
            var policy = LineupReviewSemantics.NormalizeTrackingPolicy(cg.TrackingPolicy);

            if (!includedGroups.ContainsKey(cgKey))
            {
                includedGroups[cgKey] = new GroupFilterConfig(
                    cg.CustomGroupId,
                    cg.Name,
                    cg.AutoNumStart,
                    cg.AutoNumEnd,
                    cg.SortOverride,
                    mode,
                    policy,
                    cg.TrackingKeywords);
            }

            var overrideMap = new Dictionary<string, ChannelOverride>(StringComparer.Ordinal);
            foreach (var row in cg.Channels)
            {
                var ch = row.ProviderChannel;
                if (string.IsNullOrWhiteSpace(ch.StreamUrl))
                    continue;
                overrideMap[ch.StreamUrl] = new ChannelOverride(
                    LineupReviewSemantics.NormalizeChannelState(row.State),
                    row.DisplayNameOverride,
                    null,
                    row.ChannelNumber,
                    row.TvgIdOverride);

                // Inject the provider channel as a build-data entry keyed under the custom group name
                if (!channels.Any(c => c.ProviderChannelId == ch.ProviderChannelId && c.GroupTitle == cgKey))
                {
                    channels.Add(new ChannelBuildData(
                        ch.ProviderChannelId,
                        ch.ProviderChannelKey,
                        ch.DisplayName,
                        ch.StreamUrl,
                        ch.ContentType,
                        cgKey,
                        ch.TvgId,
                        ch.TvgName,
                        ch.LogoUrl,
                        ch.IsEvent,
                        ch.IsPlaceholder,
                        ch.EventContentKey,
                        ch.EventTitle,
                        ch.EventSport,
                        ch.EventLeague,
                        ch.EventParticipantsJson));
                }
            }
            customGroupOverrides[cg.CustomGroupId] = overrideMap;
        }

        foreach (var (cgId, overrideMap) in customGroupOverrides)
            channelOverridesByFilterId[cgId] = overrideMap;

        var channelIndex = BuildChannelIndex(
            channels,
            profileId,
            includedGroups,
            channelOverridesByFilterId,
            provider.IncludeVod,
            provider.IncludeSeries,
            provider.ProviderId);

        // Write snapshot files
        var snapshotId = Guid.NewGuid().ToString();
        var snapshotDir = GetSnapshotDir(snapshotId);
        Directory.CreateDirectory(snapshotDir);

        var channelIndexPath = Path.Combine(snapshotDir, "channel_index.ndjson");
        var channelIndexIdxPath = Path.Combine(snapshotDir, "channel_index.idx");
        var xmltvPath = Path.Combine(snapshotDir, "guide.xml");

        await ChannelIndexStore.WriteAsync(channelIndexPath, channelIndexIdxPath, channelIndex, cancellationToken);
        await File.WriteAllTextAsync(xmltvPath, xmltvContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        int liveCount = 0, vodCount = 0;
        var seriesNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in channelIndex)
        {
            switch (LiveClassifier.ClassifyContent(e.StreamUrl))
            {
                case "vod": vodCount++; break;
                case "series": seriesNames.Add(ExtractSeriesName(e.DisplayName)); break;
                default: liveCount++; break;
            }
        }
        int seriesCount = seriesNames.Count;

        // Classify the change against the previous active snapshot
        var changeClass = await SnapshotChangeClassifier.ClassifyAsync(prevSnapshot, channelIndexPath, xmltvPath, cancellationToken);

        if (prevSnapshot is not null && changeClass == ChangeClasses.None)
        {
            try
            {
                if (Directory.Exists(snapshotDir))
                    Directory.Delete(snapshotDir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete no-op snapshot directory {Dir}.", snapshotDir);
            }

            logger.LogInformation(
                "No published change detected for profile {ProfileId} — keeping existing active snapshot {SnapshotId}.",
                profileId, prevSnapshot.SnapshotId);

            return (true, null, changeClass);
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
            ChangeClass = changeClass,
        };
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);

        var lineupDeltas = await ComputeLineupMetricDeltasAsync(prevSnapshot, channelIndex, cancellationToken);

        await PromoteSnapshotAsync(snapshot, profileId, cancellationToken);
        await PurgeOldSnapshotsAsync(profileId, cancellationToken);
        metrics?.RecordLineupPublish(
            profileId,
            lineupDeltas.Added,
            lineupDeltas.Removed,
            lineupDeltas.Renamed,
            lineupDeltas.MappingChanges);

        using var snapshotScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Snapshot" });
        logger.LogInformation(
            "Snapshot {SnapshotId} promoted to active — {ChannelCount} channels, change={ChangeClass}.",
            snapshotId, channelIndex.Count, changeClass ?? "first_run");

        return (true, null, changeClass);
    }

    internal const int OverflowRangeStart = 9000;

    private static readonly Regex SeriesNameRegex =
        new(@"^(.*?)\s+[Ss](\d{1,2})[\s\-]*[Ee]?(\d+)", RegexOptions.Compiled);

    private static string ExtractSeriesName(string displayName)
    {
        var m = SeriesNameRegex.Match(displayName);
        return m.Success ? m.Groups[1].Value.Trim() : displayName;
    }

    internal static List<ChannelIndexEntry> BuildChannelIndex(
        IReadOnlyList<ChannelBuildData> channels,
        string profileId,
        IReadOnlyDictionary<string, GroupFilterConfig> includedGroups,
        IReadOnlyDictionary<string, Dictionary<string, ChannelOverride>> channelOverridesByFilterId,
        bool includeVod,
        bool includeSeries,
        string? providerId = null)
    {
        // No included live groups and no VOD/series passthrough enabled.
        if (includedGroups.Count == 0 && !includeVod && !includeSeries)
            return [];

        var pending = new List<(string OutputGroup, ChannelBuildData Channel, int? ExplicitNumber, string? TvgIdOverride, string? DisplayNameOverride)>();

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

                pending.Add((fallbackGroup, channel, null, null, null));
                continue;
            }

            if (!hasGroup || !includedGroups.TryGetValue(groupName!, out var filter))
                continue;

            channelOverridesByFilterId.TryGetValue(filter.ProfileGroupFilterId, out var overrides);
            ChannelOverride? ov = null;
            var hasOverride = overrides is not null
                              && overrides.TryGetValue(channel.StreamUrl ?? string.Empty, out ov);

            if (!ShouldIncludeLiveChannel(channel, filter, hasOverride, ov))
                continue;

            var effectiveGroup = hasOverride && ov is not null && !string.IsNullOrWhiteSpace(ov.OutputGroupName)
                ? ov.OutputGroupName
                : filter.OutputName;
            var effectiveNumber = hasOverride && ov is not null ? ov.ChannelNumber : null;
            var effectiveTvgId = hasOverride && ov is not null ? ov.TvgIdOverride : null;
            var effectiveDisplayName = hasOverride && ov is not null ? ov.DisplayNameOverride : null;

            pending.Add((effectiveGroup, channel, effectiveNumber, effectiveTvgId, effectiveDisplayName));
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

            foreach (var (_, channel, num, tvgIdOverride, displayNameOverride) in withNum)
                result.Add(BuildEntry(channel, outputName, num, profileId, providerId, tvgIdOverride, displayNameOverride));

            int? nextNum = parentFilter?.AutoNumStart;
            int? maxNum = parentFilter?.AutoNumEnd;
            bool hasRange = nextNum.HasValue;

            foreach (var (_, channel, _, tvgIdOverride, displayNameOverride) in withoutNum)
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

                result.Add(BuildEntry(channel, outputName, assignedNum, profileId, providerId, tvgIdOverride, displayNameOverride));
            }
        }

        return result;
    }

    private static bool ShouldIncludeLiveChannel(
        ChannelBuildData channel,
        GroupFilterConfig filter,
        bool hasOverride,
        ChannelOverride? ov)
    {
        if (channel.IsPlaceholder)
            return false;

        if (hasOverride && ov is not null && ov.State == LineupReviewSemantics.ChannelStateExcluded)
            return false;

        if (filter.GroupMode == LineupReviewSemantics.GroupModeManualReview)
            return hasOverride && ov is not null && ov.State == LineupReviewSemantics.ChannelStateIncluded;

        // Explicit include always wins in auto-update mode.
        if (hasOverride && ov is not null && ov.State == LineupReviewSemantics.ChannelStateIncluded)
            return true;

        // Pending channels in auto-update mode are not yet approved — exclude.
        if (hasOverride && ov is not null && ov.State == LineupReviewSemantics.ChannelStatePending)
            return false;

        // Non-event channels preserve the existing auto-update behavior.
        if (!channel.IsEvent && string.IsNullOrWhiteSpace(channel.EventContentKey))
            return true;

        if (LineupReviewSemantics.ShouldAutoAddAll(filter.TrackingPolicy))
            return true;

        if (LineupReviewSemantics.ShouldAutoAddPopulated(filter.TrackingPolicy))
            return !string.IsNullOrWhiteSpace(channel.EventContentKey);

        if (LineupReviewSemantics.ShouldAutoAddMatching(filter.TrackingPolicy))
        {
            // First check structured interest rules (suppress takes precedence, then auto_add)
            if (filter.InterestRules is { Count: > 0 })
            {
                string?[] candidateTexts = [channel.DisplayName, channel.GroupTitle, channel.EventContentKey, channel.EventTitle, channel.EventSport, channel.EventLeague, channel.EventParticipantsJson];
                foreach (var rule in filter.InterestRules)
                {
                    if (!LineupReviewSemantics.InterestRuleMatches(rule.MatchValue, candidateTexts))
                        continue;

                    if (rule.Action == LineupReviewSemantics.InterestActionSuppress)
                        return false;
                    if (rule.Action == LineupReviewSemantics.InterestActionAutoAdd)
                        return true;
                    // notify — surface but don't auto-add
                }
            }

            // Fall back to free-text keyword matching
            return LineupReviewSemantics.MatchesTrackingKeywords(
                filter.TrackingKeywords,
                channel.DisplayName,
                channel.GroupTitle,
                channel.EventContentKey);
        }

        // Event channels with review/notify policies are intentionally not auto-added.
        return false;
    }

    private static ChannelIndexEntry BuildEntry(
        ChannelBuildData channel,
        string? groupTitle,
        int? tvgChno,
        string profileId,
        string? providerId,
        string? tvgIdOverride = null,
        string? displayNameOverride = null)
    {
        // Include stream URL + display/group context to avoid collapsing distinct items
        // that share tvg-id/URL across multiple provider groups.
        var stableKey = !string.IsNullOrWhiteSpace(channel.ProviderChannelKey)
            ? $"{channel.ProviderChannelKey}\u001f{channel.StreamUrl}\u001f{groupTitle}\u001f{channel.DisplayName}"
            : $"{channel.DisplayName}\u001f{channel.StreamUrl}\u001f{groupTitle}";

        return new ChannelIndexEntry(
            StreamKey: DeriveStreamKey(stableKey, profileId),
            DisplayName: string.IsNullOrWhiteSpace(displayNameOverride) ? channel.DisplayName : displayNameOverride,
            TvgId: string.IsNullOrWhiteSpace(tvgIdOverride) ? channel.TvgId : tvgIdOverride,
            TvgName: channel.TvgName,
            LogoUrl: channel.LogoUrl,
            GroupTitle: groupTitle,
            TvgChno: tvgChno,
            ProviderChannelId: channel.ProviderChannelId,
            StreamUrl: channel.StreamUrl!,
            ProviderId: providerId);
    }

    // -------------------------------------------------------------------------
    // EPG cadence predicate (internal for unit testing)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when the cached EPG data for a source is still fresh and the
    /// network fetch can be skipped.  The cache is fresh when the time since the last
    /// successful fetch is less than <paramref name="intervalHours"/>.
    /// </summary>
    internal static bool IsEpgCacheFresh(DateTime? lastSuccessUtc, int intervalHours, DateTimeOffset utcNow)
        => lastSuccessUtc.HasValue
           && (utcNow.UtcDateTime - lastSuccessUtc.Value).TotalHours < intervalHours;

    // -------------------------------------------------------------------------
    // EPG fetch + compile
    // -------------------------------------------------------------------------

    private static readonly string EmptyXmltvDocument =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><tv generator-info-name=\"M3Undle\"></tv>";

    private async Task<string> FetchAndCompileEpgAsync(
        Provider provider,
        string profileId,
        Stopwatch sw,
        int? globalIntervalHours,
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
            var utcNow = timeProvider.GetUtcNow();
            var startedUtc = utcNow.UtcDateTime;
            var cacheFile = Path.Combine(epgCacheDir, $"{source.EpgSourceId}.xml");

            // Per-source cadence check: skip network fetch if still fresh
            var effectiveInterval = source.RefreshIntervalHours ?? globalIntervalHours;
            if (effectiveInterval.HasValue
                && IsEpgCacheFresh(source.LastSuccessUtc, effectiveInterval.Value, utcNow)
                && File.Exists(cacheFile))
            {
                logger.LogDebug(
                    "EPG source {EpgSourceId} ({Name}): cadence not elapsed ({H}h since last success {Last:u}) — using cached data.",
                    source.EpgSourceId, source.Name, effectiveInterval.Value, source.LastSuccessUtc!.Value);
                var cachedXml = await File.ReadAllTextAsync(cacheFile, cancellationToken);
                var cachedResult = new EpgSourceFetcher.FetchResult(
                    null, "not_modified", 0, source.ETag, source.LastModifiedUtc, null);
                return (Source: source, Result: cachedResult, Xml: (string?)cachedXml, StartedUtc: startedUtc);
            }

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

        // Build output channel list aligned to the same select-mode stream URL rules used
        // by BuildChannelIndex for live channel publishing.
        var liveChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == provider.ProviderId && x.Active && x.ContentType == "live")
            .ToListAsync(cancellationToken);

        var includedFilters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId
                        && x.ProviderGroup.ProviderId == provider.ProviderId
                        && (x.ProviderGroup.ContentType == "live" || x.ProviderGroup.ContentType == "mixed"))
            .ToListAsync(cancellationToken);

        var includedFilterLookup = includedFilters
            .Where(f => LineupReviewSemantics.IsGroupIncluded(f.Decision))
            .ToDictionary(
                f => f.ProviderGroup.RawName,
                f => new GroupFilterConfig(
                    f.ProfileGroupFilterId,
                    f.OutputName ?? f.ProviderGroup.RawName,
                    f.AutoNumStart,
                    f.AutoNumEnd,
                    f.SortOverride,
                    LineupReviewSemantics.NormalizeGroupMode(f.ChannelMode),
                    LineupReviewSemantics.NormalizeTrackingPolicy(f.TrackingPolicy),
                    f.TrackingKeywords),
                StringComparer.Ordinal);

        var filterSelectionsByFilterId = new Dictionary<string, Dictionary<string, ChannelOverride>>(StringComparer.Ordinal);
        if (includedFilterLookup.Count > 0)
        {
            var includedFilterIds = includedFilterLookup.Values.Select(f => f.ProfileGroupFilterId).ToList();

            var selections = await db.ProfileGroupChannelFilters
                .AsNoTracking()
                .Include(x => x.ProviderChannel)
                .Where(x => includedFilterIds.Contains(x.ProfileGroupFilterId))
                .ToListAsync(cancellationToken);

            filterSelectionsByFilterId = selections
                .GroupBy(x => x.ProfileGroupFilterId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g
                        .Where(x => !string.IsNullOrWhiteSpace(x.ProviderChannel.StreamUrl))
                        .GroupBy(x => x.ProviderChannel.StreamUrl!, StringComparer.Ordinal)
                        .ToDictionary(
                            sg => sg.Key,
                            sg => sg.Select(x => new ChannelOverride(
                                LineupReviewSemantics.NormalizeChannelState(x.State),
                                x.DisplayNameOverride,
                                x.OutputGroupName,
                                x.ChannelNumber,
                                x.TvgIdOverride)).First(),
                            StringComparer.Ordinal),
                    StringComparer.Ordinal);
        }

        var dbChannels = liveChannels
            .Where(ch =>
            {
                if (string.IsNullOrWhiteSpace(ch.StreamUrl) || string.IsNullOrWhiteSpace(ch.GroupTitle))
                    return false;

                if (!includedFilterLookup.TryGetValue(ch.GroupTitle!, out var filter))
                    return false;

                filterSelectionsByFilterId.TryGetValue(filter.ProfileGroupFilterId, out var streamStates);
                ChannelOverride? ov = null;
                var hasOverride = streamStates is not null
                                  && streamStates.TryGetValue(ch.StreamUrl!, out ov);

                var buildData = new ChannelBuildData(
                    ch.ProviderChannelId,
                    ch.ProviderChannelKey,
                    ch.DisplayName,
                    ch.StreamUrl,
                    ch.ContentType,
                    ch.GroupTitle,
                    ch.TvgId,
                    ch.TvgName,
                    ch.LogoUrl,
                    ch.IsEvent,
                    ch.IsPlaceholder,
                    ch.EventContentKey,
                    ch.EventTitle,
                    ch.EventSport,
                    ch.EventLeague,
                    ch.EventParticipantsJson);

                return ShouldIncludeLiveChannel(buildData, filter, hasOverride, ov);
            })
            .ToList();

        var prevChannelNumbers = await LoadPreviousChannelNumbersAsync(profileId, cancellationToken);

        var outputChannels = dbChannels
            .Where(ch => !string.IsNullOrWhiteSpace(ch.TvgId))
            .Select(ch => new OutputChannel(
                ch.ProviderChannelId,
                ch.TvgId!,
                ch.DisplayName,
                ch.LogoUrl,
                prevChannelNumbers.TryGetValue(ch.ProviderChannelId, out var n) ? n : null))
            .ToList();

        // Compile
        var (compiledXml, report) = epgCompiler.Compile(outputChannels, sources, catalogues, mappingLookup);
        metrics?.RecordEpgRefresh(
            success: fetchResults.Any(x => x.Result.Status is "ok" or "not_modified"),
            channels: report.TotalChannels,
            programs: report.TotalProgrammes,
            unmatchedChannels: Math.Max(0, report.TotalChannels - report.MappedChannels));

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

    private async Task<Dictionary<string, int>> LoadPreviousChannelNumbersAsync(
        string profileId, CancellationToken cancellationToken)
    {
        var snap = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.ProfileId == profileId && x.Status == "active")
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (snap is null || !File.Exists(snap.ChannelIndexPath))
            return [];

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var entry in ChannelIndexStore.StreamAllAsync(snap.ChannelIndexPath, cancellationToken))
        {
            if (entry.TvgChno.HasValue && !string.IsNullOrEmpty(entry.ProviderChannelId))
                result[entry.ProviderChannelId] = entry.TvgChno.Value;
        }
        return result;
    }

    private async Task<Dictionary<ProviderGroupKey, string>> SyncProviderGroupsAsync(
        string providerId,
        IReadOnlyList<ParsedProviderChannel> channels,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Include ALL channels (live, vod, series) — determine dominant content type per group
        var groupData = channels
            .Where(x => !string.IsNullOrWhiteSpace(x.GroupTitle) && !string.IsNullOrWhiteSpace(x.StreamUrl))
            // Group identity is (name, content type). Providers routinely publish the same
            // category name as separate live, VOD and series categories; keeping them as one
            // row would let a live group's filter decision silently govern catalog content of
            // the same name, and would hide catalog items behind a live-labelled row.
            .GroupBy(x => new ProviderGroupKey(x.GroupTitle!, LiveClassifier.ClassifyContent(x.StreamUrl)))
            .ToDictionary(g => g.Key, g => g.Count());

        var existingGroups = await db.ProviderGroups
            .Where(x => x.ProviderId == providerId)
            .ToListAsync(cancellationToken);

        var byKey = existingGroups.ToDictionary(x => new ProviderGroupKey(x.RawName, x.ContentType));
        var legacyMixedByName = existingGroups
            .Where(x => x.ContentType == "mixed")
            .ToDictionary(x => x.RawName, StringComparer.Ordinal);

        foreach (var (key, count) in groupData)
        {
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.LastSeenUtc = now;
                existing.Active = true;
                existing.ChannelCount = count;
                continue;
            }

            // Reuse a legacy mixed row for its live successor. Keeping the row ID preserves
            // group decisions, channel selections, event rules and custom-group links across
            // an in-place upgrade instead of leaving a stale filter beside a new live filter.
            if (key.ContentType == "live" && legacyMixedByName.TryGetValue(key.RawName, out var legacyMixed))
            {
                legacyMixed.ContentType = "live";
                legacyMixed.LastSeenUtc = now;
                legacyMixed.Active = true;
                legacyMixed.ChannelCount = count;
                byKey[key] = legacyMixed;
                continue;
            }

            db.ProviderGroups.Add(new ProviderGroup
            {
                ProviderGroupId = Guid.NewGuid().ToString(),
                ProviderId = providerId,
                RawName = key.RawName,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Active = true,
                ChannelCount = count,
                ContentType = key.ContentType,
            });
        }

        foreach (var group in existingGroups)
        {
            // Legacy rows carrying the retired 'mixed' content type match no current key and
            // deactivate here; the next build recreates them as separate per-type rows.
            if (!groupData.ContainsKey(new ProviderGroupKey(group.RawName, group.ContentType)))
            {
                group.Active = false;
                group.ChannelCount = 0;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return await db.ProviderGroups
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .ToDictionaryAsync(x => new ProviderGroupKey(x.RawName, x.ContentType), x => x.ProviderGroupId, cancellationToken);
    }

    /// <summary>
    /// Identity of a provider group: the raw category name plus the kind of content it carries.
    /// Ordinal, case-sensitive — provider category names are treated as opaque.
    /// </summary>
    internal readonly record struct ProviderGroupKey(string RawName, string ContentType);

    private async Task SyncGroupFiltersAsync(
        string profileId,
        string providerId,
        bool markNewGroups,
        CancellationToken cancellationToken)
    {
        // Only live groups are mappable — VOD/series bypass group mapping entirely and are
        // controlled by the provider's IncludeVod/IncludeSeries flags.
        var allGroupIds = await db.ProviderGroups
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId && x.ContentType == "live")
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
                Decision = LineupReviewSemantics.GroupDecisionInclude,
                IsNew = markNewGroups,
                ChannelMode = LineupReviewSemantics.GroupModeManualReview,
                TrackingPolicy = LineupReviewSemantics.TrackingPolicyReview,
                TrackNewChannels = false,
                CreatedUtc = now,
                UpdatedUtc = now,
            })
            .ToList();

        if (newFilters.Count > 0)
        {
            db.ProfileGroupFilters.AddRange(newFilters);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created {Count} new group filter(s) (auto-included) for profile {ProfileId}.", newFilters.Count, profileId);
        }
    }

    private async Task SyncPendingChannelReviewsAsync(
        string profileId,
        string providerId,
        DateTime now,
        bool queueNewRowsForReview,
        CancellationToken cancellationToken)
    {
        var manualReviewFilters = await db.ProfileGroupFilters
            .AsNoTracking()
            .Include(x => x.ProviderGroup)
            .Where(x => x.ProfileId == profileId
                        && x.ProviderGroup.ProviderId == providerId
                        && x.ProviderGroup.ContentType == "live")
            .ToListAsync(cancellationToken);

        var candidateFilters = manualReviewFilters
            .Where(f =>
                LineupReviewSemantics.IsGroupIncluded(f.Decision)
                && LineupReviewSemantics.NormalizeGroupMode(f.ChannelMode) == LineupReviewSemantics.GroupModeManualReview
                && LineupReviewSemantics.ShouldQueuePending(f.TrackingPolicy)
                && f.TrackNewChannels)
            .ToList();

        if (candidateFilters.Count == 0)
            return;

        var filterByProviderGroupId = candidateFilters
            .ToDictionary(f => f.ProviderGroupId, StringComparer.Ordinal);

        var providerGroupIds = filterByProviderGroupId.Keys.ToList();

        var activeChannels = await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId
                        && x.Active
                        && x.ContentType == "live"
                        && !x.IsPlaceholder
                        && x.ProviderGroupId != null
                        && providerGroupIds.Contains(x.ProviderGroupId!))
            .Select(x => new
            {
                x.ProviderChannelId,
                ProviderGroupId = x.ProviderGroupId!,
            })
            .ToListAsync(cancellationToken);

        if (activeChannels.Count == 0)
            return;

        var filterIds = candidateFilters.Select(x => x.ProfileGroupFilterId).ToList();
        var activeChannelIds = activeChannels.Select(x => x.ProviderChannelId).ToList();

        var existing = await db.ProfileGroupChannelFilters
            .AsNoTracking()
            .Where(x => filterIds.Contains(x.ProfileGroupFilterId)
                        && activeChannelIds.Contains(x.ProviderChannelId))
            .Select(x => new { x.ProfileGroupFilterId, x.ProviderChannelId })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(x => $"{x.ProfileGroupFilterId}:{x.ProviderChannelId}")
            .ToHashSet(StringComparer.Ordinal);

        var newRowState = queueNewRowsForReview
            ? LineupReviewSemantics.ChannelStatePending
            : LineupReviewSemantics.ChannelStateExcluded;

        var newRows = new List<ProfileGroupChannelFilter>();
        foreach (var channel in activeChannels)
        {
            if (!filterByProviderGroupId.TryGetValue(channel.ProviderGroupId, out var filter))
                continue;

            var key = $"{filter.ProfileGroupFilterId}:{channel.ProviderChannelId}";
            if (existingSet.Contains(key))
                continue;

            newRows.Add(new ProfileGroupChannelFilter
            {
                ProfileGroupChannelFilterId = Guid.NewGuid().ToString(),
                ProfileGroupFilterId = filter.ProfileGroupFilterId,
                ProviderChannelId = channel.ProviderChannelId,
                State = newRowState,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
        }

        if (newRows.Count == 0)
            return;

        db.ProfileGroupChannelFilters.AddRange(newRows);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Created {Count} {State} channel review row(s) for profile {ProfileId}, provider {ProviderId}.",
            newRows.Count,
            newRowState,
            profileId,
            providerId);
    }

    private async Task SyncProviderChannelsAsync(
        string profileId,
        string providerId,
        string fetchRunId,
        IReadOnlyList<ParsedProviderChannel> channels,
        IReadOnlyDictionary<ProviderGroupKey, string> groupNameToId,
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

            // Resolve against the live row for this name — a same-named catalog category is a
            // separate group and must never claim a live channel.
            var groupId = ch.GroupTitle is not null
                          && groupNameToId.TryGetValue(new ProviderGroupKey(ch.GroupTitle, contentType), out var gid)
                ? (string?)gid : null;
            var eventClassification = EventChannelClassifier.Classify(ch.DisplayName, ch.GroupTitle);

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
                entity.IsEvent = eventClassification.IsEvent;
                entity.IsPlaceholder = eventClassification.IsPlaceholder;
                entity.EventSlotKey = eventClassification.EventSlotKey;
                entity.EventContentKey = eventClassification.EventContentKey;
                entity.EventTitle = eventClassification.EventTitle;
                entity.EventSport = eventClassification.EventSport;
                entity.EventLeague = eventClassification.EventLeague;
                entity.EventParticipantsJson = eventClassification.EventParticipantsJson;
                entity.EventStartUtc = null;
                entity.EventEndUtc = null;
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
                    IsEvent = eventClassification.IsEvent,
                    IsPlaceholder = eventClassification.IsPlaceholder,
                    EventSlotKey = eventClassification.EventSlotKey,
                    EventContentKey = eventClassification.EventContentKey,
                    EventTitle = eventClassification.EventTitle,
                    EventSport = eventClassification.EventSport,
                    EventLeague = eventClassification.EventLeague,
                    EventParticipantsJson = eventClassification.EventParticipantsJson,
                    EventStartUtc = null,
                    EventEndUtc = null,
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

    private static async Task<LineupMetricDeltas> ComputeLineupMetricDeltasAsync(
        Snapshot? previousSnapshot,
        IReadOnlyCollection<ChannelIndexEntry> currentEntries,
        CancellationToken cancellationToken)
    {
        if (previousSnapshot is null)
            return new LineupMetricDeltas(currentEntries.Count, 0, 0, 0);

        if (!File.Exists(previousSnapshot.ChannelIndexPath))
            return new LineupMetricDeltas(0, 0, 0, 0);

        var previousByKey = new Dictionary<string, ChannelIndexEntry>(StringComparer.Ordinal);
        await foreach (var entry in ChannelIndexStore.StreamAllAsync(previousSnapshot.ChannelIndexPath, cancellationToken))
            previousByKey.TryAdd(entry.StreamKey, entry);

        var currentByKey = new Dictionary<string, ChannelIndexEntry>(StringComparer.Ordinal);
        foreach (var entry in currentEntries)
            currentByKey.TryAdd(entry.StreamKey, entry);

        var added = 0;
        var renamed = 0;
        var mappingChanges = 0;
        foreach (var (streamKey, current) in currentByKey)
        {
            if (!previousByKey.TryGetValue(streamKey, out var previous))
            {
                added++;
                continue;
            }

            if (!string.Equals(previous.DisplayName, current.DisplayName, StringComparison.Ordinal)
                || !string.Equals(previous.TvgName, current.TvgName, StringComparison.Ordinal))
            {
                renamed++;
            }

            if (!string.Equals(previous.TvgId, current.TvgId, StringComparison.Ordinal)
                || previous.TvgChno != current.TvgChno)
            {
                mappingChanges++;
            }
        }

        var removed = previousByKey.Keys.Count(key => !currentByKey.ContainsKey(key));
        return new LineupMetricDeltas(added, removed, renamed, mappingChanges);
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

    private async Task PublishProviderBackOnlineIfNeededAsync(Provider provider, CancellationToken cancellationToken)
    {
        try
        {
            var hasFailure = await eventService.HasEventAsync(
                SystemEventTypes.ProviderFetchFailed,
                providerId: provider.ProviderId,
                ct: cancellationToken);
            if (!hasFailure)
                return;

            var hasRecovery = await eventService.HasEventAsync(
                SystemEventTypes.ProviderBackOnline,
                providerId: provider.ProviderId,
                ct: cancellationToken);
            if (hasRecovery)
                return;

            await eventService.PublishAsync(
                SystemEventSeverity.Info,
                SystemEventTypes.ProviderBackOnline,
                $"Provider '{provider.Name}' is back online",
                providerId: provider.ProviderId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish ProviderBackOnline event for provider {ProviderId}.", provider.ProviderId);
        }
    }

    private async Task PublishSystemEventBestEffortAsync(
        SystemEventSeverity severity,
        string eventType,
        string title,
        string? detail = null,
        string? providerId = null,
        string? integrationId = null)
    {
        try
        {
            await eventService.PublishAsync(severity, eventType, title, detail, providerId, integrationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish system event {EventType}.", eventType);
        }
    }
}
