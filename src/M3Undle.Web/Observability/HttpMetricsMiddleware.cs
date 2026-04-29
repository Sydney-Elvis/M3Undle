using System.Diagnostics;

namespace M3Undle.Web.Observability;

public sealed class HttpMetricsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, M3UndleMetrics metrics)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var endpoint = context.GetEndpoint();
            var route = endpoint is RouteEndpoint routeEndpoint
                ? routeEndpoint.RoutePattern.RawText ?? context.Request.Path.Value ?? "unknown"
                : context.Request.Path.Value ?? "unknown";
            var duration = Stopwatch.GetElapsedTime(started);
            metrics.RecordHttpRequest(route, context.Request.Method, context.Response.StatusCode, duration);
        }
    }
}
