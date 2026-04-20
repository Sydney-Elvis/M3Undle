namespace M3Undle.Web.Streaming.Observability;

public static class StreamLogClassification
{
    public const string ExternalViewer = "external_viewer";
    public const string InternalHelper = "internal_helper";
    public const string ObservedHlsClient = "observed_hls_client";
    public const string HdhrRoute = "hdhr";
    public const string XtreamRoute = "xtream";
    public const string SharedHlsRoute = "shared_hls";
    public const string DirectOrOtherRoute = "direct_or_other";

    public static string ClassifyRoute(string? requestedRoute)
    {
        if (string.IsNullOrWhiteSpace(requestedRoute))
            return DirectOrOtherRoute;

        var value = requestedRoute.Trim();
        if (value.StartsWith("/hls/generated", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/hls/generated/", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return SharedHlsRoute;
        }

        if (value.StartsWith("/hdhr/tune", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/tune", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/hdhr/auto", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/auto", StringComparison.OrdinalIgnoreCase)
            || IsNativeHdhrTunerPath(value))
        {
            return HdhrRoute;
        }

        if (value.StartsWith("/live/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/movie/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/series/", StringComparison.OrdinalIgnoreCase))
        {
            return XtreamRoute;
        }

        if (value.StartsWith("/hls/", StringComparison.OrdinalIgnoreCase))
            return SharedHlsRoute;

        return DirectOrOtherRoute;
    }

    public static string ClassifySubscriber(bool isInternal, bool isObservedHlsClient = false)
    {
        if (isObservedHlsClient)
            return ObservedHlsClient;

        return isInternal ? InternalHelper : ExternalViewer;
    }

    private static bool IsNativeHdhrTunerPath(string value)
    {
        if (value.StartsWith("/hdhr/tuner", StringComparison.OrdinalIgnoreCase)
            && value.Length > "/hdhr/tuner".Length
            && char.IsDigit(value["/hdhr/tuner".Length]))
        {
            return true;
        }

        return value.StartsWith("/tuner", StringComparison.OrdinalIgnoreCase)
               && value.Length > "/tuner".Length
               && char.IsDigit(value["/tuner".Length]);
    }
}
