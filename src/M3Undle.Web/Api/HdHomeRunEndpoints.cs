using System.Text;
using System.Text.Json;
using System.Xml;
using M3Undle.Web.Application;
using M3Undle.Web.Security;

namespace M3Undle.Web.Api;

public static class HdHomeRunEndpoints
{
    private const string HdhrEventType = "HDHR";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.ReplaceLineEndings(" ");

    public static IEndpointRouteBuilder MapHdHomeRunEndpoints(this IEndpointRouteBuilder app)
    {
        var client = app.MapClientSurface();
        var hdhr = client.MapGroup("hdhr");

        hdhr.MapGet("discover.json", ServeDiscoverAsync);
        hdhr.MapGet("lineup.json", ServeLineupJsonAsync);
        hdhr.MapGet("lineup.xml", ServeLineupXmlAsync);
        hdhr.MapGet("lineup.m3u", ServeLineupM3uAsync);
        hdhr.MapGet("lineup_status.json", ServeLineupStatusAsync);
        hdhr.MapGet("lineup.post", ServeLineupPost);
        hdhr.MapPost("lineup.post", ServeLineupPost);
        hdhr.MapGet("device.xml", ServeDeviceXmlAsync);

        // Legacy aliases kept for HDHR client compatibility.
        client.MapGet("discover.json", ServeDiscoverAsync);
        client.MapGet("lineup.json", ServeLineupJsonAsync);
        client.MapGet("lineup.xml", ServeLineupXmlAsync);
        client.MapGet("lineup.m3u", ServeLineupM3uAsync);
        client.MapGet("lineup_status.json", ServeLineupStatusAsync);
        client.MapGet("lineup.post", ServeLineupPost);
        client.MapPost("lineup.post", ServeLineupPost);
        client.MapGet("device.xml", ServeDeviceXmlAsync);

        return app;
    }

    private static async Task<IResult> ServeDiscoverAsync(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!deviceService.IsEnabled)
            return TypedResults.NotFound();

        var logger = CreateLogger(loggerFactory);
        using var scope = BeginHdhrScope(logger);

        var device = await deviceService.GetDeviceDescriptorAsync(cancellationToken);
        var baseUrl = deviceService.ResolveBaseUrl(context).TrimEnd('/');
        var hdhrBaseUrl = $"{baseUrl}/hdhr";

        var payload = new DiscoverResponse(
            FriendlyName: device.FriendlyName,
            Manufacturer: device.Manufacturer,
            ModelNumber: device.ModelNumber,
            FirmwareName: device.FirmwareName,
            FirmwareVersion: device.FirmwareVersion,
            DeviceID: device.DeviceId,
            DeviceAuth: device.DeviceAuth,
            BaseURL: baseUrl,
            LineupURL: $"{hdhrBaseUrl}/lineup.json".ApplyClientAccessQuery(context),
            TunerCount: device.TunerCount);

        logger.LogInformation(
            "HDHomeRun discover.json served to {Client}. path={Path} deviceId={DeviceId} tunerCount={TunerCount} baseUrl={BaseUrl}",
            DescribeClient(context),
            SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/discover.json",
            device.DeviceId,
            device.TunerCount,
            baseUrl);

