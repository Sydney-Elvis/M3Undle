using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.GeneratedHls;
using M3Undle.Web.Streaming.Models;
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
    public async Task StartAsync_WhenWorkDirectoryCannotBePrepared_DisablesGeneratedHls()
    {
        var root = CreateTempDir();
        try
        {
            await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
            var workDirectoryPath = Path.Combine(root, "hls-work");
            File.WriteAllText(workDirectoryPath, "not a directory");

            await using var manager = CreateManager(
                root,
                ffmpeg.ExePath,
                startupTimeoutSeconds: 1,
                workDirectory: workDirectoryPath);

            await manager.StartAsync(CancellationToken.None);

            Assert.IsFalse(manager.FfmpegAvailable);
            Assert.IsFalse(manager.IsEffectivelyEnabled);
            StringAssert.Contains(manager.FfmpegUnavailableReason!, "not writable");
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
    public async Task CreateSessionAsync_WithSameAdmissionKeyAndStream_ReusesExistingSession()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3);
        var key = new ChannelSessionKey("provider-1", "channel-1");

        await manager.StartAsync(CancellationToken.None);

        var first = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/channel-1.ts",
                DisplayName: "Reusable Channel",
                AdmissionKey: key,
                RequestedRoute: "/live/a"),
            CancellationToken.None);

        var second = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/channel-1.ts",
                DisplayName: "Reusable Channel",
                AdmissionKey: key,
                RequestedRoute: "/live/b"),
            CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(first.SessionId, second.SessionId);

        var hlsRoot = Path.Combine(ffmpeg.Root, "generated-hls");
        Assert.HasCount(1, Directory.GetDirectories(hlsRoot));

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

    [TestMethod]
    public async Task CreateSessionAsync_ForInternalRelay_MarksInputAsMpegTs()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3);
        var argsFile = Path.Combine(ffmpeg.Root, "args.txt");

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: $"http://127.0.0.1:8080/internal/relay/provider/channel.ts?argsOut={Uri.EscapeDataString(argsFile)}",
                DisplayName: "Internal Relay",
                InternalRelaySecret: "secret"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        var args = await File.ReadAllLinesAsync(argsFile);
        var inputIndex = Array.IndexOf(args, "-i");
        Assert.IsTrue(inputIndex > 2);
        Assert.AreEqual("-f", args[inputIndex - 2]);
        Assert.AreEqual("mpegts", args[inputIndex - 1]);
        Assert.IsFalse(args.Contains("-allowed_extensions"));

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task CreateSessionAsync_ForProviderHlsInput_AddsAllowedExtensions()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3);
        var argsFile = Path.Combine(ffmpeg.Root, "args.txt");

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: $"https://provider.test/live/channel.m3u8?argsOut={Uri.EscapeDataString(argsFile)}",
                DisplayName: "Provider HLS"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        var args = await File.ReadAllLinesAsync(argsFile);
        var allowedExtIdx = Array.IndexOf(args, "-allowed_extensions");
        Assert.IsTrue(allowedExtIdx >= 0, "-allowed_extensions should be present for HLS input");
        Assert.AreEqual("ALL", args[allowedExtIdx + 1]);
        Assert.IsFalse(args.Any(a => a.Contains("X-M3Undle-Internal-Relay")), "Relay secret header should not be present");

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task CreateSessionAsync_ForProviderTsInput_DoesNotAddAllowedExtensions()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3);
        var argsFile = Path.Combine(ffmpeg.Root, "args.txt");

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: $"https://provider.test/live/channel.ts?argsOut={Uri.EscapeDataString(argsFile)}",
                DisplayName: "Provider TS",
                ProviderUserAgent: "TestApp/1.0"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        var args = await File.ReadAllLinesAsync(argsFile);
        Assert.IsFalse(args.Contains("-allowed_extensions"), "-allowed_extensions should not be present for TS input");
        Assert.IsTrue(args.Contains("-user_agent"), "-user_agent should be present when ProviderUserAgent is set");

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task TrackClient_WhenKnownProbeUserAgent_DoesNotCountSubscriber()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Probe Test"),
            CancellationToken.None);

        Assert.IsNotNull(handle);
        Assert.IsFalse(GeneratedHlsSessionManager.ShouldCountAsViewer("curl/8.5.0"));
        Assert.IsTrue(GeneratedHlsSessionManager.ShouldCountAsViewer("Mozilla/5.0 IPTVnator"));

        manager.TrackClient(
            handle.SessionId,
            "172.21.0.1",
            "curl/8.5.0",
            "/hls/generated/test/segment_000068.ts",
            countAsViewer: GeneratedHlsSessionManager.ShouldCountAsViewer("curl/8.5.0"));

        Assert.IsEmpty(registry.GetActiveClients());

        var session = registry.TryGetSession(handle.SessionId);
        Assert.IsNotNull(session);
        Assert.AreEqual(0, session.SubscriberCount);

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task TrackClient_RegistersHlsClientInRegistry()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Track Test"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        manager.TrackClient(handle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/test/index.m3u8");

        var clients = registry.GetActiveClients();
        Assert.HasCount(1, clients);
        Assert.AreEqual("192.168.1.100", clients[0].RemoteIp);
        Assert.AreEqual("Mozilla/5.0 IPTVnator", clients[0].UserAgent);
        Assert.AreEqual(handle.SessionId, clients[0].SessionId);
        Assert.AreEqual(ClientTransport.GeneratedHls, clients[0].Transport);

        // Generated HLS sessions are internal — use TryGetSession (no IsInternal filter).
        var session = registry.TryGetSession(handle.SessionId);
        Assert.IsNotNull(session);
        Assert.AreEqual(1, session.SubscriberCount);

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task TrackClient_NormalizesIpv4MappedIpv6Address()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Mapped IP Test"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        manager.TrackClient(handle.SessionId, "::ffff:192.168.1.100", "Smarters Pro", "/hls/generated/test/index.m3u8");

        var clients = registry.GetActiveClients();
        Assert.HasCount(1, clients);
        Assert.AreEqual("192.168.1.100", clients[0].RemoteIp);

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task TrackClient_SameClientUpdatesRatherThanDuplicates()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Dedup Test"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        manager.TrackClient(handle.SessionId, "192.168.1.100", "Mozilla/5.0", "/hls/generated/test/index.m3u8");
        manager.TrackClient(handle.SessionId, "192.168.1.100", "Mozilla/5.0", "/hls/generated/test/index.m3u8");
        manager.TrackClient(handle.SessionId, "192.168.1.100", "Mozilla/5.0", "/hls/generated/test/index.m3u8");

        var clients = registry.GetActiveClients();
        Assert.HasCount(1, clients, "Same IP+UA should produce one tracked client, not duplicates.");

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task TrackClient_DifferentClientsTrackedSeparately()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Multi Client Test"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        manager.TrackClient(handle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/test/index.m3u8");
        manager.TrackClient(handle.SessionId, "192.168.1.200", "Mozilla/5.0 Chrome", "/hls/generated/test/index.m3u8");

        var clients = registry.GetActiveClients();
        Assert.HasCount(2, clients);

        // Generated HLS sessions are internal — use TryGetSession (no IsInternal filter).
        var session = registry.TryGetSession(handle.SessionId);
        Assert.IsNotNull(session);
        Assert.AreEqual(2, session.SubscriberCount);
        Assert.IsTrue(session.IsShared);

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task TrackClient_SameFingerprintOnDifferentSession_EvictsOlderSession()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(
            ffmpeg.Root,
            ffmpeg.ExePath,
            startupTimeoutSeconds: 3,
            registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var firstHandle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream-a.ts",
                DisplayName: "Retune A"),
            CancellationToken.None);

        var secondHandle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream-b.ts",
                DisplayName: "Retune B"),
            CancellationToken.None);

        Assert.IsNotNull(firstHandle);
        Assert.IsNotNull(secondHandle);

        manager.TrackClient(firstHandle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/a/index.m3u8");
        manager.TrackClient(secondHandle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/b/index.m3u8");

        var clients = registry.GetActiveClients();
        Assert.HasCount(1, clients, "Retune should evict the older generated-HLS client immediately.");
        Assert.AreEqual(secondHandle.SessionId, clients[0].SessionId);

        var firstSession = registry.TryGetSession(firstHandle.SessionId);
        var secondSession = registry.TryGetSession(secondHandle.SessionId);
        Assert.IsNotNull(firstSession);
        Assert.IsNotNull(secondSession);
        Assert.AreEqual(0, firstSession.SubscriberCount);
        Assert.AreEqual(1, secondSession.SubscriberCount);

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task TrackClient_SameFingerprintRapidRetune_EvictsOlderSessionImmediately()
    {
        // Verifies that retune eviction fires even when the client switches channels
        // back-to-back with no delay — the former grace period prevented this.
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var firstHandle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream-a.ts",
                DisplayName: "Rapid Retune A"),
            CancellationToken.None);

        var secondHandle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream-b.ts",
                DisplayName: "Rapid Retune B"),
            CancellationToken.None);

        Assert.IsNotNull(firstHandle);
        Assert.IsNotNull(secondHandle);

        manager.TrackClient(firstHandle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/a/index.m3u8");
        // Immediate retune with no delay — should evict from the first session.
        manager.TrackClient(secondHandle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/b/index.m3u8");

        var clients = registry.GetActiveClients();
        Assert.HasCount(1, clients, "Rapid retune should evict the older session immediately.");
        Assert.AreEqual(secondHandle.SessionId, clients[0].SessionId);

        var firstSession = registry.TryGetSession(firstHandle.SessionId);
        var secondSession = registry.TryGetSession(secondHandle.SessionId);
        Assert.IsNotNull(firstSession);
        Assert.IsNotNull(secondSession);
        Assert.AreEqual(0, firstSession.SubscriberCount);
        Assert.AreEqual(1, secondSession.SubscriberCount);

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task NotifyDirectClientActive_SameFingerprintOnDifferentChannel_EvictsHlsSession()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var hlsHandle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/cbs.ts",
                DisplayName: "CBS HLS",
                ParentStreamSessionId: "cbs-parent-session-id"),
            CancellationToken.None);

        Assert.IsNotNull(hlsHandle);

        manager.TrackClient(hlsHandle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/cbs/index.m3u8");
        Assert.HasCount(1, registry.GetActiveClients());

        // Client attaches as a direct subscriber to a different channel (different parent session).
        manager.NotifyDirectClientActive("192.168.1.100", "Mozilla/5.0 IPTVnator", "fox-parent-session-id");

        Assert.HasCount(0, registry.GetActiveClients(), "Direct subscriber on a different channel should evict the stale HLS session client.");

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task NotifyDirectClientActive_SameFingerprintOnSameChannel_DoesNotEvict()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        const string parentSessionId = "cbs-parent-session-id";
        var hlsHandle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/cbs.ts",
                DisplayName: "CBS HLS",
                ParentStreamSessionId: parentSessionId),
            CancellationToken.None);

        Assert.IsNotNull(hlsHandle);

        manager.TrackClient(hlsHandle.SessionId, "192.168.1.100", "Mozilla/5.0 IPTVnator", "/hls/generated/cbs/index.m3u8");
        Assert.HasCount(1, registry.GetActiveClients());

        // Direct subscriber on the SAME channel should not evict the HLS session client.
        manager.NotifyDirectClientActive("192.168.1.100", "Mozilla/5.0 IPTVnator", parentSessionId);

        Assert.HasCount(1, registry.GetActiveClients(), "Direct subscriber on the same channel should not evict its own HLS session client.");

        await manager.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task StopAsync_RemovesHlsClientsFromRegistry()
    {
        await using var ffmpeg = FakeFfmpegBinary.Create(writeManifest: true);
        var registry = new StreamingRegistry(Options.Create(new StreamProxyOptions()));
        await using var manager = CreateManager(ffmpeg.Root, ffmpeg.ExePath, startupTimeoutSeconds: 3, registry: registry);

        await manager.StartAsync(CancellationToken.None);

        var handle = await manager.CreateSessionAsync(
            new GeneratedHlsSessionRequest(
                StreamUrl: "https://provider.test/live/stream.ts",
                DisplayName: "Cleanup Test"),
            CancellationToken.None);

        Assert.IsNotNull(handle);

        manager.TrackClient(handle.SessionId, "192.168.1.100", "Mozilla/5.0", "/hls/generated/test/index.m3u8");
        Assert.HasCount(1, registry.GetActiveClients());

        await manager.StopAsync(CancellationToken.None);

        Assert.HasCount(0, registry.GetActiveClients(), "HLS clients should be removed when session is cleaned up.");
    }

    private static GeneratedHlsSessionManager CreateManager(
        string root,
        string ffmpegPath,
        int startupTimeoutSeconds,
        StreamingRegistry? registry = null,
        string? workDirectory = null)
    {
        var options = Options.Create(new GeneratedHlsOptions
        {
            Enabled = true,
            Directory = workDirectory ?? Path.Combine(root, "generated-hls"),
            FfmpegPath = ffmpegPath,
            SegmentDurationSeconds = 1,
            PlaylistSize = 2,
            DeleteThreshold = 1,
            StartupTimeoutSeconds = startupTimeoutSeconds,
            InactivityTimeoutSeconds = 120,
            CleanupIntervalSeconds = 120,
            StartupStaleAgeHours = 1,
        });

        registry ??= new StreamingRegistry(Options.Create(new StreamProxyOptions()));
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
