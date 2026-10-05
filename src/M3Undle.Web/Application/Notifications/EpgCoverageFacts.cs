using System.Globalization;
using M3Undle.Core.Epg;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>Process-lifetime memo so the 30-second reconcile does not re-read or re-parse unchanged facts.</summary>
public sealed class NotificationRuntimeState
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Version, IReadOnlyList<IReadOnlyList<EpgInterval>> Channels)> _coverage = new(StringComparer.Ordinal);
    private readonly HashSet<string> _backfilled = new(StringComparer.Ordinal);

    public string? RelevanceStamp { get; set; }

    public DateTime? LastCleanupUtc { get; set; }

    public bool TryGetCoverage(string sourceId, string version, out IReadOnlyList<IReadOnlyList<EpgInterval>> channels)
    {
        lock (_lock)
        {
            if (_coverage.TryGetValue(sourceId, out var entry) && entry.Version == version)
            {
                channels = entry.Channels;
                return true;
            }
        }

        channels = [];
        return false;
    }

    public void SetCoverage(string sourceId, string version, IReadOnlyList<IReadOnlyList<EpgInterval>> channels)
    {
        lock (_lock)
            _coverage[sourceId] = (version, channels);
    }

    public bool MarkBackfilled(string sourceId)
    {
        lock (_lock)
            return _backfilled.Add(sourceId);
    }
}

