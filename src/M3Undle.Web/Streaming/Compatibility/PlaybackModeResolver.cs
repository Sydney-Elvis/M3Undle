namespace M3Undle.Web.Streaming.Compatibility;

public static class PlaybackModeResolver
{
    public static bool RequiresHls(HttpContext context, bool forceTs)
        => !forceTs && (HasExplicitHlsRequest(context) || IsBrowserClient(context));

    public static bool HasExplicitHlsRequest(HttpContext context)
    {
        var format = context.Request.Query["format"].ToString();
        return format.Equals("hls", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBrowserClient(HttpContext context)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        // Electron apps (e.g. iptvnator) use mpegts.js/MSE to play MPEG-TS directly;
        // redirecting them to generated HLS causes mpegts.js to receive M3U8 text instead
        // of binary TS packets and fail. Electron UAs always contain "Electron/".
        if (ua.Contains("Electron/", StringComparison.OrdinalIgnoreCase))
            return false;
        return ua.Contains("Mozilla/", StringComparison.OrdinalIgnoreCase);
    }
}
