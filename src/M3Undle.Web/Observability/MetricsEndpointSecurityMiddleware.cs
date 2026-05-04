using System.Net;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Observability;

public sealed class MetricsEndpointSecurityMiddleware(
    RequestDelegate next,
    IOptions<ObservabilityOptions> options,
    ILogger<MetricsEndpointSecurityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = options.Value.Metrics.NormalizedPath;
        if (!context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var settingsService = context.RequestServices.GetRequiredService<IObservabilitySettingsService>();
        var settings = await settingsService.GetSettingsAsync(context.RequestAborted);

        if (!settings.Enabled || string.Equals(settings.Mode, MetricsAccessModes.Disabled, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (string.Equals(settings.Mode, MetricsAccessModes.Public, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (string.Equals(settings.Mode, MetricsAccessModes.LocalOnly, StringComparison.OrdinalIgnoreCase))
        {
            if (IsAllowedLocalAddress(context.Connection.RemoteIpAddress, settings.LocalAllowedCidrs))
            {
                await next(context);
                return;
            }

            logger.LogWarning("Rejected metrics scrape from non-local address {RemoteIp}.", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (string.Equals(settings.Mode, MetricsAccessModes.Token, StringComparison.OrdinalIgnoreCase))
        {
            var token = ExtractToken(context);
            var tokenService = context.RequestServices.GetRequiredService<IMetricsTokenService>();
            if (await tokenService.TryAuthenticateAsync(token, context.RequestAborted))
            {
                await next(context);
                return;
            }

            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    private static string? ExtractToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        if (context.Request.Query.TryGetValue("token", out var queryToken))
            return queryToken.ToString();

        return null;
    }

    private static bool IsAllowedLocalAddress(IPAddress? address, IReadOnlyList<string> cidrs)
    {
        if (address is null)
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        foreach (var cidr in cidrs)
        {
            if (!ParsedIpNetwork.TryParse(cidr, out var network))
                continue;

            if (network.Contains(normalized))
                return true;
        }

        return false;
    }

}
