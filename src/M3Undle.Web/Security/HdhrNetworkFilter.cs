using System.Net;
using M3Undle.Web.Application;
using M3Undle.Web.Observability;

namespace M3Undle.Web.Security;

internal sealed class HdhrNetworkFilter(
    IHdHomeRunSettingsService hdhrSettings,
    ILogger<HdhrNetworkFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var allowedNetworks = await hdhrSettings.GetAllowedNetworksAsync(http.RequestAborted);

        if (string.IsNullOrWhiteSpace(allowedNetworks))
            return await next(context);

        var remoteIp = http.Connection.RemoteIpAddress;

        if (remoteIp is null)
        {
            logger.LogWarning("HDHR access denied — could not determine remote IP.");
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (IPAddress.IsLoopback(remoteIp))
            return await next(context);

        var normalized = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;

        foreach (var network in ParseNetworks(allowedNetworks))
        {
            if (network.Contains(normalized))
                return await next(context);
        }

        logger.LogWarning(
            "HDHR access denied from {RemoteIp} — not in allowed networks.",
            remoteIp);
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static IEnumerable<ParsedIpNetwork> ParseNetworks(string value)
    {
        foreach (var part in value.Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParsedIpNetwork.TryParse(part, out var network))
                yield return network;
        }
    }
}
