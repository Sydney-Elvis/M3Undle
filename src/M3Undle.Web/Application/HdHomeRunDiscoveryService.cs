using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace M3Undle.Web.Application;

public sealed class HdHomeRunDiscoveryService(
    HdHomeRunDeviceService deviceService,
    ILogger<HdHomeRunDiscoveryService> logger)
    : BackgroundService
{
    private const int SsdpPort = 1900;
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const int DiscoverPort = 65001;

    private const ushort DiscoverRequestType = 0x0002;
    private const ushort DiscoverResponseType = 0x0003;

    private const uint DeviceTypeTuner = 0x00000001;
    private const uint DeviceTypeWildcard = 0xFFFFFFFF;
    private const uint DeviceIdWildcard = 0xFFFFFFFF;

    private const byte TagDeviceType = 0x01;
    private const byte TagDeviceId = 0x02;
    private const byte TagTunerCount = 0x10;
    private const byte TagLineupUrl = 0x27;
    private const byte TagBaseUrl = 0x2A;
    private const byte TagDeviceAuth = 0x2B;
    private const byte TagMultiType = 0x2D;

    private const string SsdpMediaServerType = "urn:schemas-upnp-org:device:MediaServer:1";
    private const string SsdpRootType = "upnp:rootdevice";
    private const string SsdpAllType = "ssdp:all";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!deviceService.IsEnabled)
        {
            logger.LogInformation("HDHomeRun discovery not started because HDHomeRun endpoints are disabled.");
            return;
        }

        if (!deviceService.IsDiscoveryEnabled)
        {
            logger.LogInformation("HDHomeRun discovery is disabled (manual add endpoints remain enabled).");
            return;
        }

        var workers = new List<Task>(2);

        if (deviceService.IsSsdpEnabled)
            workers.Add(RunSsdpListenerAsync(stoppingToken));

        if (deviceService.IsSiliconDustDiscoveryEnabled)
            workers.Add(RunSiliconDustListenerAsync(stoppingToken));

        if (workers.Count == 0)
        {
            logger.LogInformation("HDHomeRun discovery is enabled but all discovery protocols are disabled.");
            return;
        }

        await Task.WhenAll(workers);
    }

    private async Task RunSsdpListenerAsync(CancellationToken cancellationToken)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
            udp.JoinMulticastGroup(IPAddress.Parse(SsdpMulticastAddress));
            logger.LogInformation("HDHomeRun SSDP listener started on UDP {Port}.", SsdpPort);

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cancellationToken);
                if (!TryParseSsdpSearchTarget(result.Buffer, out var searchTarget, out var mxSeconds))
                    continue;

                // Respect SSDP MX delay per UPnP DA spec §1.3.3
                var maxDelayMs = Math.Min(mxSeconds * 1000, 5_000);
                await Task.Delay(Random.Shared.Next(0, maxDelayMs + 1), cancellationToken);

                var device = await deviceService.GetDeviceDescriptorAsync(cancellationToken);
                var baseUrl = deviceService.ResolveBaseUrl();
                var effectiveSearchTarget = string.Equals(searchTarget, SsdpAllType, StringComparison.OrdinalIgnoreCase)
                    ? SsdpMediaServerType
                    : searchTarget;

                var response = BuildSsdpResponse(effectiveSearchTarget, device.DeviceId, baseUrl);
                var bytes = Encoding.ASCII.GetBytes(response);
                await udp.SendAsync(bytes, result.RemoteEndPoint, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "HDHomeRun SSDP listener stopped due to socket error.");
        }
        finally
        {
            udp?.Dispose();
            logger.LogInformation("HDHomeRun SSDP listener stopped.");
        }
    }

    private async Task RunSiliconDustListenerAsync(CancellationToken cancellationToken)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoverPort));
            logger.LogInformation("HDHomeRun SiliconDust discovery listener started on UDP {Port}.", DiscoverPort);

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cancellationToken);
                if (!TryParseDiscoverRequest(result.Buffer, out var request))
                    continue;

                var device = await deviceService.GetDeviceDescriptorAsync(cancellationToken);
                var deviceId = Convert.ToUInt32(device.DeviceId, 16);
                if (!ShouldRespondToDiscover(request, deviceId))
                    continue;

                var baseUrl = deviceService.ResolveBaseUrl();
                var lineupUrl = $"{baseUrl}/lineup.json";
                var packet = BuildDiscoverResponsePacket(
                    deviceId,
                    (byte)Math.Clamp(device.TunerCount, 1, 255),
                    device.DeviceAuth,
                    baseUrl,
                    lineupUrl);

                await udp.SendAsync(packet, result.RemoteEndPoint, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "HDHomeRun SiliconDust discovery listener stopped due to socket error.");
        }
        finally
        {
            udp?.Dispose();
            logger.LogInformation("HDHomeRun SiliconDust discovery listener stopped.");
        }
    }

    private static bool TryParseSsdpSearchTarget(byte[] payload, out string searchTarget, out int mxSeconds)
    {
        searchTarget = string.Empty;
        mxSeconds = 1;
        if (payload.Length == 0)
            return false;

        var text = Encoding.ASCII.GetString(payload);
        if (!text.StartsWith("M-SEARCH * HTTP/1.1", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("MX:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line[3..].Trim(), out var mx) && mx > 0)
                    mxSeconds = Math.Min(mx, 120);
                continue;
            }

            if (!line.StartsWith("ST:", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = line[3..].Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            if (string.Equals(candidate, SsdpAllType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, SsdpRootType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, SsdpMediaServerType, StringComparison.OrdinalIgnoreCase))
            {
                searchTarget = candidate;
                return true;
            }
        }

        return false;
    }

    private static string BuildSsdpResponse(string searchTarget, string deviceId, string baseUrl)
    {
        var dateHeader = DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        var location = $"{baseUrl}/device.xml";
        var usn = $"uuid:{deviceId}::{searchTarget}";

        return string.Join("\r\n",
        [
            "HTTP/1.1 200 OK",
            "CACHE-CONTROL: max-age=1800",
            $"DATE: {dateHeader}",
            "EXT:",
            $"LOCATION: {location}",
            "SERVER: M3Undle/1.0 UPnP/1.0",
            $"ST: {searchTarget}",
            $"USN: {usn}",
            "",
            "",
        ]);
    }

    private static bool ShouldRespondToDiscover(DiscoverRequest request, uint deviceId)
    {
        if (!request.AcceptsTuners)
            return false;

        if (request.DeviceIdFilter.HasValue &&
            request.DeviceIdFilter.Value != DeviceIdWildcard &&
            request.DeviceIdFilter.Value != deviceId)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseDiscoverRequest(ReadOnlySpan<byte> datagram, out DiscoverRequest request)
    {
        request = default;
        if (datagram.Length < 8)
            return false;

        var packetType = BinaryPrimitives.ReadUInt16BigEndian(datagram[..2]);
        if (packetType != DiscoverRequestType)
            return false;

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(2, 2));
        var frameLength = 4 + payloadLength;
        if (datagram.Length != frameLength + 4)
            return false;

        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(frameLength, 4));
        var calculatedCrc = CalculateEthernetCrc(datagram[..frameLength]);
        if (calculatedCrc != expectedCrc)
            return false;

        var payload = datagram.Slice(4, payloadLength);
        uint? deviceIdFilter = null;
        var hasDeviceTypeTag = false;
        var acceptsTuners = false;

        var offset = 0;
        while (offset < payload.Length)
        {
            var tag = payload[offset++];
            if (!TryReadVarLength(payload, ref offset, out var valueLength))
                return false;
            if (offset + valueLength > payload.Length)
                return false;

            var value = payload.Slice(offset, valueLength);
            offset += valueLength;

            switch (tag)
            {
                case TagDeviceType when value.Length == 4:
                {
                    hasDeviceTypeTag = true;
                    var deviceType = BinaryPrimitives.ReadUInt32BigEndian(value);
                    if (deviceType is DeviceTypeTuner or DeviceTypeWildcard)
                        acceptsTuners = true;
                    break;
                }
                case TagMultiType when value.Length % 4 == 0:
                {
                    hasDeviceTypeTag = true;
                    for (var i = 0; i < value.Length; i += 4)
                    {
                        var deviceType = BinaryPrimitives.ReadUInt32BigEndian(value.Slice(i, 4));
                        if (deviceType is DeviceTypeTuner or DeviceTypeWildcard)
                        {
                            acceptsTuners = true;
                            break;
                        }
                    }
                    break;
                }
                case TagDeviceId when value.Length == 4:
                    deviceIdFilter = BinaryPrimitives.ReadUInt32BigEndian(value);
                    break;
            }
        }

        if (!hasDeviceTypeTag)
            acceptsTuners = true;

        request = new DiscoverRequest(deviceIdFilter, acceptsTuners);
        return true;
    }

    private static byte[] BuildDiscoverResponsePacket(
        uint deviceId,
        byte tunerCount,
        string deviceAuth,
        string baseUrl,
        string lineupUrl)
    {
        var payload = new List<byte>(256);
        WriteTlvU32(payload, TagDeviceType, DeviceTypeTuner);
        WriteTlvU32(payload, TagDeviceId, deviceId);
        WriteTlvByte(payload, TagTunerCount, tunerCount);
        WriteTlvString(payload, TagDeviceAuth, deviceAuth);
        WriteTlvString(payload, TagBaseUrl, baseUrl);
        WriteTlvString(payload, TagLineupUrl, lineupUrl);

        var packet = new byte[4 + payload.Count + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet, DiscoverResponseType);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)payload.Count);
        payload.CopyTo(packet, 4);

        var crc = CalculateEthernetCrc(packet.AsSpan(0, 4 + payload.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4 + payload.Count), crc);
        return packet;
    }

    private static void WriteTlvU32(List<byte> output, byte tag, uint value)
    {
        output.Add(tag);
        WriteVarLength(output, 4);
        output.Add((byte)(value >> 24));
        output.Add((byte)(value >> 16));
        output.Add((byte)(value >> 8));
        output.Add((byte)value);
    }

    private static void WriteTlvByte(List<byte> output, byte tag, byte value)
    {
        output.Add(tag);
        WriteVarLength(output, 1);
        output.Add(value);
    }

    private static void WriteTlvString(List<byte> output, byte tag, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        output.Add(tag);
        WriteVarLength(output, bytes.Length);
        output.AddRange(bytes);
    }

    private static void WriteVarLength(List<byte> output, int value)
    {
        if (value <= 127)
        {
            output.Add((byte)value);
            return;
        }

        output.Add((byte)((value & 0x7F) | 0x80));
        output.Add((byte)(value >> 7));
    }

    private static bool TryReadVarLength(ReadOnlySpan<byte> payload, ref int offset, out int valueLength)
    {
        valueLength = 0;
        if (offset >= payload.Length)
            return false;

        var first = payload[offset++];
        if ((first & 0x80) == 0)
        {
            valueLength = first;
            return true;
        }

        if (offset >= payload.Length)
            return false;

        var second = payload[offset++];
        valueLength = (first & 0x7F) | (second << 7);
        return true;
    }

    private static uint CalculateEthernetCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            var x = (crc ^ value) & 0xFF;
            crc >>= 8;
            if ((x & 0x01) != 0) crc ^= 0x77073096;
            if ((x & 0x02) != 0) crc ^= 0xEE0E612C;
            if ((x & 0x04) != 0) crc ^= 0x076DC419;
            if ((x & 0x08) != 0) crc ^= 0x0EDB8832;
            if ((x & 0x10) != 0) crc ^= 0x1DB71064;
            if ((x & 0x20) != 0) crc ^= 0x3B6E20C8;
            if ((x & 0x40) != 0) crc ^= 0x76DC4190;
            if ((x & 0x80) != 0) crc ^= 0xEDB88320;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private readonly record struct DiscoverRequest(uint? DeviceIdFilter, bool AcceptsTuners);
}

