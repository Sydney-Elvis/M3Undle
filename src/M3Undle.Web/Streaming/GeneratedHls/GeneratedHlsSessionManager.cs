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
    ChannelSessionKey? AdmissionKey = null,
    string? InternalRelaySecret = null,
    string? ParentStreamSessionId = null,
    string? RequestedRoute = null);

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

    public static bool ShouldCountAsViewer(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return true;

        return !userAgent.Contains("curl/", StringComparison.OrdinalIgnoreCase)
               && !userAgent.Contains("Wget/", StringComparison.OrdinalIgnoreCase)
               && !userAgent.Contains("HTTPie/", StringComparison.OrdinalIgnoreCase)
               && !userAgent.Contains("PostmanRuntime/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<GeneratedHlsSessionHandle?> CreateSessionAsync(
        GeneratedHlsSessionRequest request,
        CancellationToken ct)
    {
        if (!IsEffectivelyEnabled)
            return null;

        Directory.CreateDirectory(_options.Directory);

        if (TryGetReusableSession(request, out var reusableSession))
            return reusableSession;

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
            request.StreamUrl,
            request.DisplayName,
            sessionDir,
            manifestPath,
            process,
            request.AdmissionKey,
            request.ParentStreamSessionId,
            request.RequestedRoute);

        if (!_sessions.TryAdd(sessionId, session))
        {
            TryStopProcess(process);
            process.Dispose();
            TryDeleteDirectory(sessionDir);
            return null;
        }

        RetainParentStreamActivity(session);

        var stderrPumpTask = PumpProcessStreamAsync(session, process.StandardError, isError: true, _lifetimeCts.Token);
        var stdoutPumpTask = PumpProcessStreamAsync(session, process.StandardOutput, isError: false, _lifetimeCts.Token);
        session.SetPumpTasks(stderrPumpTask, stdoutPumpTask);
        ObservePumpTaskFailure(stderrPumpTask, session, "stderr");
        ObservePumpTaskFailure(stdoutPumpTask, session, "stdout");

        var ready = await WaitForManifestReadyAsync(session, ct);
        if (!ready)
        {
            await RemoveSessionAsync(sessionId, "startup_failed");
            return null;
        }

        session.Touch();
        registry.UpsertSession(session.ToSnapshot());
        logger.LogInformation(
            "Generated HLS session created: SessionId={SessionId} DisplayName={DisplayName} ParentStreamSessionId={ParentStreamSessionId} AdmissionKey={AdmissionKey} TrackInStatus={TrackInStatus} Reused={Reused} ManifestPath={ManifestPath}",
            session.SessionId,
            session.DisplayName,
            session.ParentStreamSessionId,
            session.AdmissionKey?.ToString(),
            true,
            false,
            session.ManifestPath);
        return new GeneratedHlsSessionHandle(sessionId, manifestPath);
    }

    private bool TryGetReusableSession(
        GeneratedHlsSessionRequest request,
        out GeneratedHlsSessionHandle handle)
    {
        handle = default!;

        if (request.AdmissionKey is null)
            return false;

        foreach (var session in _sessions.Values)
        {
            if (!session.CanReuseFor(request))
                continue;

            session.Touch();
            registry.UpsertSession(session.ToSnapshot());
            if (session.AdmissionKey is { } key)
                channelSessionManager?.TouchHlsSlot(key);

            logger.LogInformation(
                "Generated HLS session reused: SessionId={SessionId} DisplayName={DisplayName} ParentStreamSessionId={ParentStreamSessionId} AdmissionKey={AdmissionKey} ManifestPath={ManifestPath}",
                session.SessionId,
                session.DisplayName,
                session.ParentStreamSessionId,
                session.AdmissionKey?.ToString(),
                session.ManifestPath);
            handle = new GeneratedHlsSessionHandle(session.SessionId, session.ManifestPath);
            return true;
        }

        return false;
    }

    public async Task<string?> ReadManifestAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        session.Touch();
        registry.UpsertSession(session.ToSnapshot());
        if (session.AdmissionKey is { } key)
            channelSessionManager?.TouchHlsSlot(key);

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
            channelSessionManager?.TouchHlsSlot(admKey);

        filePath = candidate;
        return true;
    }

    public void TrackClient(
        string sessionId,
        string? remoteIp,
        string? userAgent,
        string requestedRoute,
        bool countAsViewer = true)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        var trackedClient = session.TrackClient(remoteIp, userAgent, requestedRoute, countAsViewer);
        var record = trackedClient.Record;
        if (record.CountAsViewer)
        {
            registry.UpsertClient(new StreamClientSnapshot(
                record.ClientId, record.SessionId, record.RequestedRoute,
                record.RemoteIp, record.UserAgent, record.ConnectedUtc,
                BytesSent: 0, QueueDepth: 0));
        }

        registry.UpsertSession(session.ToSnapshot());

        if (trackedClient.IsNewClient && record.CountAsViewer)
            EvictRetunedClients(sessionId, remoteIp, userAgent, record.LastAccessUtc);

        if (trackedClient.IsNewClient && record.CountAsViewer)
        {
            logger.LogInformation(
                "Generated HLS client attached: SessionId={SessionId} ParentStreamSessionId={ParentStreamSessionId} ClientId={ClientId} RemoteIp={RemoteIp} UserAgent={UserAgent} RequestedRoute={RequestedRoute} Classification={Classification} HlsClientCount={HlsClientCount}",
                session.SessionId,
                session.ParentStreamSessionId,
                record.ClientId,
                record.RemoteIp,
                record.UserAgent,
                record.RequestedRoute,
                StreamLogClassification.ClassifySubscriber(isInternal: false, isObservedHlsClient: true),
                session.HlsClientCount);
        }
    }

    private void EvictRetunedClients(
        string currentSessionId,
        string? remoteIp,
        string? userAgent,
        DateTimeOffset observedAtUtc)
    {
        if (!GeneratedHlsSession.HasClientFingerprint(remoteIp, userAgent))
            return;

        var clientKey = GeneratedHlsSession.CreateClientKey(remoteIp, userAgent);
        var graceSeconds = Math.Max(0, _options.RetuneEvictionGraceSeconds);
        var cutoff = observedAtUtc - TimeSpan.FromSeconds(graceSeconds);

        foreach (var otherSession in _sessions.Values)
        {
            if (string.Equals(otherSession.SessionId, currentSessionId, StringComparison.Ordinal))
                continue;

            if (!otherSession.TryEvictClient(clientKey, cutoff, out var evictedClient))
                continue;

            registry.RemoveClient(evictedClient.ClientId);
            registry.UpsertSession(otherSession.ToSnapshot());
            LogObservedHlsClientRemoved(otherSession, evictedClient, "retune_detected");

            // If the eviction emptied a relay-backed session, tear it down immediately
            // instead of waiting up to InactivityTimeoutSeconds for the sweep loop.
            // This stops the stale internal relay subscriber from continuing to drain the
            // parent ring buffer alongside the new session's subscriber.
            if (otherSession.HlsClientCount == 0 && otherSession.ParentStreamSessionId is not null)
                _ = RemoveSessionAsync(otherSession.SessionId, "retune_orphaned");
        }
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
            await RemoveSessionAsync(sessionId, "application_shutdown");
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

        if (!string.IsNullOrWhiteSpace(request.InternalRelaySecret))
        {
            // Internal relay: authenticate with secret header, no provider credentials needed.
            info.ArgumentList.Add("-headers");
            info.ArgumentList.Add($"X-M3Undle-Internal-Relay: {request.InternalRelaySecret}\r\n");
            // The relay attaches at the live edge (mid-stream). Regenerate PTS from DTS so FFmpeg
            // does not inherit stale timestamps from an arbitrary position in the source GOP.
            info.ArgumentList.Add("-fflags");
            info.ArgumentList.Add("+genpts");
        }
        else
        {
            var (userAgent, headersJson) = await ResolveProviderMetadataAsync(
                request.ProviderId,
                request.ProviderUserAgent,
                request.ProviderHeadersJson,
                ct);

            var headersArg = BuildFfmpegHeadersArgument(headersJson);

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
            while (!timeoutCts.Token.IsCancellationRequested)
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
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timeout and linked cancellation are handled below.
        }

        if (session.Process.HasExited)
        {
            logger.LogWarning(
                "Generated HLS session {SessionId} exited before manifest was ready. exitCode={ExitCode}",
                session.SessionId,
                session.Process.ExitCode);
            return false;
        }

        if (ct.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
            return false;

        logger.LogWarning(
            "Generated HLS session {SessionId} startup timed out after {TimeoutSeconds}s; stopping FFmpeg immediately.",
            session.SessionId,
            _options.StartupTimeoutSeconds);
        TryStopProcess(session.Process);
        return false;
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

            foreach (var session in _sessions.Values)
            {
                var staleClients = session.SweepStaleClients(inactivity);
                foreach (var client in staleClients)
                {
                    registry.RemoveClient(client.ClientId);
                    LogObservedHlsClientRemoved(session, client, "stale_timeout");
                }

                if (staleClients.Count > 0)
                    registry.UpsertSession(session.ToSnapshot());
            }

            var candidates = _sessions.Values
                .Select(x => new
                {
                    x.SessionId,
                    RemovalReason = x.Process.HasExited ? "process_exit" : (now - x.LastAccessUtc) > inactivity ? "inactivity_timeout" : null,
                })
                .Where(x => x.RemovalReason is not null)
                .ToArray();

            foreach (var candidate in candidates)
                await RemoveSessionAsync(candidate.SessionId, candidate.RemovalReason!);
        }
    }

    private async Task RemoveSessionAsync(string sessionId, string reason)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        session.ReleaseParentActivityRetention();

        foreach (var client in session.RemoveAllClients())
        {
            registry.RemoveClient(client.ClientId);
            LogObservedHlsClientRemoved(session, client, "session_removed");
        }

        registry.RemoveSession(sessionId);

        if (session.AdmissionKey is { } key)
            channelSessionManager?.ReleaseHlsSlot(key);

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
            await ObservePumpTasksAsync(session);
            var artifactState = CaptureArtifactState(session);
            var parentSessionActive = IsParentSessionActive(session);
            session.Process.Dispose();
            TryDeleteDirectory(session.WorkDirectory);
            logger.LogInformation(
                "Generated HLS session removed: SessionId={SessionId} DisplayName={DisplayName} ParentStreamSessionId={ParentStreamSessionId} AdmissionKey={AdmissionKey} RemovalReason={RemovalReason} ParentSessionActive={ParentSessionActive} ManifestExists={ManifestExists} LastManifestWriteUtc={LastManifestWriteUtc} LastSegmentFileName={LastSegmentFileName} LastSegmentIndex={LastSegmentIndex} LastSegmentWriteUtc={LastSegmentWriteUtc} LastPlaylistActivityUtc={LastPlaylistActivityUtc} LastSegmentActivityUtc={LastSegmentActivityUtc}",
                sessionId,
                session.DisplayName,
                session.ParentStreamSessionId,
                session.AdmissionKey?.ToString(),
                reason,
                parentSessionActive,
                artifactState.ManifestExists,
                artifactState.LastManifestWriteUtc,
                artifactState.LastSegmentFileName,
                artifactState.LastSegmentIndex,
                artifactState.LastSegmentWriteUtc,
                artifactState.LastManifestWriteUtc,
                artifactState.LastSegmentWriteUtc);
        }
    }

    private void RetainParentStreamActivity(GeneratedHlsSession session)
    {
        if (session.AdmissionKey is not { } key || string.IsNullOrWhiteSpace(session.ParentStreamSessionId))
            return;

        var retention = channelSessionManager.RetainGeneratedHlsActivity(key, session.ParentStreamSessionId);
        if (retention is null)
            return;

        session.SetParentActivityRetention(retention);
        logger.LogInformation(
            "Generated HLS session retained parent stream activity: SessionId={SessionId} ParentStreamSessionId={ParentStreamSessionId} AdmissionKey={AdmissionKey}",
            session.SessionId,
            session.ParentStreamSessionId,
            session.AdmissionKey?.ToString());
    }

    private async Task ObservePumpTasksAsync(GeneratedHlsSession session)
    {
        var pumpTasks = session.GetPumpTasks();
        if (pumpTasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(pumpTasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            logger.LogDebug(
                "Generated HLS session {SessionId} pump tasks did not complete within teardown timeout.",
                session.SessionId);
        }
        catch
        {
            // Pump exceptions are logged via fault-only continuations.
        }
    }

    private void ObservePumpTaskFailure(Task pumpTask, GeneratedHlsSession session, string streamName)
    {
        _ = pumpTask.ContinueWith(
            faultedTask =>
            {
                logger.LogWarning(
                    faultedTask.Exception,
                    "Generated HLS session {SessionId} parent={ParentStreamSessionId} FFmpeg {StreamName} pump failed.",
                    session.SessionId,
                    session.ParentStreamSessionId,
                    streamName);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (line is null)
                return;

            if (isError)
            {
                logger.LogDebug(
                    "FFmpeg[{SessionId}] parent={ParentStreamSessionId} {DisplayName}: {Line}",
                    session.SessionId,
                    session.ParentStreamSessionId,
                    session.DisplayName,
                    line);
            }
        }
    }

    private void LogObservedHlsClientRemoved(GeneratedHlsSession session, HlsClientRecord client, string removalReason)
    {
        logger.LogInformation(
            "Generated HLS client removed: SessionId={SessionId} ParentStreamSessionId={ParentStreamSessionId} ClientId={ClientId} RemoteIp={RemoteIp} UserAgent={UserAgent} RequestedRoute={RequestedRoute} Classification={Classification} RemovalReason={RemovalReason}",
            session.SessionId,
            session.ParentStreamSessionId,
            client.ClientId,
            client.RemoteIp,
            client.UserAgent,
            client.RequestedRoute,
            StreamLogClassification.ClassifySubscriber(isInternal: false, isObservedHlsClient: true),
            removalReason);
    }

    private bool IsParentSessionActive(GeneratedHlsSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ParentStreamSessionId) || session.AdmissionKey is not { } key)
            return false;

        return channelSessionManager.TryGet(key, out var activeSession)
               && activeSession is not null
               && string.Equals(activeSession.SessionId, session.ParentStreamSessionId, StringComparison.Ordinal);
    }

    private static HlsArtifactState CaptureArtifactState(GeneratedHlsSession session)
    {
        var manifestExists = File.Exists(session.ManifestPath);
        DateTimeOffset? lastManifestWriteUtc = null;
        if (manifestExists)
        {
            var manifestInfo = new FileInfo(session.ManifestPath);
            if (manifestInfo.Exists)
                lastManifestWriteUtc = manifestInfo.LastWriteTimeUtc;
        }

        string? lastSegmentFileName = null;
        int? lastSegmentIndex = null;
        DateTimeOffset? lastSegmentWriteUtc = null;

        if (Directory.Exists(session.WorkDirectory))
        {
            var latestSegment = Directory.EnumerateFiles(session.WorkDirectory, "segment_*.ts")
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            if (latestSegment is not null)
            {
                lastSegmentFileName = latestSegment.Name;
                lastSegmentWriteUtc = latestSegment.LastWriteTimeUtc;
                lastSegmentIndex = TryParseSegmentIndex(latestSegment.Name);
            }
        }

        return new HlsArtifactState(
            manifestExists,
            lastManifestWriteUtc,
            lastSegmentFileName,
            lastSegmentIndex,
            lastSegmentWriteUtc);
    }

    private static int? TryParseSegmentIndex(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var underscore = stem.LastIndexOf('_');
        if (underscore < 0 || underscore == stem.Length - 1)
            return null;

        return int.TryParse(stem[(underscore + 1)..], out var index) ? index : null;
    }

    private sealed record HlsArtifactState(
        bool ManifestExists,
        DateTimeOffset? LastManifestWriteUtc,
        string? LastSegmentFileName,
        int? LastSegmentIndex,
        DateTimeOffset? LastSegmentWriteUtc);

    private sealed record TrackedHlsClient(HlsClientRecord Record, bool IsNewClient);

    private sealed record HlsClientRecord(
        string ClientId,
        string SessionId,
        string RequestedRoute,
        string? RemoteIp,
        string? UserAgent,
        DateTimeOffset ConnectedUtc,
        DateTimeOffset LastAccessUtc,
        bool CountAsViewer);

    private sealed class GeneratedHlsSession(
        string sessionId,
        string streamUrl,
        string displayName,
        string workDirectory,
        string manifestPath,
        Process process,
        ChannelSessionKey? admissionKey = null,
        string? parentStreamSessionId = null,
        string? requestedRoute = null)
    {
        private long _lastAccessUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private Task? _stderrPumpTask;
        private Task? _stdoutPumpTask;
        private IDisposable? _parentActivityRetention;
        private readonly object _clientSync = new();
        private readonly ConcurrentDictionary<string, HlsClientRecord> _hlsClients = new(StringComparer.Ordinal);

        public string SessionId { get; } = sessionId;

        public string StreamUrl { get; } = streamUrl;

        public string DisplayName { get; } = displayName;

        public string WorkDirectory { get; } = Path.GetFullPath(workDirectory);

        public string ManifestPath { get; } = manifestPath;

        public Process Process { get; } = process;

        public ChannelSessionKey? AdmissionKey { get; } = admissionKey;

        public string? ParentStreamSessionId { get; } = parentStreamSessionId;

        public string? RequestedRoute { get; } = requestedRoute;

        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastAccessUtc
            => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastAccessUnixMs));

        public int HlsClientCount => _hlsClients.Values.Count(x => x.CountAsViewer);

        public bool CanReuseFor(GeneratedHlsSessionRequest request)
        {
            if (request.AdmissionKey is null || AdmissionKey is null)
                return false;

            if (Process.HasExited || !File.Exists(ManifestPath))
                return false;

            if (!AdmissionKey.Value.Equals(request.AdmissionKey.Value))
                return false;

            if (!string.Equals(StreamUrl, request.StreamUrl, StringComparison.Ordinal))
                return false;

            return string.Equals(ParentStreamSessionId, request.ParentStreamSessionId, StringComparison.Ordinal);
        }

        public void SetPumpTasks(Task stderrPumpTask, Task stdoutPumpTask)
        {
            _stderrPumpTask = stderrPumpTask;
            _stdoutPumpTask = stdoutPumpTask;
        }

        public void SetParentActivityRetention(IDisposable retention)
        {
            var previous = Interlocked.Exchange(ref _parentActivityRetention, retention);
            previous?.Dispose();
        }

        public void ReleaseParentActivityRetention()
        {
            var retention = Interlocked.Exchange(ref _parentActivityRetention, null);
            retention?.Dispose();
        }

        public Task[] GetPumpTasks()
        {
            if (_stderrPumpTask is null && _stdoutPumpTask is null)
                return [];

            if (_stderrPumpTask is null)
                return [_stdoutPumpTask!];

            if (_stdoutPumpTask is null)
                return [_stderrPumpTask];

            return [_stderrPumpTask, _stdoutPumpTask];
        }

        public void Touch()
            => Interlocked.Exchange(ref _lastAccessUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public static string CreateClientKey(string? remoteIp, string? userAgent)
            => $"{remoteIp}\x1f{userAgent}";

        public static bool HasClientFingerprint(string? remoteIp, string? userAgent)
            => !string.IsNullOrWhiteSpace(remoteIp) || !string.IsNullOrWhiteSpace(userAgent);

        public TrackedHlsClient TrackClient(string? remoteIp, string? userAgent, string requestedRoute, bool countAsViewer)
        {
            var clientKey = CreateClientKey(remoteIp, userAgent);
            var now = DateTimeOffset.UtcNow;

            lock (_clientSync)
            {
                if (_hlsClients.TryGetValue(clientKey, out var existing))
                {
                    var updated = existing with
                    {
                        LastAccessUtc = now,
                        RequestedRoute = requestedRoute,
                        CountAsViewer = existing.CountAsViewer || countAsViewer,
                    };
                    _hlsClients[clientKey] = updated;
                    return new TrackedHlsClient(updated, IsNewClient: false);
                }

                var created = new HlsClientRecord(
                    Guid.NewGuid().ToString("N"),
                    SessionId,
                    requestedRoute,
                    remoteIp,
                    userAgent,
                    now,
                    now,
                    countAsViewer);

                _hlsClients[clientKey] = created;
                return new TrackedHlsClient(created, IsNewClient: true);
            }
        }

        public bool TryEvictClient(string clientKey, DateTimeOffset cutoffUtc, out HlsClientRecord removed)
        {
            lock (_clientSync)
            {
                if (!_hlsClients.TryGetValue(clientKey, out var existing) || existing.LastAccessUtc > cutoffUtc)
                {
                    removed = default!;
                    return false;
                }

                if (_hlsClients.TryRemove(clientKey, out var record))
                {
                    removed = record;
                    return true;
                }

                removed = default!;
                return false;
            }
        }

        public List<HlsClientRecord> SweepStaleClients(TimeSpan timeout)
        {
            var cutoff = DateTimeOffset.UtcNow - timeout;
            var removed = new List<HlsClientRecord>();

            lock (_clientSync)
            {
                foreach (var kvp in _hlsClients.ToArray())
                {
                    if (kvp.Value.LastAccessUtc >= cutoff || !_hlsClients.TryRemove(kvp.Key, out var record))
                        continue;

                    if (record.CountAsViewer)
                        removed.Add(record);
                }
            }

            return removed;
        }

        public List<HlsClientRecord> RemoveAllClients()
        {
            var removed = new List<HlsClientRecord>();
            lock (_clientSync)
            {
                foreach (var key in _hlsClients.Keys.ToArray())
                {
                    if (_hlsClients.TryRemove(key, out var record) && record.CountAsViewer)
                        removed.Add(record);
                }
            }

            return removed;
        }

        public StreamSessionSnapshot ToSnapshot() => new(
            SessionId: SessionId,
            ProviderId: AdmissionKey?.ProviderId ?? string.Empty,
            ProviderChannelId: AdmissionKey?.ProviderChannelId ?? string.Empty,
            DisplayName: DisplayName,
            State: Process.HasExited ? SessionState.Faulted : SessionState.Live,
            SubscriberCount: HlsClientCount,
            IsShared: HlsClientCount > 1,
            BufferUsedBytes: 0,
            BufferMaxBytes: 0,
            StartedUtc: StartedUtc,
            LastUpstreamByteUtc: LastAccessUtc,
            ReconnectAttempts: 0,
            LastFailureKind: null,
            IsInternal: ParentStreamSessionId is not null,
            ParentStreamSessionId: ParentStreamSessionId);
    }
}