        return TypedResults.Json(payload, JsonOptions);
    }

    private static async Task<IResult> ServeLineupJsonAsync(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService lineupService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = CreateLogger(loggerFactory);
        using var scope = BeginHdhrScope(logger);

        var lineupResult = await TryBuildHdhrLineupAsync(context, deviceService, lineupRenderer, lineupService, cancellationToken);
        if (!lineupResult.Succeeded)
        {
            logger.LogWarning(
                "HDHomeRun lineup.json unavailable for {Client}. path={Path}",
                DescribeClient(context),
                SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup.json");
            return lineupResult.ErrorResult!;
        }

        var payload = lineupResult.Lineup!.Channels
            .Select(x => new LineupChannelResponse(
                GuideNumber: x.GuideNumber,
                GuideName: x.GuideName,
                URL: x.Url))
            .ToList();

        logger.LogInformation(
            "HDHomeRun lineup.json served to {Client}. path={Path} snapshot={SnapshotId} channels={ChannelCount}",
            DescribeClient(context),
            SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup.json",
            lineupResult.Lineup.SnapshotId,
            payload.Count);

        return TypedResults.Json(payload, JsonOptions);
    }

    private static async Task<IResult> ServeLineupXmlAsync(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService lineupService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!deviceService.IsEnabled)
            return TypedResults.NotFound();

        var logger = CreateLogger(loggerFactory);
        using var scope = BeginHdhrScope(logger);

        var lineupResult = await TryBuildHdhrLineupAsync(context, deviceService, lineupRenderer, lineupService, cancellationToken);
        if (!lineupResult.Succeeded)
        {
            logger.LogWarning(
                "HDHomeRun lineup.xml unavailable for {Client}. path={Path}",
                DescribeClient(context),
                SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup.xml");
            return lineupResult.ErrorResult!;
        }

        var lineup = lineupResult.Lineup!;

        using var ms = new MemoryStream(2048);
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            Indent = false,
        };

        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Lineup");
            foreach (var channel in lineup.Channels)
            {
                writer.WriteStartElement("Program");
                writer.WriteElementString("GuideNumber", channel.GuideNumber);
                writer.WriteElementString("GuideName", channel.GuideName);
                writer.WriteElementString("URL", channel.Url);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        logger.LogInformation(
            "HDHomeRun lineup.xml served to {Client}. path={Path} snapshot={SnapshotId} channels={ChannelCount}",
            DescribeClient(context),
            SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup.xml",
            lineup.SnapshotId,
            lineup.Channels.Count);

        return TypedResults.Bytes(ms.ToArray(), "application/xml; charset=utf-8");
    }

    private static async Task<IResult> ServeLineupM3uAsync(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService lineupService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!deviceService.IsEnabled)
            return TypedResults.NotFound();

        var logger = CreateLogger(loggerFactory);
        using var scope = BeginHdhrScope(logger);

        var lineupResult = await TryBuildHdhrLineupAsync(context, deviceService, lineupRenderer, lineupService, cancellationToken);
        if (!lineupResult.Succeeded)
        {
            logger.LogWarning(
                "HDHomeRun lineup.m3u unavailable for {Client}. path={Path}",
                DescribeClient(context),
                SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup.m3u");
            return lineupResult.ErrorResult!;
        }

        var lineup = lineupResult.Lineup!;

        var sb = new StringBuilder(4096);
        sb.Append("#EXTM3U").Append('\n');
        foreach (var channel in lineup.Channels)
        {
            sb.Append("#EXTINF:-1");
            if (!string.IsNullOrWhiteSpace(channel.TvgId))
                sb.Append(" tvg-id=\"").Append(channel.TvgId).Append('"');

            sb.Append(" tvg-chno=\"").Append(channel.GuideNumber).Append('"');
            if (!string.IsNullOrWhiteSpace(channel.LogoUrl))
                sb.Append(" tvg-logo=\"").Append(channel.LogoUrl).Append('"');

            sb.Append(',').Append(channel.GuideName).Append('\n');
            sb.Append(channel.Url).Append('\n');
        }

        logger.LogInformation(
            "HDHomeRun lineup.m3u served to {Client}. path={Path} snapshot={SnapshotId} channels={ChannelCount}",
            DescribeClient(context),
            SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup.m3u",
            lineup.SnapshotId,
            lineup.Channels.Count);

        return TypedResults.Text(sb.ToString(), "application/x-mpegurl; charset=utf-8");
    }

    private static async Task<IResult> ServeLineupStatusAsync(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService lineupService,
        IRefreshTrigger refreshTrigger,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!deviceService.IsEnabled)
            return TypedResults.NotFound();

        var logger = CreateLogger(loggerFactory);
        using var scope = BeginHdhrScope(logger);

        var channelCount = 0;
        var isRefreshing = refreshTrigger.IsRefreshing;
        var status = "No active snapshot";

        var lineupResult = await TryBuildHdhrLineupAsync(context, deviceService, lineupRenderer, lineupService, cancellationToken);
        if (lineupResult.Succeeded)
        {
            channelCount = lineupResult.Lineup!.Channels.Count;
            status = isRefreshing
                ? $"Refreshing ({channelCount} channels)"
                : $"Ready ({channelCount} channels)";
        }
        else if (isRefreshing)
        {
            status = "Scanning for channels";
        }
        else if (lineupResult.ErrorResult is IStatusCodeHttpResult { StatusCode: StatusCodes.Status503ServiceUnavailable })
        {
            status = "Snapshot data unavailable";
        }

        var payload = new LineupStatusResponse(
            ScanInProgress: isRefreshing ? 1 : 0,
            ScanPossible: 1,
            Source: "Cable",
            SourceList: ["Cable"],
            Status: status,
            ChannelCount: channelCount);

        logger.LogInformation(
            "HDHomeRun lineup_status.json served to {Client}. path={Path} status={Status} channels={ChannelCount}",
            DescribeClient(context),
            SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup_status.json",
            status,
            channelCount);

        return TypedResults.Json(payload, JsonOptions);
    }

    private static IResult ServeLineupPost(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILoggerFactory loggerFactory)
    {
        if (!deviceService.IsEnabled)
            return TypedResults.NotFound();

        var logger = CreateLogger(loggerFactory);
        using var scope = BeginHdhrScope(logger);
        logger.LogInformation(
            "HDHomeRun lineup.post acknowledged for {Client}. path={Path} method={Method}",
            DescribeClient(context),
            SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/lineup.post",
            context.Request.Method);

        return TypedResults.Text("OK", "text/plain; charset=utf-8");
    }

    private static async Task<IResult> ServeDeviceXmlAsync(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!deviceService.IsEnabled)
            return TypedResults.NotFound();

        var logger = CreateLogger(loggerFactory);
        using var scope = BeginHdhrScope(logger);

        var device = await deviceService.GetDeviceDescriptorAsync(cancellationToken);
        var baseUrl = deviceService.ResolveBaseUrl(context).TrimEnd('/');
        var hdhrBaseUrl = $"{baseUrl}/hdhr";

        using var ms = new MemoryStream(2048);
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            Indent = false,
        };

        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("root", "urn:schemas-upnp-org:device-1-0");
            writer.WriteStartElement("specVersion");
            writer.WriteElementString("major", "1");
            writer.WriteElementString("minor", "0");
            writer.WriteEndElement();

            writer.WriteElementString("URLBase", $"{baseUrl}/");
            writer.WriteStartElement("device");
            writer.WriteElementString("deviceType", "urn:schemas-upnp-org:device:MediaServer:1");
            writer.WriteElementString("friendlyName", device.FriendlyName);
            writer.WriteElementString("manufacturer", device.Manufacturer);
            writer.WriteElementString("modelDescription", device.ModelNumber);
            writer.WriteElementString("modelName", device.ModelNumber);
            writer.WriteElementString("modelNumber", device.ModelNumber);
            writer.WriteElementString("serialNumber", device.DeviceId);
            writer.WriteElementString("UDN", $"uuid:{device.DeviceId}");
            writer.WriteElementString("presentationURL", baseUrl);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        logger.LogInformation(
            "HDHomeRun device.xml served to {Client}. path={Path} deviceId={DeviceId}",
            DescribeClient(context),
            SanitizeForLog(context.Request.Path.Value) ?? "/hdhr/device.xml",
            device.DeviceId);

        return TypedResults.Bytes(ms.ToArray(), "application/xml; charset=utf-8");
    }

    private static async Task<LineupBuildResult> TryBuildHdhrLineupAsync(
        HttpContext context,
        HdHomeRunDeviceService deviceService,
        ILineupRenderer lineupRenderer,
        HdHomeRunLineupService lineupService,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = context.GetResolvedClientAccess();
            var renderedLineup = await lineupRenderer.TryRenderActiveLineupAsync(access.Binding.ActiveProfileId, cancellationToken);
            if (renderedLineup is null)
            {
                return LineupBuildResult.Failure(
                    TypedResults.Problem(
                        "No active snapshot available.",
                        statusCode: StatusCodes.Status503ServiceUnavailable));
            }

            var baseUrl = deviceService.ResolveBaseUrl(context).TrimEnd('/');
            var lineup = await lineupService.TryBuildActiveLineupAsync(baseUrl, renderedLineup, context, cancellationToken);
            if (lineup is null)
            {
                return LineupBuildResult.Failure(
                    TypedResults.Problem(
                        "No active snapshot available.",
                        statusCode: StatusCodes.Status503ServiceUnavailable));
            }

            return LineupBuildResult.Success(lineup);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return LineupBuildResult.Failure(
                TypedResults.Problem(
                    "Active snapshot data is unavailable.",
                    statusCode: StatusCodes.Status503ServiceUnavailable));
        }
    }

    private sealed record DiscoverResponse(
        string FriendlyName,
        string Manufacturer,
        string ModelNumber,
        string FirmwareName,
        string FirmwareVersion,
        string DeviceID,
        string DeviceAuth,
        string BaseURL,
        string LineupURL,
        int TunerCount);

    private sealed record LineupChannelResponse(string GuideNumber, string GuideName, string URL);

    private sealed record LineupStatusResponse(
        int ScanInProgress,
        int ScanPossible,
        string Source,
        IReadOnlyList<string> SourceList,
        string Status,
        int ChannelCount);

    private sealed record LineupBuildResult(HdHomeRunLineupResult? Lineup, IResult? ErrorResult)
    {
        public bool Succeeded => Lineup is not null;

        public static LineupBuildResult Success(HdHomeRunLineupResult lineup) => new(lineup, null);

        public static LineupBuildResult Failure(IResult error) => new(null, error);
    }

    private static ILogger CreateLogger(ILoggerFactory loggerFactory)
        => loggerFactory.CreateLogger($"{typeof(HdHomeRunEndpoints).FullName}.Access");

    private static IDisposable? BeginHdhrScope(ILogger logger)
        => logger.BeginScope(new Dictionary<string, object> { ["EventType"] = HdhrEventType });

    private static string DescribeClient(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

}
