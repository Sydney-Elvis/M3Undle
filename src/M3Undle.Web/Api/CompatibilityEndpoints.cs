using System.Text.Json;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Api;

public static class CompatibilityEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static IEndpointRouteBuilder MapCompatibilityEndpoints(this IEndpointRouteBuilder app)
    {
        var client = app.MapClientSurface();

        client.MapGet("m3u/m3undle.m3u", ServeM3uAsync);
        client.MapGet("xmltv/m3undle.xml", ServeXmltvAsync);
        client.MapGet("live/{streamKey}", ServeStreamAsync);
        client.MapGet("live/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("movie/{streamKey}", ServeStreamAsync);
        client.MapGet("movie/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("vod/{streamKey}", ServeStreamAsync);
        client.MapGet("vod/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("series/{streamKey}", ServeStreamAsync);
        client.MapGet("series/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("stream/{streamKey}", ServeStreamAsync);
        client.MapGet("tune/{streamKey}", ServeStreamAsync);
        client.MapGet("tune/{streamKey}/{*tail}", ServeStreamAsync);
        client.MapGet("hdhr/tune/{streamKey}", ServeStreamAsync);
        client.MapGet("hdhr/tune/{streamKey}/{*tail}", ServeStreamAsync);

        app.MapGet("/status", ServeStatusAsync).AllowAnonymous();

        return app;
    }

    private static async Task ServeM3uAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IM3USerializer m3uSerializer,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = context.GetResolvedClientAccess();
            var lineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);

            if (lineup is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.Append("Retry-After", "60");
                await context.Response.WriteAsync("No active snapshot available. Waiting for first refresh.", cancellationToken);
                return;
            }

            await m3uSerializer.WriteAsync(context, lineup, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.Append("Retry-After", "30");
                await context.Response.WriteAsync("Active snapshot data is unavailable.", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected before response completed
        }
    }

    private static async Task<IResult> ServeXmltvAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IXmlTvSerializer xmlTvSerializer,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = context.GetResolvedClientAccess();
            var lineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);
            if (lineup is null)
            {
                return TypedResults.Problem(
                    "No active snapshot available.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return xmlTvSerializer.Serialize(lineup);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return TypedResults.Problem(
                "Active snapshot data is unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task ServeStreamAsync(
        string streamKey,
        HttpContext context,
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.Stream");
        using var streamScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Stream" });

        var access = context.GetResolvedClientAccess();

        var snapshot = await db.Snapshots
            .AsNoTracking()
            .Where(x => x.Status == "active" && x.ProfileId == access.Binding.ActiveProfileId)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(CancellationToken.None);

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ChannelIndexPath))
        {
            logger.LogWarning(
                "Stream tune-in failed: no active snapshot for profile {ProfileId}. key={StreamKey} client={Client}",
                access.Binding.ActiveProfileId,
                streamKey,
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        (string StreamUrl, string DisplayName) entry;
        try
        {
            var hit = await GetStreamEntryAsync(snapshot.SnapshotId, snapshot.ChannelIndexPath, streamKey, cancellationToken);
            if (hit is null)
            {
                logger.LogWarning("Stream tune-in failed: unknown key. key={StreamKey} client={Client}",
                    streamKey, context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            entry = hit.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        logger.LogInformation("Stream tune-in: channel={Channel} key={StreamKey} client={Client}",
            entry.DisplayName, streamKey, context.Connection.RemoteIpAddress);

        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, CancellationToken.None);

        try
        {
            using var client = httpClientFactory.CreateClient("stream-relay");

            if (provider is not null)
            {
                ProviderFetcher.ApplyHeadersFromJson(client, provider.HeadersJson);
                if (!string.IsNullOrWhiteSpace(provider.UserAgent))
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
            }

            if (context.Request.Headers.TryGetValue("Range", out var rangeValue))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Range", rangeValue.ToArray());

            using var upstreamResponse = await client.GetAsync(
                entry.StreamUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            logger.LogInformation("Stream upstream: channel={Channel} status={Status} contentType={ContentType}",
                entry.DisplayName,
                (int)upstreamResponse.StatusCode,
                upstreamResponse.Content.Headers.ContentType?.ToString() ?? "none");

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            if (upstreamResponse.Content.Headers.ContentType is not null)
                context.Response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();

            if (upstreamResponse.Content.Headers.ContentLength.HasValue)
                context.Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            await upstreamStream.CopyToAsync(context.Response.Body, cancellationToken);

            logger.LogDebug("Stream complete: channel={Channel} client={Client}",
                entry.DisplayName,
                context.Connection.RemoteIpAddress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Stream client disconnected: channel={Channel} client={Client}",
                entry.DisplayName,
                context.Connection.RemoteIpAddress);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Stream upstream request failed: channel={Channel} key={StreamKey}",
                entry.DisplayName,
                streamKey);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    private static async Task<(string StreamUrl, string DisplayName)?> GetStreamEntryAsync(
        string snapshotId,
        string channelIndexPath,
        string streamKey,
        CancellationToken cancellationToken)
    {
        var idxPath = ChannelIndexStore.GetIdxPath(channelIndexPath);
        var entry = await ChannelIndexStore.TryLookupAsync(
            snapshotId,
            channelIndexPath,
            idxPath,
            streamKey,
            cancellationToken);
        return entry is null ? null : (entry.StreamUrl, entry.DisplayName);
    }

    private static async Task ServeStatusAsync(HttpContext context, ApplicationDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var activeSnapshot = await db.Snapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Status == "active", cancellationToken);

            var activeProvider = await db.Providers
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IsActive && x.Enabled, cancellationToken);

            FetchRunInfo? lastRefresh = null;
            if (activeProvider is not null)
            {
                var run = await db.FetchRuns
                    .AsNoTracking()
                    .Where(x => x.ProviderId == activeProvider.ProviderId && x.Type == "snapshot")
                    .OrderByDescending(x => x.StartedUtc)
                    .FirstOrDefaultAsync(cancellationToken);

                if (run is not null)
                {
                    lastRefresh = new FetchRunInfo(run.Status, run.StartedUtc, run.FinishedUtc, run.ChannelCountSeen, run.ErrorSummary);
                }
            }

            var lineupStatus = activeSnapshot is not null
                ? (lastRefresh?.Status == "fail" ? "degraded" : "ok")
                : "no_active_snapshot";
            var lineup = new LineupStatusInfo(
                Name: "m3undle",
                Status: lineupStatus,
                ActiveProvider: activeProvider is null ? null : new ActiveProviderInfo(activeProvider.ProviderId, activeProvider.Name),
                ActiveSnapshot: activeSnapshot is null ? null : new ActiveSnapshotInfo(
                    activeSnapshot.SnapshotId,
                    activeSnapshot.ProfileId,
                    activeSnapshot.CreatedUtc,
                    activeSnapshot.ChannelCountPublished),
                LastRefresh: lastRefresh);

            var status = new StatusResponse(Status: lineupStatus, Lineups: [lineup]);

            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, status, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected before response completed
        }
    }

    private sealed record StatusResponse(
        string Status,
        IReadOnlyList<LineupStatusInfo> Lineups);

    private sealed record LineupStatusInfo(
        string Name,
        string Status,
        ActiveProviderInfo? ActiveProvider,
        ActiveSnapshotInfo? ActiveSnapshot,
        FetchRunInfo? LastRefresh);

    private sealed record ActiveProviderInfo(string ProviderId, string Name);

    private sealed record ActiveSnapshotInfo(
        string SnapshotId,
        string ProfileId,
        DateTime CreatedUtc,
        int ChannelCountPublished);

    private sealed record FetchRunInfo(
        string Status,
        DateTime StartedUtc,
        DateTime? FinishedUtc,
        int? ChannelCountSeen,
        string? ErrorSummary);
}
