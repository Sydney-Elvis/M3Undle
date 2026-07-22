using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Compatibility;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Upstream;

public sealed class UpstreamStreamConnector(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IStreamChannelHealthProfileService healthProfileService,
    IOptions<ReconnectOptions> reconnectOptions,
    IOptions<GeneratedHlsOptions> hlsOptions,
    IOptions<CleanRelayOptions> cleanRelayOptions,
    ILogger<UpstreamStreamConnector> logger)
{
    private readonly ReconnectOptions _reconnectOptions = reconnectOptions.Value;
    private readonly GeneratedHlsOptions _hlsOptions = hlsOptions.Value;
    private readonly CleanRelayOptions _cleanRelayOptions = cleanRelayOptions.Value;

    public async Task<UpstreamConnection> ConnectAsync(
        StreamSourceDescriptor source,
        StreamChannelRecoveryPolicy? recoveryPolicy,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var provider = await db.Providers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProviderId == source.ProviderId && x.Enabled, ct);

        if (provider is null)
        {
            logger.LogWarning(
                "Cannot start stream for '{DisplayName}' — provider '{ProviderId}' is not found or is disabled.",
                source.DisplayName,
                source.ProviderId);
            throw new UpstreamConnectException(
                $"Provider '{source.ProviderId}' is not available.",
                UpstreamFailureKind.StartupFatal);
        }

        var effectiveStreamUrl = source.StreamUrl;
        if (!string.IsNullOrWhiteSpace(source.ProviderChannelId))
        {
            var refreshedStreamUrl = await db.ProviderChannels
                .AsNoTracking()
                .Where(x => x.ProviderChannelId == source.ProviderChannelId && x.ProviderId == source.ProviderId)
                .Select(x => x.StreamUrl)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(refreshedStreamUrl))
                effectiveStreamUrl = refreshedStreamUrl;
        }

        if (source.ForceMpegTs)
            effectiveStreamUrl = RewriteUrlForMpegTs(effectiveStreamUrl);

        var hlsCandidates = !string.IsNullOrWhiteSpace(_hlsOptions.FfmpegPath)
            ? (source.ForceMpegTs
                ? HlsDetection.GetHlsCandidates(effectiveStreamUrl)
                : HlsDetection.GetExplicitHlsCandidates(effectiveStreamUrl))
            : [];

        if (hlsCandidates.Count > 0)
        {
            var ffmpegConn = await TryFfmpegHlsRelayAsync(hlsCandidates[0], provider, source.DisplayName, ct);
            if (ffmpegConn is not null)
                return ffmpegConn;

            logger.LogWarning(
                "FFmpeg HLS relay startup failed for '{DisplayName}' using candidate '{HlsUrl}'. Falling back to direct HTTP.",
                source.DisplayName,
                hlsCandidates[0]);
        }

        var relayDecision = recoveryPolicy is null
            ? ResolveLegacyRelayDecision(provider.CleanRelayMode)
            : healthProfileService.GetRelayPolicyDecision(provider.CleanRelayMode, recoveryPolicy);
        string? relayFallbackReason = null;
        if (string.Equals(relayDecision.SelectedRelayMode, UpstreamRelayModes.FfmpegCleanRemux, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Clean remux selected for '{DisplayName}'. ProviderRelayPolicy={ProviderRelayPolicy} Reason={RelayDecisionReason}",
                source.DisplayName,
                relayDecision.ProviderRelayPolicy,
                relayDecision.Reason);
            var cleanRelayConnection = await TryFfmpegCleanRemuxRelayAsync(effectiveStreamUrl, provider, source.DisplayName, relayDecision, ct);
            if (cleanRelayConnection is not null)
                return cleanRelayConnection;

            relayFallbackReason = "clean_relay_startup_failed";
            if (!_cleanRelayOptions.FallbackToDirect)
            {
                throw new UpstreamConnectException(
                    "FFmpeg clean relay startup failed.",
                    UpstreamFailureKind.Transport);
            }

            logger.LogWarning(
                "FFmpeg clean relay startup failed for '{DisplayName}'. Falling back to direct HTTP relay.",
                source.DisplayName);
        }

        var client = httpClientFactory.CreateClient("stream-relay");
        ProviderFetcher.ApplyHeadersFromJson(client, provider.HeadersJson);
        if (!string.IsNullOrWhiteSpace(provider.UserAgent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_reconnectOptions.ConnectTimeout);

        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, effectiveStreamUrl);
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                connectCts.Token);

            var statusCode = (int)response.StatusCode;
            if (statusCode is 401 or 403)
            {
                response.Dispose();
                logger.LogWarning(
                    "Provider rejected the stream request for '{DisplayName}' with {StatusCode} — check your provider credentials.",
                    source.DisplayName,
                    statusCode);
                throw new UpstreamConnectException("Provider authorization rejected stream request.", UpstreamFailureKind.UpstreamAuth, statusCode);
            }
            if (statusCode == 404)
            {
                response.Dispose();
                logger.LogWarning(
                    "Provider returned 404 for '{DisplayName}' — the stream URL may be invalid or the channel is unavailable.",
                    source.DisplayName);
                throw new UpstreamConnectException("Provider stream endpoint not found.", UpstreamFailureKind.UpstreamNotFound, statusCode);
            }
            if (statusCode == 407)
            {
                var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                response.Dispose();
                logger.LogWarning(
                    "Provider returned 407 for '{DisplayName}' — stream requests will cool down. RetryAfter={RetryAfterSeconds}s.",
                    source.DisplayName,
                    retryAfter?.TotalSeconds);
                throw new UpstreamConnectException(
                    "Provider proxy authentication rejected stream request.",
                    UpstreamFailureKind.UpstreamProxyAuthRequired,
                    statusCode,
                    retryAfter: retryAfter);
            }
            if (statusCode == 429)
            {
                var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
                response.Dispose();
                logger.LogWarning(
                    "Provider rate-limited stream request for '{DisplayName}' with 429. RetryAfter={RetryAfterSeconds}s.",
                    source.DisplayName,
                    retryAfter?.TotalSeconds);
                throw new UpstreamConnectException(
                    "Provider rate-limited stream request.",
                    UpstreamFailureKind.UpstreamRateLimited,
                    statusCode,
                    retryAfter: retryAfter);
            }
            if (statusCode >= 500)
            {
                response.Dispose();
                logger.LogWarning(
                    "Provider returned a server error ({StatusCode}) for '{DisplayName}' — will retry.",
                    statusCode,
                    source.DisplayName);
                throw new UpstreamConnectException($"Upstream returned {statusCode}.", UpstreamFailureKind.UpstreamServerError, statusCode);
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new UpstreamConnectException($"Upstream returned non-success status {statusCode}.", UpstreamFailureKind.StartupFatal, statusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            logger.LogInformation(
                "Connected to upstream for '{DisplayName}' — HTTP {Status}, content type: {ContentType}.",
                source.DisplayName,
                statusCode,
                response.Content.Headers.ContentType?.ToString() ?? "unknown");

            var connection = new UpstreamConnection(
                client,
                response,
                stream,
                relayMode: UpstreamRelayModes.Direct,
                relayFallbackReason: relayFallbackReason,
                relayPolicy: relayDecision.ProviderRelayPolicy,
                relayDecisionReason: relayDecision.Reason);
            response = null; // ownership transferred to UpstreamConnection
            return connection;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            response?.Dispose();
            client.Dispose();
            throw new UpstreamConnectException("Upstream connection attempt timed out.", UpstreamFailureKind.TimeoutOrStall, null, ex);
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            client.Dispose();
            throw new UpstreamConnectException("Upstream request failed.", UpstreamFailureKind.Transport, ex.StatusCode is null ? null : (int)ex.StatusCode, ex);
        }
        catch (UpstreamConnectException)
        {
            response?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public Task<UpstreamConnection> ConnectAsync(StreamSourceDescriptor source, CancellationToken ct)
        => ConnectAsync(source, recoveryPolicy: null, ct);

    private static StreamRelayPolicyDecision ResolveLegacyRelayDecision(string? cleanRelayMode)
    {
        var normalized = CleanRelayModes.Normalize(cleanRelayMode);
        return CleanRelayModes.IsRemux(normalized)
            ? StreamRelayPolicyDecision.CleanRemux(normalized, "Provider relay policy is On; clean remux is forced for this provider.")
            : StreamRelayPolicyDecision.Direct(normalized, "Provider relay policy is Off; direct relay is forced for this provider.");
    }

    internal static string RewriteUrlForMpegTs(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var path = uri.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = path[..^5];
            uri = new UriBuilder(uri) { Path = stripped }.Uri;
        }

        var query = uri.Query;
        if (string.IsNullOrEmpty(query))
            return uri.ToString();

        var pairs = query.TrimStart('?').Split('&');
        var modified = false;
        for (var i = 0; i < pairs.Length; i++)
        {
            var kv = pairs[i].Split('=', 2);
            if (kv.Length != 2) continue;
            var key = Uri.UnescapeDataString(kv[0]);
            if (!key.Equals("output", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(kv[1]);
            if (value.Equals("m3u8", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("hls", StringComparison.OrdinalIgnoreCase))
            {
                pairs[i] = $"{kv[0]}=ts";
                modified = true;
            }
        }

        if (!modified)
            return uri.ToString();

        return new UriBuilder(uri) { Query = string.Join("&", pairs) }.Uri.ToString();
    }

    public UpstreamFailureKind Classify(Exception ex, int? statusCode = null)
    {
        if (ex is UpstreamConnectException connectException)
            return connectException.FailureKind;

        if (ex is OperationCanceledException)
            return UpstreamFailureKind.TimeoutOrStall;

        if (ex is HttpRequestException httpEx)
        {
            var code = statusCode ?? (httpEx.StatusCode is null ? null : (int)httpEx.StatusCode);
            if (code is 401 or 403)
                return UpstreamFailureKind.UpstreamAuth;
            if (code == 404)
                return UpstreamFailureKind.UpstreamNotFound;
            if (code == 407)
                return UpstreamFailureKind.UpstreamProxyAuthRequired;
            if (code == 429)
                return UpstreamFailureKind.UpstreamRateLimited;
            if (code >= 500)
                return UpstreamFailureKind.UpstreamServerError;
            return UpstreamFailureKind.Transport;
        }

        return UpstreamFailureKind.Unknown;
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;

        if (retryAfter.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    private async Task<UpstreamConnection?> TryFfmpegHlsRelayAsync(
        string hlsUrl,
        Provider provider,
        string displayName,
        CancellationToken ct)
    {
        return await TryFfmpegRelayAsync(
            inputUrl: hlsUrl,
            provider: provider,
            displayName: displayName,
            ffmpegPath: _hlsOptions.FfmpegPath,
            startupTimeout: TimeSpan.FromSeconds(10),
            startupBufferBytes: 8 * 1024,
            relayMode: UpstreamRelayModes.FfmpegHlsToMpegTs,
            relayDescription: "HLS relay",
            includeCleanRepairFlags: false,
            isHlsInput: true,
            ct: ct);
    }

    private async Task<UpstreamConnection?> TryFfmpegCleanRemuxRelayAsync(
        string inputUrl,
        Provider provider,
        string displayName,
        StreamRelayPolicyDecision relayDecision,
        CancellationToken ct)
    {
        var ffmpegPath = ResolveCleanRelayFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            logger.LogWarning(
                "FFmpeg clean relay requested for '{DisplayName}' but no FFmpeg path is configured.",
                displayName);
            return null;
        }

        return await TryFfmpegRelayAsync(
            inputUrl: inputUrl,
            provider: provider,
            displayName: displayName,
            ffmpegPath: ffmpegPath,
            startupTimeout: TimeSpan.FromSeconds(_cleanRelayOptions.StartupTimeoutSeconds),
            startupBufferBytes: _cleanRelayOptions.MaxStartupBytes,
            relayMode: UpstreamRelayModes.FfmpegCleanRemux,
            relayPolicy: relayDecision.ProviderRelayPolicy,
            relayDecisionReason: relayDecision.Reason,
            relayDescription: "clean remux relay",
            includeCleanRepairFlags: true,
            isHlsInput: false,
            ct: ct);
    }

    private string? ResolveCleanRelayFfmpegPath()
        => string.IsNullOrWhiteSpace(_cleanRelayOptions.FfmpegPath)
            ? _hlsOptions.FfmpegPath
            : _cleanRelayOptions.FfmpegPath;

    private async Task<UpstreamConnection?> TryFfmpegRelayAsync(
        string inputUrl,
        Provider provider,
        string displayName,
        string? ffmpegPath,
        TimeSpan startupTimeout,
        int startupBufferBytes,
        string relayMode,
        string relayDescription,
        bool includeCleanRepairFlags,
        bool isHlsInput,
        CancellationToken ct,
        string? relayPolicy = null,
        string? relayDecisionReason = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return null;

        var info = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Clean remux also receives the upstream .m3u8 directly, so detect HLS by the
        // actual input URL, not just the dedicated HLS-relay path flag.
        var inputIsHls = isHlsInput || HlsDetection.GetExplicitHlsCandidates(inputUrl).Count > 0;

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-nostdin");
        info.ArgumentList.Add("-loglevel");
        info.ArgumentList.Add("warning");
        info.ArgumentList.Add("-reconnect");
        info.ArgumentList.Add("1");
        // -reconnect_at_eof is fatal for HLS: every playlist/segment fetch ends in a
        // normal EOF, which FFmpeg's HTTP layer then treats as a failure and keeps
        // reconnecting the *playlist* instead of pulling segments — producing zero
        // output. Only apply it to continuous (non-HLS) inputs.
        //
        // It stays enabled for non-HLS despite letting FFmpeg reconnect and re-mux
        // a provider's replay-from-byte-zero inside a single continuous process:
        // MPEG-TS's muxer must enforce non-decreasing output DTS, so it silently
        // clamps the restarted (backward) timestamps forward rather than emitting
        // them (confirmed via real-FFmpeg spikes — neither -copyts nor removing
        // this flag surfaced the jump; -reconnect_streamed alone reconnects across
        // this exact failure mode too, so removing just this flag bought nothing).
        // Detection therefore lives downstream instead: ChannelStreamSession's
        // DetectInProcessTimelineRewind also recognizes the clamped-ramp signature
        // (a run of near-zero-tick DTS increments) as a proxy for the jump the
        // muxer absorbed, so FFmpeg keeps its full internal self-healing and the
        // recovery path still fires.
        if (!inputIsHls)
        {
            info.ArgumentList.Add("-reconnect_at_eof");
            info.ArgumentList.Add("1");
        }
        // Issue #96: let FFmpeg ride out provider blips itself instead of exiting
        // (which makes M3Undle tear down and reconnect the whole session). Reconnect
        // on network errors too. reconnect_delay_max is the delay after which FFmpeg
        // GIVES UP; keep it well above FfmpegRelayStallTimeout so FFmpeg is still
        // actively retrying throughout M3Undle's teardown window rather than sitting
        // idle after an early give-up.
        info.ArgumentList.Add("-reconnect_on_network_error");
        info.ArgumentList.Add("1");
        info.ArgumentList.Add("-reconnect_delay_max");
        info.ArgumentList.Add("30");
        if (!inputIsHls)
        {
            info.ArgumentList.Add("-reconnect_streamed");
            info.ArgumentList.Add("1");
        }
        info.ArgumentList.Add("-fflags");
        info.ArgumentList.Add("+genpts+discardcorrupt");
        if (includeCleanRepairFlags)
        {
            info.ArgumentList.Add("-err_detect");
            info.ArgumentList.Add("ignore_err");
            info.ArgumentList.Add("-avoid_negative_ts");
            info.ArgumentList.Add("make_zero");
        }

        if (!string.IsNullOrWhiteSpace(provider.UserAgent))
        {
            info.ArgumentList.Add("-user_agent");
            info.ArgumentList.Add(provider.UserAgent);
        }

        var headersArg = BuildFfmpegHeadersArgument(
            provider.HeadersJson,
            excludeUserAgent: !string.IsNullOrWhiteSpace(provider.UserAgent));
        if (!string.IsNullOrWhiteSpace(headersArg))
        {
            info.ArgumentList.Add("-headers");
            info.ArgumentList.Add(headersArg);
        }

        if (isHlsInput)
        {
            info.ArgumentList.Add("-re");
            info.ArgumentList.Add("-allowed_extensions");
            info.ArgumentList.Add("ALL");
        }

        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(inputUrl);
        // For HLS inputs, omit -map entirely: FFmpeg's default stream selection picks the best
        // variant from the master playlist without pulling in all quality levels as separate
        // video streams. Explicit -map 0:v? would include every variant and produce a
        // multi-video MPEG-TS that downstream segmenters warn about and browser players reject.
        if (!inputIsHls)
        {
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add("0:v?");
            info.ArgumentList.Add("-map");
            info.ArgumentList.Add("0:a?");
        }
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("copy");
        info.ArgumentList.Add("-muxpreload");
        info.ArgumentList.Add("0");
        info.ArgumentList.Add("-muxdelay");
        info.ArgumentList.Add("0");
        if (includeCleanRepairFlags)
        {
            info.ArgumentList.Add("-bsf:v");
            info.ArgumentList.Add("dump_extra=freq=keyframe");
        }
        info.ArgumentList.Add("-mpegts_flags");
        info.ArgumentList.Add("+resend_headers");
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add("mpegts");
        info.ArgumentList.Add("pipe:1");

        Process process;
        try
        {
            process = new Process { StartInfo = info };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FFmpeg {RelayDescription} process start failed for '{DisplayName}'.", relayDescription, displayName);
            return null;
        }

        // Drain stderr so the process does not block on a full stderr buffer
        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line is null) break;
                    logger.LogDebug("FFmpeg/{RelayMode}[{DisplayName}]: {Line}", relayMode, displayName, line);
                }
            }
            catch { }
        }, CancellationToken.None);

        var startupBuffer = new byte[Math.Max(188, startupBufferBytes)];
        var stdout = process.StandardOutput.BaseStream;
        int startupBytes;

        try
        {
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startupCts.CancelAfter(startupTimeout);
            startupBytes = await stdout.ReadAsync(startupBuffer.AsMemory(), startupCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "FFmpeg {RelayDescription} for '{DisplayName}' produced no output within {StartupTimeoutSeconds:F0} s.",
                relayDescription,
                displayName,
                startupTimeout.TotalSeconds);
            TryKillProcess(process);
            process.Dispose();
            return null;
        }
        catch
        {
            TryKillProcess(process);
            process.Dispose();
            throw;
        }

        if (startupBytes == 0)
        {
            logger.LogWarning(
                "FFmpeg {RelayDescription} for '{DisplayName}' exited with no output.",
                relayDescription,
                displayName);
            TryKillProcess(process);
            process.Dispose();
            return null;
        }

        logger.LogInformation(
            "FFmpeg {RelayMode} started for '{DisplayName}' ({StartupBytes} startup bytes).",
            relayMode,
            displayName,
            startupBytes);

        return new UpstreamConnection(
            process,
            new PrefixedStream(startupBuffer.AsMemory(0, startupBytes), stdout),
            "video/mp2t",
            relayMode: relayMode,
            relayPolicy: relayPolicy,
            relayDecisionReason: relayDecisionReason);
    }

    private static void TryKillProcess(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }

    private static string? BuildFfmpegHeadersArgument(string? headersJson, bool excludeUserAgent)
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

                if (excludeUserAgent
                    && property.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
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

    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;
        private readonly Stream _inner = inner;
        private int _prefixOffset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (buffer.Length - offset < count)
                throw new ArgumentException("Offset and count must refer to a valid range in the buffer.");

            if (_prefixOffset < _prefix.Length)
            {
                var bytesToCopy = Math.Min(count, _prefix.Length - _prefixOffset);
                _prefix.Span.Slice(_prefixOffset, bytesToCopy).CopyTo(buffer.AsSpan(offset, bytesToCopy));
                _prefixOffset += bytesToCopy;
                return bytesToCopy;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (buffer.Length - offset < count)
                throw new ArgumentException("Offset and count must refer to a valid range in the buffer.");

            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var bytesToCopy = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
                _prefix.Span.Slice(_prefixOffset, bytesToCopy).CopyTo(buffer.Span[..bytesToCopy]);
                _prefixOffset += bytesToCopy;
                return ValueTask.FromResult(bytesToCopy);
            }

            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
