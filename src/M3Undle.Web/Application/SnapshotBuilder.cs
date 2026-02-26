using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        // 6. Build channel index directly from in-memory parsed channels
        var channelIndex = BuildChannelIndex(playlistResult.Channels, profileId);

        // 7. Write snapshot files
        var snapshotId = Guid.NewGuid().ToString();
        var snapshotDir = GetSnapshotDir(snapshotId);
        Directory.CreateDirectory(snapshotDir);

        var channelIndexPath = Path.Combine(snapshotDir, "channel_index.json");
        var xmltvPath = Path.Combine(snapshotDir, "guide.xml");

        await File.WriteAllTextAsync(channelIndexPath, JsonSerializer.Serialize(channelIndex, JsonOptions), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(xmltvPath, xmltvContent, Encoding.UTF8, cancellationToken);

        // 8. Insert Snapshot as "staged"
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

        // 9. Promote: new = "active", previous actives = "archived"
        await PromoteSnapshotAsync(snapshot, profileId, cancellationToken);

        // 10. Purge old snapshots
        await PurgeOldSnapshotsAsync(profileId, cancellationToken);

        // 11. Mark FetchRun as ok
        fetchRun.FinishedUtc = DateTime.UtcNow;
        fetchRun.Status = "ok";
        fetchRun.ChannelCountSeen = playlistResult.Channels.Count;
        fetchRun.PlaylistBytes = (int)Math.Min(playlistResult.Bytes, int.MaxValue);
        fetchRun.XmltvBytes = (int)Math.Min(xmltvBytes, int.MaxValue);
        await db.SaveChangesAsync(cancellationToken);

        using var snapshotScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Snapshot" });
        logger.LogInformation(
            "Snapshot {SnapshotId} promoted to active — {ChannelCount} channels published.",
            snapshotId, channelIndex.Count);

        return (true, null);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static List<ChannelIndexEntry> BuildChannelIndex(IReadOnlyList<ParsedProviderChannel> channels, string profileId)
    {
        return channels
            .Where(x => !string.IsNullOrWhiteSpace(x.StreamUrl))
            .OrderBy(x => x.DisplayName, StringComparer.Ordinal)
            .ThenBy(x => x.StreamUrl, StringComparer.Ordinal)
            .Select(channel =>
            {
                var stableKey = !string.IsNullOrWhiteSpace(channel.ProviderChannelKey)
                    ? channel.ProviderChannelKey!
                    : $"{channel.DisplayName}\u001f{channel.StreamUrl}";

                return new ChannelIndexEntry(
                    StreamKey: DeriveStreamKey(stableKey, profileId),
                    DisplayName: channel.DisplayName,
                    TvgId: channel.TvgId,
                    TvgName: channel.TvgName,
                    LogoUrl: channel.LogoUrl,
                    GroupTitle: channel.GroupTitle,
                    TvgChno: null,
                    ProviderChannelId: string.Empty,
                    StreamUrl: channel.StreamUrl!);
            })
            .ToList();
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
        {
            old.Status = "archived";
        }

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
        {
            return;
        }

        foreach (var snapshot in toDelete)
        {
            var dir = GetSnapshotDir(snapshot.SnapshotId);
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
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

