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
