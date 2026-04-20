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
        => context.Request.Headers.UserAgent.ToString()
            .Contains("Mozilla/", StringComparison.OrdinalIgnoreCase);
}
