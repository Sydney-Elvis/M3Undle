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
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3);

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
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: false);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 1);

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

    // -------------------------------------------------------------------------
    // Fake ffmpeg — cross-platform .NET console app (M3Undle.FakeFfmpeg project).
    //
    // Behavior is communicated via a flag file in the per-test temp root rather
    // than an env var, so parallel test runs don't share any global state.
    // The fake reads {root}/write.flag by walking up from the manifest path it
    // receives as its last argument.
    // -------------------------------------------------------------------------

    private sealed class FakeFfmpegBinary : IAsyncDisposable
    {
        private FakeFfmpegBinary(string root, string exePath)
        {
            Root = root;
            ExePath = exePath;
        }

        public string Root { get; }
        public string ExePath { get; }

        public static FakeFfmpegBinary Create(bool writeManifest)
        {
            var root = CreateTempDir();
            if (writeManifest)
                File.WriteAllText(Path.Combine(root, "write.flag"), string.Empty);

            return new FakeFfmpegBinary(root, LocateExecutable());
        }

        private static string LocateExecutable()
        {
            // AppContext.BaseDirectory = .../tests/M3Undle.Web.Tests/bin/{Config}/{TFM}/
            // M3Undle.FakeFfmpeg is a sibling project built to the same {Config}/{TFM}.
            var exeName = OperatingSystem.IsWindows()
                ? "M3Undle.FakeFfmpeg.exe"
                : "M3Undle.FakeFfmpeg";

            var tfmDir = new DirectoryInfo(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var configDir = tfmDir.Parent!;                   // Debug / Release
            var testsDir = configDir.Parent!.Parent!.Parent!; // tests/

            var path = Path.Combine(
                testsDir.FullName,
                "M3Undle.FakeFfmpeg",
                "bin",
                configDir.Name,
                tfmDir.Name,
                exeName);

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"FakeFfmpeg executable not found at '{path}'. " +
                    "Ensure M3Undle.FakeFfmpeg is built (it is a ProjectReference of this test project).");

            return path;
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
