using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Models;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Streaming.Resolution;

public sealed class StreamRequestResolver(ApplicationDbContext db, ILogger<StreamRequestResolver> logger)
{
    public async Task<StreamResolveResult> ResolveAsync(string streamKey, HttpContext context, CancellationToken ct)
    {
        var access = context.GetResolvedClientAccess();

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.Status == "active" && x.ProfileId == access.Binding.ActiveProfileId)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ChannelIndexPath))
        {
            return StreamResolveResult.Fail(
                StatusCodes.Status503ServiceUnavailable,
                "No active snapshot is available for this profile.");
        }

        var idxPath = ChannelIndexStore.GetIdxPath(snapshot.ChannelIndexPath);
        var entry = await ChannelIndexStore.TryLookupAsync(
            snapshot.SnapshotId,
            snapshot.ChannelIndexPath,
            idxPath,
            streamKey,
            ct);

        if (entry is null)
        {
            return StreamResolveResult.Fail(
                StatusCodes.Status404NotFound,
                $"Unknown stream key '{streamKey}'.");
        }

        var routeMode = ResolveRouteMode(context.Request.Path);
        if (routeMode == StreamRouteMode.DirectRelay)
        {
            return StreamResolveResult.SuccessDirect(entry);
        }

        var providerChannel = await ResolveProviderChannelAsync(entry, ct);
        if (providerChannel is null)
        {
            // Legacy snapshot fallback: no provider-channel identity available yet.
            logger.LogWarning(
                "Falling back to direct relay for stream key {StreamKey}: provider channel identity not found.",
                streamKey);
            return StreamResolveResult.SuccessDirect(entry);
        }

        var tunerLimit = await db.Providers
            .AsNoTracking()
            .Where(x => x.ProviderId == providerChannel.ProviderId)
            .Select(x => (int?)x.MaxConcurrentStreams)
            .FirstOrDefaultAsync(ct);

        var descriptor = new StreamSourceDescriptor(
            ProfileId: access.Binding.ActiveProfileId,
            ProviderId: providerChannel.ProviderId,
            ProviderChannelId: providerChannel.ProviderChannelId,
            StreamUrl: providerChannel.StreamUrl,
            DisplayName: entry.DisplayName,
            RequestedRoute: context.Request.Path.Value ?? "/stream",
            UserAgent: context.Request.Headers.UserAgent.ToString(),
            RemoteIp: context.Connection.RemoteIpAddress?.ToString(),
            TunerLimit: tunerLimit.HasValue && tunerLimit.Value > 0 ? tunerLimit : null);

        return StreamResolveResult.SuccessShared(entry, descriptor);
    }

    private async Task<ProviderChannelLookup?> ResolveProviderChannelAsync(ChannelIndexEntry entry, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(entry.ProviderChannelId))
        {
            var byId = await db.ProviderChannels
                .AsNoTracking()
                .Where(x => x.ProviderChannelId == entry.ProviderChannelId && x.Active && x.ContentType == "live")
                .Select(x => new ProviderChannelLookup(x.ProviderId, x.ProviderChannelId, x.StreamUrl))
                .FirstOrDefaultAsync(ct);

            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(entry.StreamUrl))
            return null;

        return await db.ProviderChannels
            .AsNoTracking()
            .Where(x => x.StreamUrl == entry.StreamUrl && x.Active && x.ContentType == "live")
            .OrderByDescending(x => x.LastSeenUtc)
            .Select(x => new ProviderChannelLookup(x.ProviderId, x.ProviderChannelId, x.StreamUrl))
            .FirstOrDefaultAsync(ct);
    }

    private static StreamRouteMode ResolveRouteMode(PathString path)
    {
        if (path.StartsWithSegments("/movie", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/vod", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/series", StringComparison.OrdinalIgnoreCase))
        {
            return StreamRouteMode.DirectRelay;
        }

        if (path.StartsWithSegments("/live", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/stream", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/tune", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/hdhr/tune", StringComparison.OrdinalIgnoreCase))
        {
            return StreamRouteMode.SharedLiveSession;
        }

        return StreamRouteMode.DirectRelay;
    }

    private sealed record ProviderChannelLookup(string ProviderId, string ProviderChannelId, string StreamUrl);
}

public enum StreamRouteMode
{
    DirectRelay = 0,
    SharedLiveSession = 1,
}

public sealed record StreamResolveResult(
    bool IsSuccess,
    bool UseSharedSession,
    int? FailureStatusCode,
    string? FailureMessage,
    ChannelIndexEntry? Entry,
    StreamSourceDescriptor? SourceDescriptor)
{
    public static StreamResolveResult Fail(int statusCode, string message)
        => new(false, false, statusCode, message, null, null);

    public static StreamResolveResult SuccessDirect(ChannelIndexEntry entry)
        => new(true, false, null, null, entry, null);

    public static StreamResolveResult SuccessShared(ChannelIndexEntry entry, StreamSourceDescriptor descriptor)
        => new(true, true, null, null, entry, descriptor);
}

