using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using M3Undle.Web.Streaming.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class GeneratedHlsSessionManagerTests
{
    [TestMethod]
    public async Task StartAsync_WhenFfmpegIsMissing_DisablesGeneratedHls()
    {
        var root = CreateTempDir();
        try
        {
            await using var manager = CreateManager(root, "/definitely-not-a-real-ffmpeg-binary", startupTimeoutSeconds: 1);
            await manager.StartAsync(CancellationToken.None);

            Assert.IsFalse(manager.FfmpegAvailable);
            Assert.IsFalse(manager.IsEffectivelyEnabled);
            Assert.IsFalse(string.IsNullOrWhiteSpace(manager.FfmpegUnavailableReason));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task CreateSessionAsync_WithFakeFfmpeg_ReturnsHandleAndServesManifestAsset()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Fake ffmpeg shell-script tests are Linux/macOS specific.");
            return;
        }

        await using var ffmpeg = await FakeFfmpegScript.CreateAsync(writeManifest: true);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ScriptPath, startupTimeoutSeconds: 3);

        await manager.StartAsync(CancellationToken.None);
        Assert.IsTrue(manager.IsEffectivelyEnabled);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Test Channel"),
            CancellationToken.None);

        Assert.IsNotNull(handle);
        Assert.IsFalse(string.IsNullOrWhiteSpace(handle.SessionId));

        var manifest = await manager.ReadManifestAsync(handle.SessionId, CancellationToken.None);
        Assert.IsNotNull(manifest);
        StringAssert.Contains(manifest, "#EXTM3U");

        var resolved = manager.TryResolveAssetPath(
            handle.SessionId,
            "index.m3u8",
            out var manifestPath,
            out var contentType);

        Assert.IsTrue(resolved);
        Assert.IsTrue(File.Exists(manifestPath));
        Assert.AreEqual("application/vnd.apple.mpegurl", contentType);

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task CreateSessionAsync_WhenManifestNeverAppears_ReturnsNull()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Fake ffmpeg shell-script tests are Linux/macOS specific.");
            return;
        }

        await using var ffmpeg = await FakeFfmpegScript.CreateAsync(writeManifest: false);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ScriptPath, startupTimeoutSeconds: 1);

        await manager.StartAsync(CancellationToken.None);
        Assert.IsTrue(manager.IsEffectivelyEnabled);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Timeout Channel"),
            CancellationToken.None);

        Assert.IsNull(handle);

        await manager.StopAsync(CancellationToken.None);
    }

    private static GeneratedHlsSessionManager CreateManager(
        string root,
        string ffmpegPath,
        int startupTimeoutSeconds)
    {
        var options = Options.Create(new GeneratedHlsOptions
        {
            Enabled = true,
            Directory = Path.Combine(root, "generated-hls"),
            FfmpegPath = ffmpegPath,
            SegmentDurationSeconds = 1,
            PlaylistSize = 2,
            DeleteThreshold = 1,
            StartupTimeoutSeconds = startupTimeoutSeconds,
            InactivityTimeoutSeconds = 120,
            CleanupIntervalSeconds = 120,
            StartupStaleAgeHours = 1,
        });

        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        return new GeneratedHlsSessionManager(
            options,
            scopeFactory: null!,
            channelSessionManager: null!,
            registry,
            NullLogger<GeneratedHlsSessionManager>.Instance);
    }

    private sealed class FakeFfmpegScript : IAsyncDisposable
    {
        private FakeFfmpegScript(string root, string scriptPath)
        {
            Root = root;
            ScriptPath = scriptPath;
        }

        public string Root { get; }
        public string ScriptPath { get; }

        public static async Task<FakeFfmpegScript> CreateAsync(bool writeManifest)
        {
            var root = CreateTempDir();
            var scriptPath = Path.Combine(root, "fake_ffmpeg.sh");

            var script = writeManifest
                ? """
                  #!/usr/bin/env bash
                  set -eu
                  if [ "${1:-}" = "-version" ]; then
                    echo "ffmpeg version fake"
                    exit 0
                  fi
                  manifest="${@: -1}"
                  mkdir -p "$(dirname "$manifest")"
                  printf '#EXTM3U\n#EXTINF:4.0,\nsegment_000001.ts\n' > "$manifest"
                  printf 'segment' > "$(dirname "$manifest")/segment_000001.ts"
                  while true; do
                    sleep 1
                  done
                  """
                : """
                  #!/usr/bin/env bash
                  set -eu
                  if [ "${1:-}" = "-version" ]; then
                    echo "ffmpeg version fake"
                    exit 0
                  fi
                  # Simulate a startup hang where no manifest is produced.
                  sleep 30
                  """;

            await File.WriteAllTextAsync(scriptPath, script, CancellationToken.None);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new FakeFfmpegScript(root, scriptPath);
        }

        public ValueTask DisposeAsync()
        {
            TryDelete(Root);
            return ValueTask.CompletedTask;
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"m3undle-generated-hls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup in tests
        }
    }
}
