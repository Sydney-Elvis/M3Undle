using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Resolution;
using M3Undle.Web.Streaming.Sessions;
using M3Undle.Web.Streaming.Subscribers;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Api;

/// <summary>
/// Implements the Xtream Codes-compatible API surface:
///   GET/POST /player_api.php   — account info, category lists, stream lists
///   GET      /get.php          — M3U playlist (Xtream-style URL)
///   GET      /live/{user}/{pass}/{id}[/{*tail}]
///   GET      /movie/{user}/{pass}/{id}[/{*tail}]
///   GET      /series/{user}/{pass}/{id}[/{*tail}]
///
/// Clients such as TiviMate, GSE Player, and IPTV Smarters can connect using
/// these endpoints with the endpoint-security username and password.
/// </summary>
public static class XtreamEndpoints
{
    // snake_case JSON to match the Xtream Codes API wire format
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    // Matches "Show Name S01 E01", "Show Name S01E01", "Show Name - S01E01", etc.
    private static readonly Regex SeriesNameRegex =
        new(@"^(.*?)\s+[Ss](\d{1,2})[\s\-]*[Ee]?(\d+)", RegexOptions.Compiled);

    public static IEndpointRouteBuilder MapXtreamEndpoints(this IEndpointRouteBuilder app)
    {
        // player_api.php and get.php use query-string auth — handled by MapClientSurface
        var client = app.MapClientSurface();
        client.MapGet("player_api.php", ServePlayerApiAsync);
        client.MapGet("get.php", ServeGetM3uAsync);

        // Xtream path-embedded-credential streaming: /live/{user}/{pass}/{id}[.ext]
        // These use a dedicated filter that reads credentials from the route values.
        var xtream = app.MapGroup(string.Empty);
        xtream.AddEndpointFilter<XtreamPathCredentialFilter>();
        xtream.ExcludeFromDescription();

        xtream.MapGet("live/{xtreamUser}/{xtreamPass}/{streamId}", ServeXtreamStreamAsync);
        xtream.MapGet("live/{xtreamUser}/{xtreamPass}/{streamId}/{*tail}", ServeXtreamStreamAsync);
        xtream.MapGet("movie/{xtreamUser}/{xtreamPass}/{streamId}", ServeXtreamStreamAsync);
        xtream.MapGet("movie/{xtreamUser}/{xtreamPass}/{streamId}/{*tail}", ServeXtreamStreamAsync);
        xtream.MapGet("series/{xtreamUser}/{xtreamPass}/{streamId}", ServeXtreamStreamAsync);
        xtream.MapGet("series/{xtreamUser}/{xtreamPass}/{streamId}/{*tail}", ServeXtreamStreamAsync);
        xtream.MapGet("hls/{xtreamUser}/{xtreamPass}/{streamKey}/proxy", ServeXtreamHlsProxyAsync);
        xtream.MapGet("hls/generated/{xtreamUser}/{xtreamPass}/{sessionId}/{*asset}", ServeGeneratedXtreamHlsAssetAsync);

        return app;
    }

    // -------------------------------------------------------------------------
    // player_api.php
    // -------------------------------------------------------------------------

