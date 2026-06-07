namespace M3Undle.Web.Application;

public sealed class EndpointUrlService(EnvironmentVariableService env)
{
    private static readonly Lazy<ContainerInfo> _containerInfo
        = new(DetectContainerInfo, LazyThreadSafetyMode.ExecutionAndPublication);

    public string? GetPublicBaseUrl() => NormalizeUrl(env.GetValue("M3UNDLE_PUBLIC_BASE_URL"));
    public string? GetDockerBaseUrl() => NormalizeUrl(env.GetValue("M3UNDLE_DOCKER_BASE_URL"));
    public string? GetExternalBaseUrl() => NormalizeUrl(env.GetValue("M3UNDLE_EXTERNAL_BASE_URL"));

    public bool IsPublicBaseUrlFromEnv => !string.IsNullOrWhiteSpace(env.GetValue("M3UNDLE_PUBLIC_BASE_URL"));
    public bool IsDockerBaseUrlFromEnv => !string.IsNullOrWhiteSpace(env.GetValue("M3UNDLE_DOCKER_BASE_URL"));
    public bool IsExternalBaseUrlFromEnv => !string.IsNullOrWhiteSpace(env.GetValue("M3UNDLE_EXTERNAL_BASE_URL"));

    public bool IsContainerDetected => _containerInfo.Value.IsContainer;
    public string DetectedHostname => _containerInfo.Value.Hostname;
    public bool IsHostnameLikelyContainerId => IsShortHexId(DetectedHostname);

    private static ContainerInfo DetectContainerInfo()
    {
        var hostname = TryGetHostname();
        var isContainer =
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            || File.Exists("/.dockerenv");
        return new ContainerInfo(isContainer, hostname);
    }

    private static string TryGetHostname()
    {
        try { return System.Net.Dns.GetHostName(); }
        catch { return Environment.MachineName; }
    }

    private static bool IsShortHexId(string hostname)
        => (hostname.Length == 12 || hostname.Length == 64) && hostname.All(c => char.IsAsciiHexDigit(c));

    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : null;
    }

    private sealed record ContainerInfo(bool IsContainer, string Hostname);
}
