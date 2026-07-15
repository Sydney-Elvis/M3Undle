using System.Net;

namespace M3Undle.Web.Streaming.Observability;

public static class RemoteIpAddressFormatter
{
    public static string? Format(IPAddress? address)
    {
        if (address is null)
            return null;

        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : address.ToString();
    }

    public static string? Format(string? address)
    {
        if (!IPAddress.TryParse(address, out var parsed))
            return address;

        return Format(parsed);
    }
}