    private static async Task<IResult> ServePlayerApiAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        XtreamStreamIdCache streamIdCache,
        CancellationToken cancellationToken)
    {
        var access = context.GetResolvedClientAccess();
        var action = context.Request.Query["action"].ToString();

        // No action or explicit get_account_info → return account + server info
        if (string.IsNullOrEmpty(action) || action == "get_account_info")
            return BuildAccountInfoResult(context, access);

        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
            access.Binding.ActiveProfileId, cancellationToken);

        if (lineup is null)
            return Results.Json(Array.Empty<object>(), JsonOptions);

        return action switch
        {
            "get_live_categories"   => BuildCategoriesResult(lineup, "live"),
            "get_vod_categories"    => BuildCategoriesResult(lineup, "vod"),
            "get_series_categories" => BuildCategoriesResult(lineup, "series"),
            "get_live_streams"      => BuildStreamsResult(context, lineup, "live"),
            "get_vod_streams"       => BuildStreamsResult(context, lineup, "vod"),
            "get_series"            => BuildSeriesListResult(context, lineup),
            "get_series_info"       => BuildSeriesInfoResult(context, lineup),
            _                       => Results.Json(Array.Empty<object>(), JsonOptions),
        };
    }

    private static IResult BuildAccountInfoResult(HttpContext context, ResolvedClientAccess access)
    {
        var now = DateTimeOffset.UtcNow;

        var response = new
        {
            user_info = new
            {
                username = access.Credential.Username,
                // Deliberately never echo endpoint credentials back in account-info responses.
                // The field remains present for shape compatibility, but value is always redacted.
                password = string.Empty,
                message = string.Empty,
                auth = 1,
                status = "Active",
                exp_date = (string?)null,
                is_trial = "0",
                active_cons = "0",
                created_at = now.ToUnixTimeSeconds().ToString(),
                max_connections = "1",
                allowed_output_formats = new[] { "ts", "m3u8" },
            },
            server_info = new
            {
                url = context.Request.Host.Host,
                port = (context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80)).ToString(),
                https_port = context.Request.IsHttps
                    ? (context.Request.Host.Port ?? 443).ToString()
                    : string.Empty,
                server_protocol = context.Request.Scheme,
                rtmp_port = "0",
                timezone = "UTC",
                timestamp_now = now.ToUnixTimeSeconds(),
                time_now = now.ToString("yyyy-MM-dd HH:mm:ss"),
                xui = true,
            },
        };

        return Results.Json(response, JsonOptions);
    }

    private static IResult BuildCategoriesResult(RenderedLineup lineup, string contentType)
    {
        var categories = lineup.Channels
            .Where(c => MatchesContentType(c.ContentType, contentType))
            .GroupBy(c => c.GroupTitle ?? "Uncategorized")
            .Select(g => new
            {
                category_id = CategoryId(g.Key).ToString(),
                category_name = g.Key,
                parent_id = 0,
            })
            .ToArray();

        return Results.Json(categories, JsonOptions);
    }

    private static IResult BuildStreamsResult(HttpContext context, RenderedLineup lineup, string contentType)
    {
        var access = context.GetResolvedClientAccess();
        var baseUrl = GetBaseUrl(context);
        var username = access.Credential.Username;
        var password = access.UrlCredential?.Password ?? string.Empty;
        var categoryFilter = context.Request.Query["category_id"].ToString();
        var added = ((DateTimeOffset)lineup.SnapshotCreatedUtc).ToUnixTimeSeconds().ToString();

        var channels = lineup.Channels
            .Where(c => MatchesContentType(c.ContentType, contentType));

        if (!string.IsNullOrEmpty(categoryFilter))
            channels = channels.Where(c => CategoryId(c.GroupTitle ?? "Uncategorized").ToString() == categoryFilter);

        var (segment, ext, streamType) = contentType switch
        {
            "vod"    => ("movie",  "mp4",  "movie"),
            "series" => ("series", "mkv",  "series"),
            _        => ("live",   "ts",   "live"),
        };

        var streams = channels
            .Select((c, i) =>
            {
                var streamId  = XtreamStreamIdCache.ToStreamId(c.StreamKey);
                var streamUrl = $"{baseUrl}/{segment}/{username}/{password}/{streamId}.{ext}";
                var catId     = CategoryId(c.GroupTitle ?? "Uncategorized").ToString();

                if (contentType == "live")
                {
                    return (object)new
                    {
                        num              = i + 1,
                        name             = c.DisplayName,
                        stream_type      = streamType,
                        stream_id        = streamId,
                        stream_icon      = c.LogoUrl ?? string.Empty,
                        epg_channel_id   = c.TvgId   ?? string.Empty,
                        added,
                        category_id      = catId,
                        custom_sid       = string.Empty,
                        tv_archive       = 0,
                        direct_source    = string.Empty,
                        tv_archive_duration = 0,
                    };
                }

                return (object)new
                {
                    num                 = i + 1,
                    name                = c.DisplayName,
                    stream_type         = streamType,
                    stream_id           = streamId,
                    stream_icon         = c.LogoUrl ?? string.Empty,
                    added,
                    category_id         = catId,
                    container_extension = ext,
                    custom_sid          = string.Empty,
                    direct_source       = string.Empty,
                };
            })
            .ToArray();

        return Results.Json(streams, JsonOptions);
    }

    /// <summary>
    /// Returns one entry per unique series title (grouped from individual episodes),
    /// matching how the standard Xtream Codes API serves the series list.
    /// </summary>
    private static IResult BuildSeriesListResult(HttpContext context, RenderedLineup lineup)
    {
        var categoryFilter = context.Request.Query["category_id"].ToString();
        var added = ((DateTimeOffset)lineup.SnapshotCreatedUtc).ToUnixTimeSeconds().ToString();

        var seriesChannels = lineup.Channels
            .Where(c => c.ContentType == "series");

        if (!string.IsNullOrEmpty(categoryFilter))
            seriesChannels = seriesChannels.Where(c =>
                CategoryId(c.GroupTitle ?? "Uncategorized").ToString() == categoryFilter);

        var seriesList = seriesChannels
            .GroupBy(c => ExtractSeriesName(c.DisplayName))
            .Select((g, i) =>
            {
                var first = g.First();
                return (object)new
                {
                    num              = i + 1,
                    name             = g.Key,
                    series_id        = SeriesId(g.Key),
                    cover            = first.LogoUrl ?? string.Empty,
                    plot             = string.Empty,
                    cast             = string.Empty,
                    director         = string.Empty,
                    genre            = first.GroupTitle ?? string.Empty,
                    releaseDate      = string.Empty,
                    last_modified    = added,
                    rating           = string.Empty,
                    rating_5based    = 0,
                    backdrop_path    = Array.Empty<string>(),
                    youtube_trailer  = string.Empty,
                    episode_run_time = string.Empty,
                    category_id      = CategoryId(first.GroupTitle ?? "Uncategorized").ToString(),
                };
            })
            .ToArray();

        return Results.Json(seriesList, JsonOptions);
    }

    /// <summary>
    /// Returns full series info with episodes grouped by season for a given series_id,
    /// matching the standard Xtream Codes get_series_info response shape.
    /// </summary>
    private static IResult BuildSeriesInfoResult(HttpContext context, RenderedLineup lineup)
    {
        var seriesIdParam = context.Request.Query["series_id"].ToString();
        if (!int.TryParse(seriesIdParam, out var requestedSeriesId))
            return Results.Json(new { }, JsonOptions);

        var access   = context.GetResolvedClientAccess();
        var baseUrl  = GetBaseUrl(context);
        var username = access.Credential.Username;
        var password = access.UrlCredential?.Password ?? string.Empty;
        var added    = ((DateTimeOffset)lineup.SnapshotCreatedUtc).ToUnixTimeSeconds().ToString();

        var match = lineup.Channels
            .Where(c => c.ContentType == "series")
            .GroupBy(c => ExtractSeriesName(c.DisplayName))
            .FirstOrDefault(g => SeriesId(g.Key) == requestedSeriesId);

        if (match is null)
            return Results.Json(new { }, JsonOptions);

        var seriesName = match.Key;
        var first      = match.First();

        var episodesBySeason = new Dictionary<string, List<object>>();
        foreach (var ep in match.OrderBy(c => c.DisplayName))
        {
            var (season, epNum) = ExtractSeasonEpisode(ep.DisplayName);
            var seasonKey = season.ToString();
            if (!episodesBySeason.TryGetValue(seasonKey, out var list))
            {
                list = [];
                episodesBySeason[seasonKey] = list;
            }

            var streamId = XtreamStreamIdCache.ToStreamId(ep.StreamKey);
            list.Add(new
            {
                id                  = streamId.ToString(),
                episode_num         = epNum,
                title               = ep.DisplayName,
                container_extension = "mkv",
                added,
                season,
                direct_source       = $"{baseUrl}/series/{username}/{password}/{streamId}.mkv",
            });
        }

        var seasons = episodesBySeason.Keys
            .Select(k => new
            {
                season_number = int.Parse(k),
                name          = $"Season {k}",
                cover         = string.Empty,
            })
            .OrderBy(s => s.season_number)
            .ToArray();

        var response = new
        {
            info = new
            {
                name             = seriesName,
                cover            = first.LogoUrl ?? string.Empty,
                plot             = string.Empty,
                cast             = string.Empty,
                director         = string.Empty,
                genre            = first.GroupTitle ?? string.Empty,
                releaseDate      = string.Empty,
                rating           = string.Empty,
                rating_5based    = 0,
                backdrop_path    = Array.Empty<string>(),
                youtube_trailer  = string.Empty,
                episode_run_time = string.Empty,
                category_id      = CategoryId(first.GroupTitle ?? "Uncategorized").ToString(),
            },
            episodes = episodesBySeason,
            seasons,
        };

        return Results.Json(response, JsonOptions);
    }

    // -------------------------------------------------------------------------
    // get.php — Xtream-style M3U playlist
    // -------------------------------------------------------------------------

    private static async Task ServeGetM3uAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IM3USerializer m3uSerializer,
        CancellationToken cancellationToken)
    {
        var access = context.GetResolvedClientAccess();
        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
            access.Binding.ActiveProfileId, cancellationToken);

        if (lineup is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            await context.Response.WriteAsync("No active snapshot available.", cancellationToken);
            return;
        }

        await m3uSerializer.WriteAsync(context, lineup, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Path-auth streaming: /live|movie|series/{user}/{pass}/{streamId}[.ext]
    // -------------------------------------------------------------------------

    private static async Task ServeXtreamStreamAsync(
        string streamId,
        HttpContext context,
        ApplicationDbContext db,
        ILineupRenderer lineupRenderer,
        XtreamStreamIdCache streamIdCache,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        IHttpClientFactory httpClientFactory,
        HlsProxyService hlsProxyService,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        ILoggerFactory loggerFactory,
        IOptions<StreamProxyOptions> streamProxyOptions,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.XtreamStream");

        // Strip optional extension (.ts, .mp4, .mkv, etc.)
        var cleanId = streamId.Contains('.')
            ? streamId[..streamId.LastIndexOf('.')]
            : streamId;

        if (!int.TryParse(cleanId, out var numericId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Resolve streamKey from the numeric Xtream stream ID
        var access = context.GetResolvedClientAccess();
        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
            access.Binding.ActiveProfileId, cancellationToken);

        if (lineup is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            return;
        }

        var streamKey = await streamIdCache.TryGetStreamKeyAsync(
            lineup.SnapshotId, lineup.ChannelIndexPath, numericId, cancellationToken);

        if (streamKey is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // From here the logic mirrors CompatibilityEndpoints.ServeStreamAsync
        StreamResolveResult resolved;
        try
        {
            resolved = await streamRequestResolver.ResolveAsync(streamKey, context, cancellationToken);
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

        if (!resolved.IsSuccess || resolved.Entry is null)
        {
            context.Response.StatusCode = resolved.FailureStatusCode ?? StatusCodes.Status503ServiceUnavailable;
            if (!string.IsNullOrWhiteSpace(resolved.FailureMessage))
                await context.Response.WriteAsync(resolved.FailureMessage, cancellationToken);
            return;
        }

        if (!streamProxyOptions.Value.StreamingEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            await context.Response.WriteAsync("Stream proxy is disabled.", cancellationToken);
            return;
        }

        var entry = resolved.Entry;
        logger.LogInformation("Xtream stream tune-in: channel={Channel} id={StreamId} client={Client}",
            entry.DisplayName, streamId, context.Connection.RemoteIpAddress);

        var requiresHls = PlaybackModeResolver.RequiresHls(context, forceTs: resolved.SourceDescriptor?.ForceMpegTs ?? false);

        ChannelSessionManager.HlsSlotReservation? hlsSlotReservation = null;
        if (requiresHls && resolved.UseSharedSession && resolved.SourceDescriptor is not null)
        {
            try
            {
                hlsSlotReservation = channelSessionManager.ReserveHlsSlot(resolved.SourceDescriptor);
            }
            catch (StreamAdmissionException ex)
            {
                logger.LogWarning(
                    "Xtream HLS live stream admission rejected for {ProviderId}/{ProviderChannelId}: {Reason}",
                    resolved.SourceDescriptor.ProviderId,
                    resolved.SourceDescriptor.ProviderChannelId,
                    ex.Message);
                if (ex.RetryAfterSeconds is { } retry)
                    context.Response.Headers["Retry-After"] = retry.ToString();
                context.Response.StatusCode = ex.StatusCode;
                return;
            }
        }

        if (requiresHls)
        {
            if (resolved.SourceDescriptor is null)
            {
                hlsSlotReservation?.Dispose();
                await StreamErrorResponse.WriteHtmlErrorAsync(
                    context.Response, StatusCodes.Status503ServiceUnavailable,
                    "HLS delivery is unavailable for this stream.", cancellationToken);
                return;
            }

            var hlsCandidates = HlsDetection.GetHlsCandidates(resolved.SourceDescriptor.StreamUrl);
            if (hlsCandidates.Count > 0)
            {
                var xtreamUser = context.Request.RouteValues["xtreamUser"]?.ToString() ?? string.Empty;
                var xtreamPass = context.Request.RouteValues["xtreamPass"]?.ToString() ?? string.Empty;
                var segmentProxyBase =
                    $"{GetBaseUrl(context)}/hls/{Uri.EscapeDataString(xtreamUser)}/{Uri.EscapeDataString(xtreamPass)}/{Uri.EscapeDataString(streamKey)}/proxy";

                var manifest = await hlsProxyService.FetchAndRewriteManifestAsync(
                    hlsCandidates, resolved.SourceDescriptor, segmentProxyBase, cancellationToken);

                if (manifest is not null)
                {
                    hlsSlotReservation?.Dispose();
                    logger.LogInformation(
                        "Xtream native upstream HLS delivery: channel={Channel} id={StreamId} streamKey={StreamKey}",
                        entry.DisplayName, streamId, streamKey);
                    context.Response.ContentType = "application/vnd.apple.mpegurl";
                    context.Response.Headers.CacheControl = "no-cache";
                    await context.Response.WriteAsync(manifest, cancellationToken);
                    return;
                }
            }

            var generatedStreamUrl = resolved.SourceDescriptor.StreamUrl;
            string? generatedRelaySecret = null;
            if (channelSessionManager.TryGet(resolved.SourceDescriptor.SessionKey, out _))
            {
                var sk = resolved.SourceDescriptor.SessionKey;
                generatedStreamUrl =
                    $"http://127.0.0.1:{context.Connection.LocalPort}/internal/relay/{Uri.EscapeDataString(sk.ProviderId)}/{Uri.EscapeDataString(sk.ProviderChannelId)}";
                generatedRelaySecret = context.RequestServices.GetRequiredService<InternalRelaySecretService>().Secret;
            }

            var generatedSession = await generatedHlsSessionManager.CreateSessionAsync(
                new GeneratedHlsSessionRequest(
                    StreamUrl: generatedStreamUrl,
                    DisplayName: resolved.SourceDescriptor.DisplayName,
                    ProviderId: generatedRelaySecret is null ? resolved.SourceDescriptor.ProviderId : null,
                    AdmissionKey: resolved.SourceDescriptor.SessionKey,
                    InternalRelaySecret: generatedRelaySecret),
                cancellationToken);

            if (generatedSession is null)
            {
                hlsSlotReservation?.Dispose();
                await StreamErrorResponse.WriteHtmlErrorAsync(
                    context.Response, StatusCodes.Status503ServiceUnavailable,
                    "Generated HLS is unavailable for this stream.", cancellationToken);
                return;
            }

            var generatedManifestUrl =
                $"{GetBaseUrl(context)}/hls/generated/{Uri.EscapeDataString(context.Request.RouteValues["xtreamUser"]?.ToString() ?? string.Empty)}/{Uri.EscapeDataString(context.Request.RouteValues["xtreamPass"]?.ToString() ?? string.Empty)}/{Uri.EscapeDataString(generatedSession.SessionId)}/index.m3u8";
            context.Response.Redirect(generatedManifestUrl, permanent: false);
            return;
        }

        if (resolved.UseSharedSession && resolved.SourceDescriptor is not null)
        {

            SubscriberConnection? subscriber = null;
            try
            {
                var session = await channelSessionManager.GetOrCreateAsync(
                    resolved.SourceDescriptor, cancellationToken);
                subscriber = await session.AttachSubscriberAsync(context, cancellationToken);
                await subscriber.Completion;
                return;
            }
            catch (StreamAdmissionException ex)
            {
                logger.LogWarning(
                    "Xtream shared stream admission rejected for {ProviderId}/{ProviderChannelId}: {Reason}",
                    resolved.SourceDescriptor.ProviderId,
                    resolved.SourceDescriptor.ProviderChannelId,
                    ex.Message);
                if (ex.RetryAfterSeconds is { } retryAfter)
                    context.Response.Headers["Retry-After"] = retryAfter.ToString();
                context.Response.StatusCode = ex.StatusCode;
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (UpstreamConnectException ex)
            {
                logger.LogWarning(
                    "Xtream shared stream upstream failure for id={StreamId}. kind={FailureKind}",
                    streamId, ex.FailureKind);
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = ex.FailureKind is UpstreamFailureKind.UpstreamAuth
                        or UpstreamFailureKind.UpstreamNotFound
                        or UpstreamFailureKind.StartupFatal
                        ? StatusCodes.Status502BadGateway
                        : StatusCodes.Status503ServiceUnavailable;
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Xtream shared stream delivery failed for id={StreamId}.", streamId);
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }

        await ServeDirectRelayAsync(context, db, httpClientFactory, logger, entry.StreamUrl, entry.DisplayName, cancellationToken);
    }

    private static async Task ServeXtreamHlsProxyAsync(
        string streamKey,
        HttpContext context,
        HlsProxyService hlsProxyService,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        ILoggerFactory loggerFactory,
        string? u,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.HlsProxy");

        if (string.IsNullOrWhiteSpace(u))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string upstreamUrl;
        try
        {
            upstreamUrl = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(u));
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        logger.LogDebug("HLS segment proxy: key={StreamKey} upstream={UpstreamUrl}", streamKey, upstreamUrl);

        var xtreamUser = context.Request.RouteValues["xtreamUser"]?.ToString() ?? string.Empty;
        var xtreamPass = context.Request.RouteValues["xtreamPass"]?.ToString() ?? string.Empty;
        var segmentProxyBase =
            $"{GetBaseUrl(context)}/hls/{Uri.EscapeDataString(xtreamUser)}/{Uri.EscapeDataString(xtreamPass)}/{Uri.EscapeDataString(streamKey)}/proxy";

        string providerId;
        var useSharedSession = false;
        try
        {
            var resolved = await streamRequestResolver.ResolveAsync(streamKey, context, cancellationToken);
            if (!resolved.IsSuccess || resolved.SourceDescriptor is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            providerId = resolved.SourceDescriptor.ProviderId;
            useSharedSession = resolved.UseSharedSession;

            if (useSharedSession)
                _ = channelSessionManager.ReserveHlsSlot(resolved.SourceDescriptor);

            channelSessionManager.TouchHlsSlot(resolved.SourceDescriptor.SessionKey);
        }
        catch (StreamAdmissionException ex)
        {
            logger.LogWarning(
                "Xtream HLS proxy admission rejected for key={StreamKey}: {Reason}",
                streamKey,
                ex.Message);
            if (ex.RetryAfterSeconds is { } retry)
                context.Response.Headers["Retry-After"] = retry.ToString();
            context.Response.StatusCode = ex.StatusCode;
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await hlsProxyService.ProxyAsync(context, upstreamUrl, segmentProxyBase, providerId, cancellationToken);
    }

    private static async Task ServeGeneratedXtreamHlsAssetAsync(
        string sessionId,
        string asset,
        HttpContext context,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        HlsManifestRewriter hlsManifestRewriter,
        CancellationToken cancellationToken)
    {
        if (!generatedHlsSessionManager.TryResolveAssetPath(sessionId, asset, out var filePath, out var contentType))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        generatedHlsSessionManager.TrackClient(
            sessionId,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString(),
            context.Request.Path.Value ?? string.Empty);

        if (contentType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            string manifest;
            try
            {
                manifest = await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var xtreamUser = Uri.EscapeDataString(context.Request.RouteValues["xtreamUser"]?.ToString() ?? string.Empty);
            var xtreamPass = Uri.EscapeDataString(context.Request.RouteValues["xtreamPass"]?.ToString() ?? string.Empty);
            var manifestUrl = new Uri($"{GetBaseUrl(context)}{context.Request.Path}");
            var generatedAssetBase = $"{GetBaseUrl(context)}/hls/generated/{xtreamUser}/{xtreamPass}/{Uri.EscapeDataString(sessionId)}";

            var rewritten = hlsManifestRewriter.Rewrite(
                manifest,
                manifestUrl,
                uri =>
                {
                    var fileName = Path.GetFileName(uri.AbsolutePath);
                    return string.IsNullOrWhiteSpace(fileName)
                        ? uri.ToString()
                        : $"{generatedAssetBase}/{Uri.EscapeDataString(fileName)}";
                });

            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync(rewritten, cancellationToken);
            return;
        }

        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(filePath, cancellationToken);
    }

    private static async Task ServeDirectRelayAsync(
        HttpContext context,
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        string streamUrl,
        string displayName,
        CancellationToken cancellationToken)
    {
        var access = context.GetResolvedClientAccess();
        var profileProvider = await db.ProfileProviders
            .AsNoTracking()
            .Where(x => x.ProfileId == access.Binding.ActiveProfileId && x.Enabled)
            .OrderBy(x => x.Priority)
            .FirstOrDefaultAsync(cancellationToken);

        var provider = profileProvider is not null
            ? await db.Providers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProviderId == profileProvider.ProviderId && x.Enabled, cancellationToken)
            : null;

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
                streamUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            if (upstreamResponse.Content.Headers.ContentType is not null)
                context.Response.ContentType = upstreamResponse.Content.Headers.ContentType.ToString();

            if (upstreamResponse.Content.Headers.ContentLength.HasValue)
                context.Response.ContentLength = upstreamResponse.Content.Headers.ContentLength.Value;

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            await upstreamStream.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Xtream stream client disconnected: channel={Channel}", displayName);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Xtream stream upstream request failed: channel={Channel}", displayName);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true when a channel's content type matches the requested Xtream category type.
    /// Live catches everything that isn't explicitly vod or series.
    /// </summary>
    private static bool MatchesContentType(string channelContentType, string requestedType) =>
        requestedType switch
        {
            "vod"    => channelContentType == "vod",
            "series" => channelContentType == "series",
            _        => channelContentType != "vod" && channelContentType != "series",
        };

    /// <summary>Stable 31-bit numeric category ID derived from the group title.</summary>
    private static int CategoryId(string groupTitle)
    {
        var bytes = Encoding.UTF8.GetBytes(groupTitle);
        var hash = MD5.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        return (int)(value & 0x7FFF_FFFF);
    }

    /// <summary>Stable 31-bit numeric series ID derived from the series name.</summary>
    private static int SeriesId(string seriesName)
    {
        var bytes = Encoding.UTF8.GetBytes("series:" + seriesName);
        var hash = MD5.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        return (int)(value & 0x7FFF_FFFF);
    }

    /// <summary>
    /// Extracts the series title from an episode display name by stripping the
    /// season/episode marker (e.g. "Believers (2020) S01 E01" → "Believers (2020)").
    /// Falls back to the full display name when no marker is found.
    /// </summary>
    private static string ExtractSeriesName(string displayName)
    {
        var m = SeriesNameRegex.Match(displayName);
        return m.Success ? m.Groups[1].Value.Trim() : displayName;
    }

    /// <summary>
    /// Extracts the season and episode numbers from a display name.
    /// Returns (1, 1) when no pattern is found.
    /// </summary>
    private static (int Season, int Episode) ExtractSeasonEpisode(string displayName)
    {
        var m = SeriesNameRegex.Match(displayName);
        if (m.Success
            && int.TryParse(m.Groups[2].Value, out var season)
            && int.TryParse(m.Groups[3].Value, out var episode))
        {
            return (season, episode);
        }
        return (1, 1);
    }

    private static string GetBaseUrl(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";

}
