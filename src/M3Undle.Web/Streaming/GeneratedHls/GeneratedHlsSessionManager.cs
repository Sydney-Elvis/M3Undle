using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.GeneratedHls;

public sealed record GeneratedHlsSessionRequest(
    string StreamUrl,
    string DisplayName,
    string? ProviderId = null,
    string? ProviderUserAgent = null,
    string? ProviderHeadersJson = null,
    ChannelSessionKey? AdmissionKey = null);

public sealed record GeneratedHlsSessionHandle(
    string SessionId,
    string ManifestPath);

public sealed class GeneratedHlsSessionManager(
    IOptions<GeneratedHlsOptions> options,
    IServiceScopeFactory scopeFactory,
    ChannelSessionManager channelSessionManager,
    StreamingRegistry registry,
    ILogger<GeneratedHlsSessionManager> logger) : IHostedService, IAsyncDisposable
{
    private readonly GeneratedHlsOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, GeneratedHlsSession> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _sweepTask;
    private int _stopState;
    private int _disposeState;
    private volatile bool _ffmpegAvailable;
    private volatile string? _ffmpegUnavailableReason;

    public bool IsEffectivelyEnabled => _options.Enabled && _ffmpegAvailable;

    public bool FfmpegAvailable => _ffmpegAvailable;

    public string? FfmpegUnavailableReason => _ffmpegUnavailableReason;

    public string ConfiguredFfmpegPath => _options.FfmpegPath;

    public async Task<GeneratedHlsSessionHandle?> CreateSessionAsync(
        GeneratedHlsSessionRequest request,
        CancellationToken ct)
    {
        if (!IsEffectivelyEnabled)
            return null;

        Directory.CreateDirectory(_options.Directory);

        var sessionId = Guid.NewGuid().ToString("N");
        var sessionDir = Path.Combine(_options.Directory, sessionId);
        Directory.CreateDirectory(sessionDir);

        var manifestPath = Path.Combine(sessionDir, "index.m3u8");
        var segmentPattern = Path.Combine(sessionDir, "segment_%06d.ts");
        var startInfo = await BuildStartInfoAsync(request, manifestPath, segmentPattern, ct);

        Process process;
        try
        {
            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            if (!process.Start())
                throw new InvalidOperationException("FFmpeg process could not be started.");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(sessionDir);
            logger.LogWarning(ex, "Generated HLS session startup failed for '{DisplayName}'.", request.DisplayName);
            return null;
        }

        var session = new GeneratedHlsSession(
            sessionId,
            request.DisplayName,
            sessionDir,
            manifestPath,
            process,
            request.AdmissionKey);

        if (!_sessions.TryAdd(sessionId, session))
        {
            TryStopProcess(process);
            process.Dispose();
            TryDeleteDirectory(sessionDir);
            return null;
        }

        _ = PumpProcessStreamAsync(session, process.StandardError, isError: true, _lifetimeCts.Token);
        _ = PumpProcessStreamAsync(session, process.StandardOutput, isError: false, _lifetimeCts.Token);

        var ready = await WaitForManifestReadyAsync(session, ct);
        if (!ready)
        {
            await RemoveSessionAsync(sessionId, "startup failed");
            return null;
        }

        session.Touch();
        registry.UpsertSession(session.ToSnapshot());
        return new GeneratedHlsSessionHandle(sessionId, manifestPath);
    }

    public async Task<string?> ReadManifestAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        session.Touch();
        registry.UpsertSession(session.ToSnapshot());
        if (session.AdmissionKey is { } key)
            channelSessionManager.TouchHlsSlot(key);

        if (!File.Exists(session.ManifestPath))
            return null;

        try
        {
            return await File.ReadAllTextAsync(session.ManifestPath, ct);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool TryResolveAssetPath(
        string sessionId,
        string asset,
        out string filePath,
        out string contentType)
    {
        filePath = string.Empty;
        contentType = string.Empty;

        if (!TryValidateAsset(asset, out var normalizedAsset))
            return false;

        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        var candidate = Path.GetFullPath(Path.Combine(session.WorkDirectory, normalizedAsset));
        if (!candidate.StartsWith(session.WorkDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        if (!File.Exists(candidate))
            return false;

        contentType = GetContentType(candidate);
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        session.Touch();
        registry.UpsertSession(session.ToSnapshot());
        if (session.AdmissionKey is { } admKey)
            channelSessionManager.TouchHlsSlot(admKey);

        filePath = candidate;
        return true;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Generated HLS is disabled by configuration.");
            return;
        }

        _ffmpegAvailable = await ProbeFfmpegAsync();
        if (!_ffmpegAvailable)
        {
            logger.LogWarning(
                "Generated HLS auto-disabled: FFmpeg not found at '{FfmpegPath}'. {Reason} " +
                "Browser clients requesting TS-only streams will not receive HLS. " +
                "Install FFmpeg or set the correct path in Settings → Browser Playback.",
                _options.FfmpegPath,
                _ffmpegUnavailableReason ?? "Unknown reason.");
            return;
        }

        logger.LogInformation("Generated HLS enabled. FFmpeg found at '{FfmpegPath}'.", _options.FfmpegPath);
        Directory.CreateDirectory(_options.Directory);
        CleanupStaleDirectories();
        _sweepTask = Task.Run(() => SweepLoopAsync(_lifetimeCts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopState, 1) != 0)
            return;

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (_sweepTask is not null)
        {
            try
            {
                await _sweepTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Normal during shutdown.
            }
        }

        var sessionIds = _sessions.Keys.ToArray();
        foreach (var sessionId in sessionIds)
            await RemoveSessionAsync(sessionId, "application shutdown");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        await StopAsync(CancellationToken.None);
        _lifetimeCts.Dispose();
    }

    private async Task<ProcessStartInfo> BuildStartInfoAsync(
        GeneratedHlsSessionRequest request,
        string manifestPath,
        string segmentPattern,
        CancellationToken ct)
    {
        var (userAgent, headersJson) = await ResolveProviderMetadataAsync(
            request.ProviderId,
            request.ProviderUserAgent,
            request.ProviderHeadersJson,
            ct);

        var headersArg = BuildFfmpegHeadersArgument(headersJson);
        var info = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-nostdin");
        info.ArgumentList.Add("-y");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("warning");

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            info.ArgumentList.Add("-user_agent");
            info.ArgumentList.Add(userAgent);
        }

        if (!string.IsNullOrWhiteSpace(headersArg))
        {
            info.ArgumentList.Add("-headers");
            info.ArgumentList.Add(headersArg);
        }

        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(request.StreamUrl);
        info.ArgumentList.Add("-map");
        info.ArgumentList.Add("0:v?");
        info.ArgumentList.Add("-map");
        info.ArgumentList.Add("0:a?");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("copy");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("hls");
        info.ArgumentList.Add("-hls_time");
        info.ArgumentList.Add(_options.SegmentDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-hls_list_size");
        info.ArgumentList.Add(_options.PlaylistSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-hls_delete_threshold");
        info.ArgumentList.Add(_options.DeleteThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-hls_flags");
        info.ArgumentList.Add("delete_segments+omit_endlist+independent_segments");
        info.ArgumentList.Add("-hls_segment_filename");
        info.ArgumentList.Add(segmentPattern);
        info.ArgumentList.Add(manifestPath);

        return info;
    }

    private async Task<(string? UserAgent, string? HeadersJson)> ResolveProviderMetadataAsync(
        string? providerId,
        string? userAgentHint,
        string? headersHint,
        CancellationToken ct)
    {
        if ((!string.IsNullOrWhiteSpace(userAgentHint) || !string.IsNullOrWhiteSpace(headersHint))
            || string.IsNullOrWhiteSpace(providerId))
        {
            return (userAgentHint, headersHint);
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var provider = await db.Providers
                .AsNoTracking()
                .Where(x => x.ProviderId == providerId && x.Enabled)
                .Select(x => new { x.UserAgent, x.HeadersJson })
                .FirstOrDefaultAsync(ct);

            return provider is null
                ? (userAgentHint, headersHint)
                : (provider.UserAgent, provider.HeadersJson);
        }
        catch
        {
            return (userAgentHint, headersHint);
        }
    }

    private static string? BuildFfmpegHeadersArgument(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(headersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var builder = new StringBuilder();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;

                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (property.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    continue;

                builder.Append(property.Name).Append(": ").Append(value).Append("\r\n");
            }

            return builder.Length == 0 ? null : builder.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> WaitForManifestReadyAsync(GeneratedHlsSession session, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                if (session.Process.HasExited)
                {
                    logger.LogWarning(
                        "Generated HLS session {SessionId} exited before manifest was ready. exitCode={ExitCode}",
                        session.SessionId,
                        session.Process.ExitCode);
                    return false;
                }

                if (File.Exists(session.ManifestPath))
                {
                    var info = new FileInfo(session.ManifestPath);
                    if (info.Exists && info.Length > 0)
                        return true;
                }

                await Task.Delay(200, timeoutCts.Token);
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_options.CleanupIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var inactivity = TimeSpan.FromSeconds(_options.InactivityTimeoutSeconds);
            var candidates = _sessions.Values
                .Where(x => x.Process.HasExited || (now - x.LastAccessUtc) > inactivity)
                .Select(x => x.SessionId)
                .ToArray();

            foreach (var sessionId in candidates)
                await RemoveSessionAsync(sessionId, "inactivity or process exit");
        }
    }

    private async Task RemoveSessionAsync(string sessionId, string reason)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        registry.RemoveSession(sessionId);

        if (session.AdmissionKey is { } key)
            channelSessionManager.ReleaseHlsSlot(key);

        try
        {
            TryStopProcess(session.Process);
            await session.Process.WaitForExitAsync();
        }
        catch
        {
            // Ignore failures during best-effort process shutdown.
        }
        finally
        {
            session.Process.Dispose();
            TryDeleteDirectory(session.WorkDirectory);
            logger.LogInformation(
                "Generated HLS session removed: sessionId={SessionId} reason={Reason}",
                sessionId,
                reason);
        }
    }

    private void CleanupStaleDirectories()
    {
        var root = _options.Directory;
        if (!Directory.Exists(root))
            return;

        var staleBefore = DateTimeOffset.UtcNow - TimeSpan.FromHours(_options.StartupStaleAgeHours);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                if (info.LastWriteTimeUtc >= staleBefore.UtcDateTime)
                    continue;

                info.Delete(recursive: true);
            }
            catch
            {
                // Ignore startup cleanup failures and proceed.
            }
        }
    }

    private async Task<bool> ProbeFfmpegAsync()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _options.FfmpegPath,
                    ArgumentList = { "-version" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                _ffmpegUnavailableReason = $"FFmpeg process at '{_options.FfmpegPath}' could not be started.";
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryStopProcess(process);
                _ffmpegUnavailableReason = $"FFmpeg at '{_options.FfmpegPath}' did not respond within 10 seconds.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                _ffmpegUnavailableReason = $"FFmpeg at '{_options.FfmpegPath}' exited with code {process.ExitCode}.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            _ffmpegUnavailableReason = $"FFmpeg not found at '{_options.FfmpegPath}'. Install FFmpeg or configure the correct path.";
            return false;
        }
    }

    private static bool TryValidateAsset(string asset, out string normalizedAsset)
    {
        normalizedAsset = asset?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedAsset))
            return false;

        if (normalizedAsset.Contains('/') || normalizedAsset.Contains('\\'))
            return false;

        if (normalizedAsset.Contains("..", StringComparison.Ordinal))
            return false;

        var ext = Path.GetExtension(normalizedAsset);
        return ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
            return "application/vnd.apple.mpegurl";
        if (ext.Equals(".ts", StringComparison.OrdinalIgnoreCase))
            return "video/mp2t";

        return string.Empty;
    }

    private static void TryStopProcess(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort process shutdown.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort directory cleanup.
        }
    }

    private async Task PumpProcessStreamAsync(
        GeneratedHlsSession session,
        StreamReader reader,
        bool isError,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                return;
            }

            if (line is null)
                return;

            if (isError)
            {
                logger.LogDebug(
                    "FFmpeg[{SessionId}] {DisplayName}: {Line}",
                    session.SessionId,
                    session.DisplayName,
                    line);
            }
        }
    }

    private sealed class GeneratedHlsSession(
        string sessionId,
        string displayName,
        string workDirectory,
        string manifestPath,
        Process process,
        ChannelSessionKey? admissionKey = null)
    {
        private long _lastAccessUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public string SessionId { get; } = sessionId;

        public string DisplayName { get; } = displayName;

        public string WorkDirectory { get; } = Path.GetFullPath(workDirectory);

        public string ManifestPath { get; } = manifestPath;

        public Process Process { get; } = process;

        public ChannelSessionKey? AdmissionKey { get; } = admissionKey;

        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastAccessUtc
            => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastAccessUnixMs));

        public void Touch()
            => Interlocked.Exchange(ref _lastAccessUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public StreamSessionSnapshot ToSnapshot() => new(
            SessionId: SessionId,
            ProviderId: AdmissionKey?.ProviderId ?? string.Empty,
            ProviderChannelId: AdmissionKey?.ProviderChannelId ?? string.Empty,
            DisplayName: DisplayName,
            State: Process.HasExited ? SessionState.Faulted : SessionState.Live,
            SubscriberCount: 0,
            IsShared: false,
            BufferUsedBytes: 0,
            BufferMaxBytes: 0,
            StartedUtc: StartedUtc,
            LastUpstreamByteUtc: LastAccessUtc,
            ReconnectAttempts: 0,
            LastFailureKind: null);
    }
}
