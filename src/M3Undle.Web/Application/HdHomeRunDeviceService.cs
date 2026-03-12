using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Application;

public sealed record HdHomeRunDeviceDescriptor(
    string DeviceId,
    string DeviceAuth,
    string FriendlyName,
    string ModelNumber,
    int TunerCount,
    string Manufacturer,
    string FirmwareName,
    string FirmwareVersion);

public sealed class HdHomeRunDeviceService(
    RuntimePaths runtimePaths,
    IOptions<HdHomeRunOptions> options,
    IConfiguration configuration,
    EnvironmentVariableService env,
    ILogger<HdHomeRunDeviceService> logger)
{
    private const string IdentityDirectoryName = "hdhomerun";
    private const string IdentityFileName = "device_identity.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly byte[] DeviceIdChecksumLookup = [0xA, 0x5, 0xF, 0x6, 0x7, 0xC, 0x1, 0xB, 0x9, 0x2, 0x8, 0xD, 0x4, 0x3, 0xE, 0x0];
    private readonly SemaphoreSlim _identityLock = new(1, 1);

    private volatile HdHomeRunIdentityFile? _cachedIdentity;
    private volatile string? _cachedBaseUrl;

    public bool IsEnabled => GetBoolSetting("M3UNDLE_HDHR_ENABLED", options.Value.Enabled);

    public bool IsDiscoveryEnabled => GetBoolSetting("M3UNDLE_HDHR_DISCOVERY_ENABLED", options.Value.DiscoveryEnabled);

    public bool IsSsdpEnabled => GetBoolSetting("M3UNDLE_HDHR_SSDP_ENABLED", options.Value.SsdpEnabled);

    public bool IsSiliconDustDiscoveryEnabled => GetBoolSetting(
        "M3UNDLE_HDHR_SILICONDUST_DISCOVERY_ENABLED",
        options.Value.SiliconDustDiscoveryEnabled);

    public string ResolveBaseUrl(HttpContext? httpContext = null)
    {
        var overrideBaseUrl = NormalizeAdvertisedBaseUrl(GetAdvertisedBaseUrlSetting());
        if (!string.IsNullOrWhiteSpace(overrideBaseUrl))
            return overrideBaseUrl;

        if (TryBuildBaseUrlFromRequest(httpContext) is { Length: > 0 } requestBaseUrl)
        {
            _cachedBaseUrl = requestBaseUrl;
            return requestBaseUrl;
        }

        if (!string.IsNullOrWhiteSpace(_cachedBaseUrl))
            return _cachedBaseUrl;

        var scheme = "http";
        var port = ResolvePort(httpContext);
        var host = ResolveLanHost();
        var fallback = port == 80
            ? $"{scheme}://{host}"
            : $"{scheme}://{host}:{port}";
        _cachedBaseUrl = fallback;
        return fallback;
    }

    public async Task<HdHomeRunDeviceDescriptor> GetDeviceDescriptorAsync(CancellationToken cancellationToken)
    {
        var identity = await GetIdentityAsync(cancellationToken);
        return new HdHomeRunDeviceDescriptor(
            DeviceId: identity.DeviceId,
            DeviceAuth: identity.DeviceAuth,
            FriendlyName: identity.FriendlyName,
            ModelNumber: identity.ModelNumber,
            TunerCount: ResolveTunerCount(),
            Manufacturer: options.Value.Manufacturer,
            FirmwareName: options.Value.FirmwareName,
            FirmwareVersion: options.Value.FirmwareVersion);
    }

    internal async Task<HdHomeRunIdentityFile> GetIdentityAsync(CancellationToken cancellationToken)
    {
        if (_cachedIdentity is not null)
            return _cachedIdentity;

        await _identityLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedIdentity is not null)
                return _cachedIdentity;

            var filePath = GetIdentityPath();
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            if (File.Exists(filePath))
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(filePath, cancellationToken);
                    var parsed = JsonSerializer.Deserialize<HdHomeRunIdentityFile>(raw, JsonOptions);
                    if (parsed is not null && IsValidIdentity(parsed))
                    {
                        _cachedIdentity = NormalizeIdentity(parsed);
                        return _cachedIdentity;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    logger.LogWarning(ex, "Failed to parse HDHomeRun identity file; regenerating.");
                }
            }

            var created = CreateIdentity();
            var json = JsonSerializer.Serialize(created, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
            _cachedIdentity = created;
            logger.LogInformation("HDHomeRun device identity initialized with DeviceID {DeviceId}.", created.DeviceId);
            return created;
        }
        finally
        {
            _identityLock.Release();
        }
    }

    internal static bool IsValidDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length != 8)
            return false;
        if (!uint.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            return false;
        if (parsed == 0 || parsed == uint.MaxValue)
            return false;
        return ValidateDeviceIdChecksum(parsed);
    }

    private string? GetAdvertisedBaseUrlSetting()
    {
        var envValue = env.GetValue("M3UNDLE_HDHR_ADVERTISED_BASE_URL");
        return string.IsNullOrWhiteSpace(envValue) ? options.Value.AdvertisedBaseUrl : envValue;
    }

    private int ResolveTunerCount()
    {
        var envValue = env.GetValue("M3UNDLE_HDHR_TUNER_COUNT");
        if (int.TryParse(envValue, out var parsed))
            return Math.Clamp(parsed, 1, 32);
        return Math.Clamp(options.Value.TunerCount, 1, 32);
    }

    private bool GetBoolSetting(string envVarName, bool defaultValue)
    {
        var envValue = env.GetValue(envVarName);
        return bool.TryParse(envValue, out var parsed) ? parsed : defaultValue;
    }

    private string GetIdentityPath()
        => Path.Combine(runtimePaths.DataDirectory, IdentityDirectoryName, IdentityFileName);

    private HdHomeRunIdentityFile CreateIdentity()
    {
        var friendlyName = string.IsNullOrWhiteSpace(options.Value.FriendlyName)
            ? "M3Undle HDHomeRun"
            : options.Value.FriendlyName.Trim();
        var modelNumber = string.IsNullOrWhiteSpace(options.Value.ModelNumber)
            ? "HDHR3-US"
            : options.Value.ModelNumber.Trim();

        return new HdHomeRunIdentityFile
        {
            DeviceId = GenerateDeviceId(),
            DeviceAuth = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            FriendlyName = friendlyName,
            ModelNumber = modelNumber,
        };
    }

    private static string GenerateDeviceId()
    {
        var bytes = new byte[4];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var baseValue = BitConverter.ToUInt32(bytes, 0) & 0xFFFFFFF0u;
            for (uint nibble = 0; nibble < 16; nibble++)
            {
                var candidate = baseValue | nibble;
                if (candidate == 0 || candidate == uint.MaxValue)
                    continue;
                if (!ValidateDeviceIdChecksum(candidate))
                    continue;
                return candidate.ToString("X8");
            }
        }
    }

    private static bool ValidateDeviceIdChecksum(uint deviceId)
    {
        var checksum = 0;
        checksum ^= DeviceIdChecksumLookup[(int)((deviceId >> 28) & 0x0F)];
        checksum ^= (int)((deviceId >> 24) & 0x0F);
        checksum ^= DeviceIdChecksumLookup[(int)((deviceId >> 20) & 0x0F)];
        checksum ^= (int)((deviceId >> 16) & 0x0F);
        checksum ^= DeviceIdChecksumLookup[(int)((deviceId >> 12) & 0x0F)];
        checksum ^= (int)((deviceId >> 8) & 0x0F);
        checksum ^= DeviceIdChecksumLookup[(int)((deviceId >> 4) & 0x0F)];
        checksum ^= (int)(deviceId & 0x0F);
        return checksum == 0;
    }

    private static bool IsValidIdentity(HdHomeRunIdentityFile identity)
    {
        if (!IsValidDeviceId(identity.DeviceId))
            return false;
        if (string.IsNullOrWhiteSpace(identity.DeviceAuth))
            return false;
        if (string.IsNullOrWhiteSpace(identity.FriendlyName))
            return false;
        if (string.IsNullOrWhiteSpace(identity.ModelNumber))
            return false;
        return true;
    }

    private static HdHomeRunIdentityFile NormalizeIdentity(HdHomeRunIdentityFile identity)
        => new()
        {
            DeviceId = identity.DeviceId.Trim().ToUpperInvariant(),
            DeviceAuth = identity.DeviceAuth.Trim(),
            FriendlyName = identity.FriendlyName.Trim(),
            ModelNumber = identity.ModelNumber.Trim(),
        };

    private static string? TryBuildBaseUrlFromRequest(HttpContext? httpContext)
    {
        if (httpContext is null)
            return null;

        var host = httpContext.Request.Host;
        if (!host.HasValue || IsLocalHost(host.Host))
            return null;

        var scheme = string.IsNullOrWhiteSpace(httpContext.Request.Scheme)
            ? "http"
            : httpContext.Request.Scheme;
        var pathBase = httpContext.Request.PathBase.HasValue
            ? httpContext.Request.PathBase.Value
            : string.Empty;

        return $"{scheme}://{host.Value}{pathBase}".TrimEnd('/');
    }

    private static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var address))
            return false;

        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }

    private static string? NormalizeAdvertisedBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return null;
        if (IsLocalHost(uri.Host))
            return null;

        var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    private int ResolvePort(HttpContext? httpContext)
    {
        if (httpContext?.Request.Host.Port is int requestPort)
            return requestPort;

        if (TryParseFirstPort(configuration["ASPNETCORE_HTTP_PORTS"], out var envPort))
            return envPort;

        if (TryParseFirstUrlPort(configuration["ASPNETCORE_URLS"], out var urlsPort))
            return urlsPort;

        if (TryParseFirstUrlPort(configuration["urls"], out var genericUrlsPort))
            return genericUrlsPort;

        return 8080;
    }

    private static bool TryParseFirstPort(string? ports, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(ports))
            return false;

        foreach (var part in ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out port) && port is > 0 and <= 65535)
                return true;
        }

        return false;
    }

    private static bool TryParseFirstUrlPort(string? urls, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(urls))
            return false;

        foreach (var part in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out var uri))
                continue;
            if (uri.Port <= 0 || uri.Port > 65535)
                continue;
            port = uri.Port;
            return true;
        }

        return false;
    }

    private string ResolveLanHost()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 53);
            if (socket.LocalEndPoint is IPEndPoint { Address: { } localAddress } &&
                !IPAddress.IsLoopback(localAddress))
            {
                return localAddress.ToString();
            }
        }
        catch
        {
            // Ignore and continue to interface enumeration fallback.
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var ip = nic.GetIPProperties().UnicastAddresses
                .Select(x => x.Address)
                .FirstOrDefault(addr =>
                    addr.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr) &&
                    !addr.Equals(IPAddress.Any));

            if (ip is not null)
                return ip.ToString();
        }

        logger.LogWarning("Unable to resolve a non-loopback LAN host for HDHomeRun URLs. Falling back to 0.0.0.0.");
        return "0.0.0.0";
    }

    internal sealed class HdHomeRunIdentityFile
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceAuth { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public string ModelNumber { get; set; } = string.Empty;
    }
}

