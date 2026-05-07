using System.Text;

// Cross-platform fake ffmpeg for GeneratedHlsSessionManager tests.
//
// Behavior is controlled by a flag file, not an environment variable, so
// parallel test runs with different temp roots don't interfere.
//
// Flag resolution: the manager passes manifestPath as the last argument.
// manifestPath = {root}/generated-hls/{sessionId}/index.m3u8
// Flag file    = {root}/write.flag  (two directories up from sessionDir)
//
// If the flag file exists the fake writes a minimal HLS manifest + segment
// and then holds until killed.  Without the flag it just holds forever.

if (args.Length > 0 && args[0] == "-version")
{
    Console.WriteLine("ffmpeg version fake");
    return 0;
}

if (args.Length == 0)
    return 1;

if (string.Equals(args[^1], "pipe:1", StringComparison.OrdinalIgnoreCase))
{
    return await RunRelayModeAsync(args);
}

var manifestPath = args[^1];
var sessionDir = Path.GetDirectoryName(manifestPath) ?? ".";
var hlsDir = Path.GetDirectoryName(sessionDir) ?? ".";
var rootDir = Path.GetDirectoryName(hlsDir) ?? ".";
var flagFile = Path.Combine(rootDir, "write.flag");
var inputUrl = GetInputUrl(args);

var argsOut = GetQueryValue(inputUrl, "argsOut");
if (!string.IsNullOrWhiteSpace(argsOut))
    await File.WriteAllTextAsync(argsOut, string.Join("\n", args));

if (File.Exists(flagFile))
{
    Directory.CreateDirectory(sessionDir);
    await File.WriteAllTextAsync(manifestPath, "#EXTM3U\n#EXTINF:4.0,\nsegment_000001.ts\n");
    await File.WriteAllBytesAsync(
        Path.Combine(sessionDir, "segment_000001.ts"),
        "segment"u8.ToArray());
}

// Hold until the test kills the process.
await Task.Delay(Timeout.Infinite);
return 0;

static async Task<int> RunRelayModeAsync(string[] args)
{
    var inputUrl = GetInputUrl(args);
    var stdout = Console.OpenStandardOutput();
    var mode = GetQueryValue(inputUrl, "ffmpegMode") ?? "relay-stall";

    // Write all args to a file before producing stdout — lets tests inspect FFmpeg arguments.
    var argsOut = GetQueryValue(inputUrl, "argsOut");
    if (!string.IsNullOrWhiteSpace(argsOut))
        await File.WriteAllTextAsync(argsOut, string.Join("\n", args));

    switch (mode)
    {
        case "relay-success":
        {
            var prefix = GetQueryValue(inputUrl, "prefix") ?? "HEAD";
            var suffix = GetQueryValue(inputUrl, "suffix") ?? "TAIL";
            var delayMs = int.TryParse(GetQueryValue(inputUrl, "delayMs"), out var parsedDelayMs)
                ? parsedDelayMs
                : 1000;

            await stdout.WriteAsync(Encoding.ASCII.GetBytes(prefix));
            await stdout.FlushAsync();
            await Task.Delay(delayMs);
            await stdout.WriteAsync(Encoding.ASCII.GetBytes(suffix));
            await stdout.FlushAsync();
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        case "relay-ts-sequence":
        {
            var cycles = int.TryParse(GetQueryValue(inputUrl, "cycles"), out var parsedCycles)
                ? parsedCycles
                : 0;
            var delayMs = int.TryParse(GetQueryValue(inputUrl, "delayMs"), out var parsedDelayMs)
                ? parsedDelayMs
                : 5;
            var exitAfterCycles = cycles > 0;
            var writtenCycles = 0;

            while (!exitAfterCycles || writtenCycles < cycles)
            {
                foreach (var packet in CreateTsSequence())
                {
                    await stdout.WriteAsync(packet);
                    await stdout.FlushAsync();
                    if (delayMs > 0)
                        await Task.Delay(delayMs);
                }
                writtenCycles++;
            }

            return 0;
        }
        case "relay-eof":
            await stdout.FlushAsync();
            return 0;
        case "relay-stall":
        default:
            await Task.Delay(Timeout.Infinite);
            return 0;
    }
}

static string? GetInputUrl(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "-i")
            return args[i + 1];
    }

    return null;
}

static byte[][] CreateTsSequence()
{
    byte[] patPayload =
    [
        0x00,
        0x00, 0xB0, 0x0D,
        0x00, 0x01,
        0xC1, 0x00, 0x00,
        0x00, 0x01,
        0xE0, 0x64,
        0x00, 0x00, 0x00, 0x00,
    ];

    byte[] pmtPayload =
    [
        0x00,
        0x02, 0xB0, 0x12,
        0x00, 0x01,
        0xC1, 0x00, 0x00,
        0xE1, 0x00,
        0xF0, 0x00,
        0x1B,
        0xE1, 0x00,
        0xF0, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    byte[] pesHeader = [0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x00];

    return
    [
        TsPacket(0, patPayload, payloadUnitStart: true),
        TsPacket(100, pmtPayload, payloadUnitStart: true),
        TsPacket(256, Append(pesHeader, [0x00, 0x00, 0x01, 0x67, 0x42, 0x00]), payloadUnitStart: true),
        TsPacket(256, Append(pesHeader, [0x00, 0x00, 0x01, 0x68, 0x01, 0x00]), payloadUnitStart: true),
        TsPacket(256, Append(pesHeader, [0x00, 0x00, 0x01, 0x65, 0x00, 0x00]), payloadUnitStart: true),
    ];
}

static byte[] Append(byte[] prefix, byte[] suffix)
{
    var result = new byte[prefix.Length + suffix.Length];
    prefix.CopyTo(result, 0);
    suffix.CopyTo(result, prefix.Length);
    return result;
}

static byte[] TsPacket(int pid, byte[] payload, bool payloadUnitStart = false)
{
    var packet = Enumerable.Repeat((byte)0xFF, 188).ToArray();
    packet[0] = 0x47;
    packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
    packet[2] = (byte)pid;
    packet[3] = 0x10;
    payload.CopyTo(packet.AsSpan(4, Math.Min(payload.Length, packet.Length - 4)));
    return packet;
}

static string? GetQueryValue(string? inputUrl, string key)
{
    if (string.IsNullOrWhiteSpace(inputUrl)
        || !Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri)
        || string.IsNullOrWhiteSpace(uri.Query))
    {
        return null;
    }

    var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
    foreach (var pair in pairs)
    {
        var parts = pair.Split('=', 2);
        var candidateKey = Uri.UnescapeDataString(parts[0]);
        if (!string.Equals(candidateKey, key, StringComparison.Ordinal))
            continue;

        return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
    }

    return null;
}
