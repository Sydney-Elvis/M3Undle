namespace M3Undle.Web.Application;

public sealed class ReverseProxyOptions
{
    /// <summary>
    /// Individual trusted proxy IP addresses (e.g. "192.168.1.1").
    /// Forwarded headers are only honoured when the connecting IP is in this list,
    /// <see cref="TrustedNetworks"/>, or is a loopback address.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>
    /// Trusted proxy CIDR networks (e.g. "10.0.0.0/8").
    /// Forwarded headers are only honoured when the connecting IP belongs to one of
    /// these networks, <see cref="TrustedProxies"/>, or is a loopback address.
    /// </summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>
    /// Maximum number of entries to process in X-Forwarded-* headers.
    /// Defaults to 1 (only a single directly-connected proxy is trusted).
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}
