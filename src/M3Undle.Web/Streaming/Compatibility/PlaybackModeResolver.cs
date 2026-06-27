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

    // Returns true for clients known to burst-buffer (ExoPlayer/Media3, Roku) where
    // direct TS push reliably loses to the client's own buffer management.
    public static bool IsBurstBufferingClient(HttpContext context)
        => IsBurstBufferingUserAgent(context.Request.Headers.UserAgent.ToString());

    public static bool IsBurstBufferingUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return false;
        if (userAgent.Contains("Roku", StringComparison.OrdinalIgnoreCase))
            return true;
        if (userAgent.Contains("SmartersPro", StringComparison.OrdinalIgnoreCase))
            return true;
        if (userAgent.StartsWith("Dalvik/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (userAgent.StartsWith("okhttp/", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
