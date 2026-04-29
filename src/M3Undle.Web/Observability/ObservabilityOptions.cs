using System.Net;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Observability;

public sealed class ObservabilityOptions
{
    public MetricsEndpointOptions Metrics { get; set; } = new();
}

public sealed class MetricsEndpointOptions
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "/metrics";
    public string Mode { get; set; } = MetricsAccessModes.LocalOnly;
    public bool EnableChannelLabels { get; set; }
    public string[] LocalAllowedCidrs { get; set; } = [];

    public string NormalizedPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Path))
                return "/metrics";
            var trimmed = Path.Trim();
            return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        }
    }
}

public static class MetricsAccessModes
{
    public const string Disabled = "Disabled";
    public const string LocalOnly = "LocalOnly";
    public const string Token = "Token";
    public const string Public = "Public";

    public static bool IsValid(string? mode)
        => string.Equals(mode, Disabled, StringComparison.OrdinalIgnoreCase)
           || string.Equals(mode, LocalOnly, StringComparison.OrdinalIgnoreCase)
           || string.Equals(mode, Token, StringComparison.OrdinalIgnoreCase)
           || string.Equals(mode, Public, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, Disabled, StringComparison.OrdinalIgnoreCase)) return Disabled;
        if (string.Equals(mode, Token, StringComparison.OrdinalIgnoreCase)) return Token;
        if (string.Equals(mode, Public, StringComparison.OrdinalIgnoreCase)) return Public;
        return LocalOnly;
    }
}

internal sealed record ParsedIpNetwork(IPAddress Prefix, int PrefixLength)
{
    public bool Contains(IPAddress address)
    {
        var normalizedAddress = NormalizeAddress(address);
        var normalizedPrefix = NormalizeAddress(Prefix);
        var addressBytes = normalizedAddress.GetAddressBytes();
        var prefixBytes = normalizedPrefix.GetAddressBytes();
        if (addressBytes.Length != prefixBytes.Length)
            return false;

        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != prefixBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (prefixBytes[fullBytes] & mask);
    }

    public static bool TryParse(string value, out ParsedIpNetwork network)
    {
        network = default!;
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var normalized = NormalizeAddress(address);
        var maxPrefixLength = normalized.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
            return false;

        network = new ParsedIpNetwork(normalized, prefixLength);
        return true;
    }

    private static IPAddress NormalizeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        return address;
    }
}

internal sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        if (!MetricsAccessModes.IsValid(options.Metrics.Mode))
            return ValidateOptionsResult.Fail(
                $"M3Undle:Observability:Metrics:Mode '{options.Metrics.Mode}' is not valid. Must be Disabled, LocalOnly, Token, or Public.");

        foreach (var cidr in options.Metrics.LocalAllowedCidrs)
        {
            if (!ParsedIpNetwork.TryParse(cidr, out _))
                return ValidateOptionsResult.Fail(
                    $"M3Undle:Observability:Metrics:LocalAllowedCidrs contains an invalid CIDR: '{cidr}'.");
        }

        return ValidateOptionsResult.Success;
    }
}
