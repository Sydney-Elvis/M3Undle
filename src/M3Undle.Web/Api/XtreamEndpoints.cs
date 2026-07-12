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
///   GET/POST /get.php          — M3U playlist (Xtream-style URL)
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
        // player_api.php, get.php, and xmltv.php use query-string/form auth — handled by MapClientSurface
        var client = app.MapClientSurface();
        client.MapMethods("player_api.php", ["GET", "POST"], ServePlayerApiAsync);
        client.MapMethods("get.php", ["GET", "POST"], ServeGetM3uAsync);
        client.MapMethods("xmltv.php", ["GET", "POST"], ServeXmltvAsync);

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
        xtream.MapGet("hls/{xtreamUser}/{xtreamPass}/{streamId}/index.m3u8", ServeXtreamExternalHlsManifestAsync);
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
        ApplicationDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.XtreamApi");
        var access = context.GetResolvedClientAccess();
        var form = await TryReadFormAsync(context.Request, cancellationToken);
        var action = GetRequestValue(context.Request, form, "action");

        var actionForLog    = (string.IsNullOrEmpty(action) ? "get_account_info" : action).ReplaceLineEndings(" ");
        var clientForLog    = (context.Connection.RemoteIpAddress?.ToString() ?? "unknown").ReplaceLineEndings(" ");
        var userAgentForLog = context.Request.Headers.UserAgent.ToString().ReplaceLineEndings(" ");
        logger.LogInformation("Xtream API: action={Action} client={Client} ua={UserAgent}",
            actionForLog, clientForLog, userAgentForLog);

        // No action or explicit get_account_info → return account + server info
        if (string.IsNullOrEmpty(action) || action == "get_account_info")
        {
            var minExpiry = await db.ProfileProviders
                .Where(pp => pp.ProfileId == access.Binding.ActiveProfileId && pp.Enabled)
                .Join(db.Providers.Where(p => p.Enabled), pp => pp.ProviderId, p => p.ProviderId, (pp, p) => p.PlaylistExpiresUtc)
                .Where(e => e != null)
                .MinAsync(e => e, cancellationToken);
            return BuildAccountInfoResult(context, access, minExpiry);
        }

        // EPG actions always return an empty envelope — no lineup needed.
        if (action is "get_short_epg" or "get_epg_info")
            return Results.Json(new { epg_listings = Array.Empty<object>() }, JsonOptions);

        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
            access.Binding.ActiveProfileId, cancellationToken);

        if (lineup is null)
            return Results.Json(Array.Empty<object>(), JsonOptions);

        // Resolve the snapshot's bijective key→ID assignment once. The same assignment backs
        // path-auth streaming (ID→key), so every ID advertised here resolves back to its channel.
        var streamIds = streamIdCache.GetAssignment(
            lineup.SnapshotId, lineup.Channels.Select(c => c.StreamKey)).KeyToId;

        return action switch
        {
            "get_live_categories"   => BuildCategoriesResult(lineup, "live"),
            "get_vod_categories"    => BuildCategoriesResult(lineup, "vod"),
            "get_series_categories" => BuildCategoriesResult(lineup, "series"),
            "get_live_streams"      => BuildStreamsResult(context, form, lineup, "live", streamIds),
            "get_vod_streams"       => BuildStreamsResult(context, form, lineup, "vod", streamIds),
            "get_series"            => BuildSeriesListResult(context, form, lineup),
            "get_series_info"       => BuildSeriesInfoResult(context, form, lineup, streamIds),
            _               => Results.Json(Array.Empty<object>(), JsonOptions),
        };
    }

    private static IResult BuildAccountInfoResult(HttpContext context, ResolvedClientAccess access, DateTime? minProviderExpiry)
    {
        var now = DateTimeOffset.UtcNow;

        var expDate = minProviderExpiry.HasValue
            ? new DateTimeOffset(minProviderExpiry.Value, TimeSpan.Zero).ToUnixTimeSeconds().ToString()
            : (string?)null;

        var response = new
        {
            user_info = new
            {
                username = access.UrlCredential?.Username ?? access.Credential.Username,
                // Echo the URL credential password (what the client submitted) so that Xtream
                // clients that use the auth response to seed subsequent requests work correctly.
                // This is the external/URL credential only — the internal M3Undle account
                // password is never exposed here.
                password = access.UrlCredential?.Password ?? string.Empty,
                message = string.Empty,
                auth = 1,
                status = "Active",
                exp_date = expDate,
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

    private static IResult BuildStreamsResult(
        HttpContext context,
        IFormCollection? form,
        RenderedLineup lineup,
        string contentType,
        IReadOnlyDictionary<string, int> streamIds)
    {
        var access = context.GetResolvedClientAccess();
        var baseUrl = GetBaseUrl(context);
        var username = Uri.EscapeDataString(access.UrlCredential?.Username ?? access.Credential.Username);
        var password = Uri.EscapeDataString(access.UrlCredential?.Password ?? string.Empty);
        var categoryFilter = GetRequestValue(context.Request, form, "category_id");
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
                // Use the snapshot's bijective assignment (collision-free, always < 10,000,000)
                // so the advertised ID resolves back to this exact channel and stays
                // Brightscript-safe. Fall back to the preferred hash if a key is somehow absent.
                var streamId  = streamIds.TryGetValue(c.StreamKey, out var assignedId)
                    ? assignedId
                    : XtreamStreamIdCache.ToStreamId(c.StreamKey);
                var streamUrl = $"{baseUrl}/{segment}/{username}/{password}/{streamId}.{ext}";
                var catId     = CategoryId(c.GroupTitle ?? "Uncategorized").ToString();

                // Emit stream_id as a string so Brightscript treats it verbatim rather than
                // coercing through a 32-bit float. The ID is already < 10M, so this is plain digits.
                var streamIdStr = streamId.ToString();

                if (contentType == "live")
                {
                    return (object)new
                    {
                        num              = i + 1,
                        name             = c.DisplayName,
                        stream_type      = streamType,
                        stream_id        = streamIdStr,
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
                    stream_id           = streamIdStr,
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
    private static IResult BuildSeriesListResult(HttpContext context, IFormCollection? form, RenderedLineup lineup)
    {
        var categoryFilter = GetRequestValue(context.Request, form, "category_id");
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
                    series_id        = SeriesId(g.Key).ToString(),
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
    private static IResult BuildSeriesInfoResult(
        HttpContext context,
        IFormCollection? form,
        RenderedLineup lineup,
        IReadOnlyDictionary<string, int> streamIds)
    {
        var seriesIdParam = GetRequestValue(context.Request, form, "series_id");
        if (!int.TryParse(seriesIdParam, out var requestedSeriesId))
            return Results.Json(new { }, JsonOptions);

        var access   = context.GetResolvedClientAccess();
        var baseUrl  = GetBaseUrl(context);
        var username = Uri.EscapeDataString(access.UrlCredential?.Username ?? access.Credential.Username);
        var password = Uri.EscapeDataString(access.UrlCredential?.Password ?? string.Empty);
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

            var streamId = streamIds.TryGetValue(ep.StreamKey, out var assignedId)
                ? assignedId
                : XtreamStreamIdCache.ToStreamId(ep.StreamKey);
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
        ApplicationDbContext db,
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

        var minExpiry = await db.ProfileProviders
            .Where(pp => pp.ProfileId == access.Binding.ActiveProfileId && pp.Enabled)
            .Join(db.Providers.Where(p => p.Enabled), pp => pp.ProviderId, p => p.ProviderId, (pp, p) => p.PlaylistExpiresUtc)
            .Where(e => e != null)
            .MinAsync(e => e, cancellationToken);

        await m3uSerializer.WriteAsync(context, lineup, minExpiry, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // xmltv.php — Xtream-style XMLTV EPG endpoint
    // -------------------------------------------------------------------------

    private static async Task<IResult> ServeXmltvAsync(
        HttpContext context,
        ILineupRenderer lineupRenderer,
        IXmlTvSerializer xmlTvSerializer,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = context.GetResolvedClientAccess();
            var lineup = await lineupRenderer.TryRenderActiveLineupAsync(
                access.Binding.ActiveProfileId, cancellationToken);
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

        var streamKey = streamIdCache.TryGetStreamKey(
            lineup.SnapshotId, lineup.Channels.Select(c => c.StreamKey), numericId);

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

        var forceTs = resolved.SourceDescriptor?.ForceMpegTs ?? false;

        // Use the server-side resolved access credentials (not the raw route values) so that
        // the redirect path is built from trusted data and does not trigger open-redirect
        // analysis on values sourced directly from the request.
        var urlPass = access.UrlCredential?.Password;
        var clientRequestedHls = streamId.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        var clientRequestedTs  = streamId.EndsWith(".ts",   StringComparison.OrdinalIgnoreCase);
        // Burst-buffering clients (Roku, ExoPlayer, okhttp) cannot play raw MPEG-TS over HTTP,
        // so redirect them to generated HLS even when the URL explicitly ends in .ts.
        // For all other clients, honour the explicit .ts extension.
        var isBurstClient = PlaybackModeResolver.IsBurstBufferingClient(context);
        if (!forceTs && resolved.UseSharedSession
            && (clientRequestedHls || isBurstClient)
            && (!clientRequestedTs || isBurstClient)
            && urlPass is not null)
        {
            var numericStreamId = streamId.Contains('.')
                ? streamId[..streamId.LastIndexOf('.')]
                : streamId;
            // Pass the app-layer UA as a hint so the manifest handler can seed client tracking
            // with the app identity rather than whatever the media player sends on follow-up requests.
            var requestUa = context.Request.Headers.UserAgent.ToString();
            var uaHint    = string.IsNullOrEmpty(requestUa) ? string.Empty : $"?_ua={Uri.EscapeDataString(requestUa)}";
            var fromHint  = (clientRequestedTs && isBurstClient)
                ? (uaHint.Length > 0 ? "&_from=ts" : "?_from=ts")
                : string.Empty;
            var hlsPath = $"/hls/{Uri.EscapeDataString(access.Credential.Username)}/{Uri.EscapeDataString(urlPass)}/{Uri.EscapeDataString(numericStreamId)}/index.m3u8{uaHint}{fromHint}";
            logger.LogInformation(
                "Auto-HLS redirect (Xtream): channel={Channel} id={StreamId} client={Client}",
                entry.DisplayName, streamId, context.Connection.RemoteIpAddress);
            await context.RedirectLocalAsync(hlsPath);
            return;
        }

        var requiresHls = clientRequestedHls || PlaybackModeResolver.RequiresHls(context, forceTs);

        // HLS slot reservation only applies to non-shared (native upstream HLS) sessions.
        ChannelSessionManager.HlsSlotReservation? hlsSlotReservation = null;
        if (requiresHls && !resolved.UseSharedSession && resolved.SourceDescriptor is not null)
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

            // Native upstream HLS only for non-shared sessions.
            // Shared sessions use generated HLS wrapper to relay from the ring buffer.
            if (!resolved.UseSharedSession)
            {
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
            }

            var generatedStreamUrl = resolved.SourceDescriptor.StreamUrl;
            string? generatedRelaySecret = null;
            string? parentStreamSessionId = null;
            var parentSession = await channelSessionManager.TryGetOrCreateForGeneratedHlsAsync(
                resolved.SourceDescriptor,
                resolved.UseSharedSession,
                cancellationToken);
            if (parentSession is not null)
            {
                var sk = resolved.SourceDescriptor.SessionKey;
                parentStreamSessionId = parentSession.SessionId;
                generatedStreamUrl =
                    $"http://127.0.0.1:{context.Connection.LocalPort}/internal/relay/{Uri.EscapeDataString(sk.ProviderId)}/{Uri.EscapeDataString(sk.ProviderChannelId)}.ts";
                generatedRelaySecret = context.RequestServices.GetRequiredService<InternalRelaySecretService>().Secret;
            }

            var generatedSession = await generatedHlsSessionManager.CreateSessionAsync(
                new GeneratedHlsSessionRequest(
                    StreamUrl: generatedStreamUrl,
                    DisplayName: resolved.SourceDescriptor.DisplayName,
                    ProviderId: generatedRelaySecret is null ? resolved.SourceDescriptor.ProviderId : null,
                    AdmissionKey: resolved.SourceDescriptor.SessionKey,
                    InternalRelaySecret: generatedRelaySecret,
                    ParentStreamSessionId: parentStreamSessionId,
                    RequestedRoute: resolved.SourceDescriptor.RequestedRoute),
                cancellationToken);

            if (generatedSession is null)
            {
                hlsSlotReservation?.Dispose();
                await StreamErrorResponse.WriteHtmlErrorAsync(
                    context.Response, StatusCodes.Status503ServiceUnavailable,
                    "Generated HLS is unavailable for this stream.", cancellationToken);
                return;
            }

            var generatedManifestUrl = BuildGeneratedXtreamHlsManifestRedirectUrl(
                context,
                generatedSession.SessionId);
            await context.RedirectLocalAsync(generatedManifestUrl);
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
                generatedHlsSessionManager.NotifyDirectClientActive(
                    subscriber.RemoteIp,
                    subscriber.UserAgent,
                    session.SessionId);
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

            // Only reserve HLS slot for non-shared (native upstream HLS) sessions.
            if (!useSharedSession)
            {
                _ = channelSessionManager.ReserveHlsSlot(resolved.SourceDescriptor);
                channelSessionManager.TouchHlsSlot(resolved.SourceDescriptor.SessionKey);
            }
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

    private static async Task ServeXtreamExternalHlsManifestAsync(
        string streamId,
        HttpContext context,
        ILineupRenderer lineupRenderer,
        XtreamStreamIdCache streamIdCache,
        StreamRequestResolver streamRequestResolver,
        ChannelSessionManager channelSessionManager,
        GeneratedHlsSessionManager generatedHlsSessionManager,
        InternalRelaySecretService internalRelaySecretService,
        HlsManifestRewriter hlsManifestRewriter,
        IOptions<StreamProxyOptions> streamProxyOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("M3Undle.XtreamStream");

        var cleanId = streamId.Contains('.')
            ? streamId[..streamId.LastIndexOf('.')]
            : streamId;

        if (!int.TryParse(cleanId, out var numericId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var access = context.GetResolvedClientAccess();
        var lineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);
        if (lineup is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "60");
            return;
        }

        var streamKey = streamIdCache.TryGetStreamKey(
            lineup.SnapshotId, lineup.Channels.Select(c => c.StreamKey), numericId);
        if (streamKey is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        StreamResolveResult resolved;
        try
        {
            resolved = await streamRequestResolver.ResolveAsync(streamKey, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!resolved.IsSuccess || resolved.Entry is null || resolved.SourceDescriptor is null)
        {
            context.Response.StatusCode = resolved.FailureStatusCode ?? StatusCodes.Status404NotFound;
            return;
        }

        if (!streamProxyOptions.Value.StreamingEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Stream proxy is disabled.", cancellationToken);
            return;
        }

        var entry = resolved.Entry;
        var source = resolved.SourceDescriptor;

        var parentSession = await channelSessionManager.TryGetOrCreateForGeneratedHlsAsync(
            source, useSharedSession: true, cancellationToken);

        string generatedStreamUrl = source.StreamUrl;
        string? generatedRelaySecret = null;
        string? parentStreamSessionId = null;

        if (parentSession is not null)
        {
            var sk = source.SessionKey;
            parentStreamSessionId = parentSession.SessionId;
            generatedStreamUrl =
                $"http://127.0.0.1:{context.Connection.LocalPort}/internal/relay/{Uri.EscapeDataString(sk.ProviderId)}/{Uri.EscapeDataString(sk.ProviderChannelId)}.ts";
            generatedRelaySecret = internalRelaySecretService.Secret;
        }

        var generatedSession = await generatedHlsSessionManager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: generatedStreamUrl,
                DisplayName: source.DisplayName,
                ProviderId: generatedRelaySecret is null ? source.ProviderId : null,
                AdmissionKey: source.SessionKey,
                InternalRelaySecret: generatedRelaySecret,
                ParentStreamSessionId: parentStreamSessionId,
                RequestedRoute: context.Request.Path.Value ?? "/hls/xtream/manifest",
                // The redirect seeds _from=ts when a burst client (Roku) asked for .ts
                // and was auto-upgraded to HLS — surface that in the stream monitor.
                UpgradedFromTs: string.Equals(context.Request.Query["_from"], "ts", StringComparison.Ordinal)),
            cancellationToken);

        if (generatedSession is null)
        {
            logger.LogWarning(
                "Xtream external HLS startup failed: channel={Channel} id={StreamId}",
                entry.DisplayName, streamId);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "5");
            await context.Response.WriteAsync("HLS unavailable for this channel.", cancellationToken);
            return;
        }

        // Prefer the UA hint seeded by the redirect (the app-layer UA from ServeXtreamStreamAsync)
        // over whatever the media player sends on this follow-up request — the hint is more specific.
        var hintUa      = context.Request.Query["_ua"].ToString();
        var requestUa   = context.Request.Headers.UserAgent.ToString();
        var effectiveUa = string.IsNullOrEmpty(hintUa) ? requestUa : hintUa;
        generatedHlsSessionManager.TrackClient(
            generatedSession.SessionId,
            context.Connection.RemoteIpAddress?.ToString(),
            effectiveUa,
            context.Request.Path.Value ?? string.Empty,
            GeneratedHlsSessionManager.ShouldCountAsViewer(effectiveUa),
            upgradedFromTs: string.Equals(context.Request.Query["_from"], "ts", StringComparison.Ordinal));

        var manifest = await generatedHlsSessionManager.ReadManifestAsync(generatedSession.SessionId, cancellationToken);
        if (manifest is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Append("Retry-After", "2");
            await context.Response.WriteAsync("HLS manifest not ready yet.", cancellationToken);
            return;
        }

        var manifestUrl = new Uri($"{GetBaseUrl(context)}{context.Request.Path}");
        var segmentBase = BuildGeneratedXtreamHlsAssetBaseUrl(context, generatedSession.SessionId);

        var rewritten = hlsManifestRewriter.Rewrite(
            manifest,
            manifestUrl,
            uri =>
            {
                var fileName = Path.GetFileName(uri.AbsolutePath);
                return string.IsNullOrWhiteSpace(fileName)
                    ? uri.ToString()
                    : $"{segmentBase}/{Uri.EscapeDataString(fileName)}";
            });

        logger.LogInformation(
            "Xtream external HLS manifest served: channel={Channel} id={StreamId} session={SessionId}",
            entry.DisplayName, streamId, generatedSession.SessionId);

        context.Response.ContentType = "application/vnd.apple.mpegurl";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(rewritten, cancellationToken);
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
            context.Request.Path.Value ?? string.Empty,
            GeneratedHlsSessionManager.ShouldCountAsViewer(context.Request.Headers.UserAgent.ToString()));

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

            var manifestUrl = new Uri($"{GetBaseUrl(context)}{context.Request.Path}");
            var generatedAssetBase = BuildGeneratedXtreamHlsAssetBaseUrl(context, sessionId);

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

    internal static string BuildGeneratedXtreamHlsManifestRedirectUrl(HttpContext context, string sessionId)
    {
        var pathBase = context.Request.PathBase.HasValue
            ? context.Request.PathBase.Value!.TrimEnd('/')
            : string.Empty;
        var escapedSessionId = Uri.EscapeDataString(sessionId);

        if (TryGetEscapedXtreamPathCredentials(context, out var xtreamUser, out var xtreamPass))
            return $"{pathBase}/hls/generated/{xtreamUser}/{xtreamPass}/{escapedSessionId}/index.m3u8";

        var fallback = $"{pathBase}/hls/generated/{escapedSessionId}/index.m3u8";
        return fallback.ApplyClientAccessQuery(context);
    }

    internal static string BuildGeneratedXtreamHlsAssetBaseUrl(HttpContext context, string sessionId)
    {
        var escapedSessionId = Uri.EscapeDataString(sessionId);
        if (TryGetEscapedXtreamPathCredentials(context, out var xtreamUser, out var xtreamPass))
            return $"{GetBaseUrl(context)}/hls/generated/{xtreamUser}/{xtreamPass}/{escapedSessionId}";

        return $"{GetBaseUrl(context)}/hls/generated/{escapedSessionId}";
    }

    private static bool TryGetEscapedXtreamPathCredentials(
        HttpContext context,
        out string xtreamUser,
        out string xtreamPass)
    {
        var rawUser = context.Request.RouteValues["xtreamUser"]?.ToString();
        var rawPass = context.Request.RouteValues["xtreamPass"]?.ToString();

        if (string.IsNullOrWhiteSpace(rawUser) || string.IsNullOrEmpty(rawPass))
        {
            xtreamUser = string.Empty;
            xtreamPass = string.Empty;
            return false;
        }

        xtreamUser = Uri.EscapeDataString(rawUser);
        xtreamPass = Uri.EscapeDataString(rawPass);
        return true;
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

    private static string GetRequestValue(HttpRequest request, IFormCollection? form, string key)
    {
        if (request.Query.TryGetValue(key, out var queryValues))
        {
            var queryValue = queryValues.ToString();
            if (!string.IsNullOrEmpty(queryValue))
                return queryValue;
        }

        if (form is not null && form.TryGetValue(key, out var formValues))
            return formValues.ToString();

        return string.Empty;
    }

    private static async Task<IFormCollection?> TryReadFormAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return null;

        try
        {
            return await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static string GetBaseUrl(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";

}