/// <summary>
/// Bounded per-channel programme intervals for the future-coverage evaluation, plus which channels matter.
/// Intervals are persisted when a source is downloaded (same commit as its outcome); relevance comes from committed
/// publications and mappings, so it never depends on a UI event or a live parse.
/// </summary>
public sealed class EpgCoverageFacts(
    ApplicationDbContext db,
    NotificationRuntimeState state,
    XmltvParser xmltvParser,
    RuntimePaths runtimePaths,
    TimeProvider timeProvider,
    ILogger<EpgCoverageFacts> logger)
{
    private const int MaxIntervalsPerChannel = 400;
    private static readonly TimeSpan PastRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan FutureRetention = TimeSpan.FromHours(180);

    public static (string Encoded, int Count) EncodeIntervals(IEnumerable<EpgInterval> intervals, DateTimeOffset nowUtc)
    {
        var kept = intervals
            .Where(i => i.StopUtc > i.StartUtc && i.StopUtc > nowUtc - PastRetention && i.StartUtc < nowUtc + FutureRetention)
            .OrderBy(i => i.StartUtc)
            .ToList();

        var merged = new List<EpgInterval>();
        foreach (var interval in kept)
        {
            if (merged.Count > 0 && interval.StartUtc <= merged[^1].StopUtc)
            {
                if (interval.StopUtc > merged[^1].StopUtc)
                    merged[^1] = merged[^1] with { StopUtc = interval.StopUtc };
            }
            else
            {
                merged.Add(interval);
            }
        }

        if (merged.Count > MaxIntervalsPerChannel)
            merged = merged.Take(MaxIntervalsPerChannel).ToList();

        var encoded = string.Join(';', merged.Select(i =>
            $"{i.StartUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}-{i.StopUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}"));
        return (encoded, merged.Count);
    }

    public static List<EpgInterval> DecodeIntervals(string? encoded)
    {
        var result = new List<EpgInterval>();
        if (string.IsNullOrEmpty(encoded))
            return result;

        foreach (var part in encoded.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-', 1);
            if (dash <= 0
                || !long.TryParse(part.AsSpan(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
                || !long.TryParse(part.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stop))
                continue;
            result.Add(new EpgInterval(DateTimeOffset.FromUnixTimeSeconds(start), DateTimeOffset.FromUnixTimeSeconds(stop)));
        }

        return result;
    }

    /// <summary>Stages interval facts for every channel the parsed source describes; channels it no longer has lose theirs.</summary>
    public async Task StageFromCatalogueAsync(
        string sourceId,
        EpgCatalogue catalogue,
        DateTime nowUtc,
        string evidenceRevision,
        CancellationToken cancellationToken)
    {
        var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        var rows = await db.EpgNotificationCoverage
            .Where(x => x.EpgSourceId == sourceId)
            .ToDictionaryAsync(x => x.XmltvChannelId, StringComparer.Ordinal, cancellationToken);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var channelIds = catalogue.Channels.Select(c => c.XmltvChannelId)
            .Concat(catalogue.ProgrammesByChannel.Keys)
            .Distinct(StringComparer.Ordinal);

        foreach (var channelId in channelIds)
        {
            seen.Add(channelId);
            catalogue.ProgrammesByChannel.TryGetValue(channelId, out var programmes);
            var (encoded, count) = EncodeIntervals(
                (programmes ?? []).Select(p => new EpgInterval(p.StartUtc, p.StopUtc)), now);
            Upsert(rows, sourceId, channelId, encoded, count, evidenceRevision, nowUtc);
        }

        foreach (var (channelId, row) in rows)
        {
            if (seen.Contains(channelId) || row.IntervalCount == 0 && row.IntervalsEncoded.Length == 0)
                continue;
            row.IntervalsEncoded = string.Empty;
            row.IntervalCount = 0;
            row.EvidenceRevision = evidenceRevision;
            row.UpdatedUtc = nowUtc;
        }
    }

    private void Upsert(
        Dictionary<string, EpgNotificationCoverage> rows,
        string sourceId,
        string channelId,
        string encoded,
        int count,
        string evidenceRevision,
        DateTime nowUtc)
    {
        if (rows.TryGetValue(channelId, out var row))
        {
            row.IntervalsEncoded = encoded;
            row.IntervalCount = count;
            row.EvidenceRevision = evidenceRevision;
            row.UpdatedUtc = nowUtc;
            return;
        }

        row = new EpgNotificationCoverage
        {
            EpgNotificationCoverageId = Guid.NewGuid().ToString(),
            EpgSourceId = sourceId,
            XmltvChannelId = channelId,
            IsRelevant = false,
            IntervalsEncoded = encoded,
            IntervalCount = count,
            EvidenceRevision = evidenceRevision,
            UpdatedUtc = nowUtc,
        };
        rows[channelId] = row;
        db.EpgNotificationCoverage.Add(row);
    }

    /// <summary>
    /// Recomputes which XMLTV channels matter: the distinct channels mapped from live channels in each profile's
    /// committed active publication, per source. Unmapped channels never count and a channel mapped from several
    /// profiles counts once. Returns true when it did any work.
    /// </summary>
    public async Task<bool> RefreshRelevanceAsync(bool force, CancellationToken cancellationToken)
    {
        var activeSnapshots = (await db.Snapshots.AsNoTracking()
                .Where(s => s.Status == "active")
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.ProfileId)
            .Select(g => g.OrderByDescending(s => s.CreatedUtc).First())
            .ToList();

        var mappingStats = await db.EpgChannelMappings.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Max = g.Max(m => m.UpdatedUtc) })
            .FirstOrDefaultAsync(cancellationToken);
        var sourceCount = await db.EpgSources.CountAsync(x => x.Enabled, cancellationToken);

        var stamp = string.Join(',', activeSnapshots.Select(s => s.SnapshotId).OrderBy(x => x, StringComparer.Ordinal))
            + $"|{mappingStats?.Count}|{mappingStats?.Max:O}|{sourceCount}";
        if (!force && stamp == state.RelevanceStamp)
            return false;

        var profileNames = await db.Profiles.AsNoTracking()
            .Where(p => p.Enabled)
            .ToDictionaryAsync(p => p.ProfileId, p => p.Name, StringComparer.Ordinal, cancellationToken);

        var published = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var snapshot in activeSnapshots.Where(s => profileNames.ContainsKey(s.ProfileId)))
        {
            if (string.IsNullOrWhiteSpace(snapshot.ChannelIndexPath) || !File.Exists(snapshot.ChannelIndexPath))
                continue;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var entry in ChannelIndexStore.StreamAllAsync(snapshot.ChannelIndexPath, cancellationToken))
            {
                if (!string.IsNullOrEmpty(entry.ProviderChannelId))
                    ids.Add(entry.ProviderChannelId);
            }
            published[snapshot.ProfileId] = ids;
        }

        var relevant = new Dictionary<(string SourceId, string ChannelId), SortedSet<string>>();
        var mappings = await db.EpgChannelMappings.AsNoTracking()
            .Select(m => new { m.ProfileId, m.ProviderChannelId, m.EpgSourceId, m.XmltvChannelId })
            .ToListAsync(cancellationToken);
        foreach (var m in mappings)
        {
            if (!published.TryGetValue(m.ProfileId, out var ids) || !ids.Contains(m.ProviderChannelId))
                continue;
            var key = (m.EpgSourceId, m.XmltvChannelId);
            if (!relevant.TryGetValue(key, out var names))
                relevant[key] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(profileNames[m.ProfileId]);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var rows = (await db.EpgNotificationCoverage.ToListAsync(cancellationToken))
            .ToDictionary(x => (x.EpgSourceId, x.XmltvChannelId));
        var knownSources = await db.EpgSources.AsNoTracking().Select(s => s.EpgSourceId).ToHashSetAsync(cancellationToken);

        foreach (var ((sourceId, channelId), names) in relevant)
        {
            if (!knownSources.Contains(sourceId))
                continue;

            var context = Truncate(string.Join(", ", names), 200);
            if (rows.TryGetValue((sourceId, channelId), out var row))
            {
                if (!row.IsRelevant || row.RelevanceContext != context)
                {
                    row.IsRelevant = true;
                    row.RelevanceContext = context;
                    row.UpdatedUtc = nowUtc;
                }
            }
            else
            {
                db.EpgNotificationCoverage.Add(new EpgNotificationCoverage
                {
                    EpgNotificationCoverageId = Guid.NewGuid().ToString(),
                    EpgSourceId = sourceId,
                    XmltvChannelId = channelId,
                    IsRelevant = true,
                    RelevanceContext = context,
                    IntervalsEncoded = string.Empty,
                    UpdatedUtc = nowUtc,
                });
            }
        }

        foreach (var (key, row) in rows)
        {
            if (row.IsRelevant && !relevant.ContainsKey(key))
            {
                row.IsRelevant = false;
                row.RelevanceContext = null;
                row.UpdatedUtc = nowUtc;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        state.RelevanceStamp = stamp;
        return true;
    }

    /// <summary>
    /// Once per process, rebuilds interval facts from the on-disk cache for sources that have relevant channels but no
    /// facts yet (for example straight after an upgrade), so health is judged from what is actually being served.
    /// </summary>
    public async Task BackfillMissingFromCacheAsync(CancellationToken cancellationToken)
    {
        var candidates = await db.EpgNotificationCoverage.AsNoTracking()
            .Where(x => x.IsRelevant)
            .GroupBy(x => x.EpgSourceId)
            .Select(g => new { SourceId = g.Key, WithIntervals = g.Count(x => x.IntervalCount > 0) })
            .Where(x => x.WithIntervals == 0)
            .Select(x => x.SourceId)
            .ToListAsync(cancellationToken);

        foreach (var sourceId in candidates)
        {
            if (!state.MarkBackfilled(sourceId))
                continue;

            var cacheFile = Path.Combine(runtimePaths.DataDirectory, "epg-cache", $"{sourceId}.xml");
            if (!File.Exists(cacheFile))
                continue;

            try
            {
                var xml = await File.ReadAllTextAsync(cacheFile, cancellationToken);
                if (string.IsNullOrWhiteSpace(xml))
                    continue;
                var catalogue = xmltvParser.Parse(sourceId, xml);
                await StageFromCatalogueAsync(sourceId, catalogue, timeProvider.GetUtcNow().UtcDateTime, "cache-backfill", cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                logger.LogWarning(ex, "Could not rebuild guide coverage facts for source {EpgSourceId} from its cache.", sourceId);
            }
        }
    }

    public async Task<IReadOnlyList<IReadOnlyList<EpgInterval>>> LoadRelevantChannelsAsync(string sourceId, CancellationToken cancellationToken)
    {
        var stats = await db.EpgNotificationCoverage.AsNoTracking()
            .Where(x => x.EpgSourceId == sourceId && x.IsRelevant)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Max = g.Max(x => x.UpdatedUtc) })
            .FirstOrDefaultAsync(cancellationToken);
        if (stats is null)
            return [];

        var version = $"{stats.Count}|{stats.Max:O}";
        if (state.TryGetCoverage(sourceId, version, out var cached))
            return cached;

        var channels = (await db.EpgNotificationCoverage.AsNoTracking()
                .Where(x => x.EpgSourceId == sourceId && x.IsRelevant)
                .Select(x => x.IntervalsEncoded)
                .ToListAsync(cancellationToken))
            .Select(encoded => (IReadOnlyList<EpgInterval>)DecodeIntervals(encoded))
            .ToList();
        state.SetCoverage(sourceId, version, channels);
        return channels;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
