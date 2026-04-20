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
