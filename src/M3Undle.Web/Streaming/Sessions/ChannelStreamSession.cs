using System.Collections.Concurrent;
using M3Undle.Core.MpegTs;
using M3Undle.Web.Streaming.Buffering;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Subscribers;
using M3Undle.Web.Streaming.Upstream;

namespace M3Undle.Web.Streaming.Sessions;

public sealed class ChannelStreamSession : IAsyncDisposable
{
    private readonly BufferOptions _bufferOptions;
    private readonly StreamProxyOptions _proxyOptions;
    private readonly ReconnectOptions _reconnectOptions;
    private readonly UpstreamStreamConnector _upstreamConnector;
    private readonly UpstreamFailureStrikeStore _strikeStore;
    private readonly StreamingRegistry _registry;
    private readonly StreamingDiagnosticsStore _diagnosticsStore;
    private readonly ILogger<ChannelStreamSession> _logger;
    private readonly Func<ChannelSessionKey, ChannelStreamSession, Task> _onClosed;
    private readonly RingBuffer _buffer;
    private readonly ConcurrentDictionary<string, SubscriberConnection> _subscribers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly TaskCompletionSource<bool> _headersReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly TimeSpan _idleGrace;

    private StreamSourceDescriptor _source;
    private SessionState _state = SessionState.Initializing;
    private Task? _runTask;
    private long _lastPublishTick;
    private string? _contentType;
    private string? _cacheControl;
    private DateTimeOffset? _lastUpstreamByteUtc;
    private int _reconnectAttempts;
    private string? _lastFailureKind;
    private int? _lastUpstreamStatusCode;
    private double? _lastCooldownSeconds;
    private double? _lastFirstByteLatencyMs;
    private int _closeNotified;
    private int _pendingSubscriberAttaches;
    private int _stopRequested;
    private int _retainedExternalActivityCount;
    private long _totalBytesRelayed;
    private long _bytesSinceReconnect;
    private string? _pendingStopTrigger;
    private string? _lastStopTrigger;
    private string? _lastDisconnectReason;
    private DateTimeOffset? _connectAttemptStartedUtc;
    private bool _awaitingFirstByte;
    private readonly MpegTsBoundaryScanner _mpegTsScanner = new();
    private bool _mpegTsSafeStartSelected;
    private int? _mpegTsCandidateSafeStartGeneration;
    private long? _mpegTsCandidateSafeStartSequence;
    private long _mpegTsBytesSinceReset;
    private int _mpegTsProbeBytes;
    private bool _mpegTsPacketModeKnown;
    private bool _mpegTsPacketizeEnabled;
    private CancellationTokenSource? _idleCts;
    private string? _lastIdleGraceRemoteIp;

    public ChannelStreamSession(
        StreamSourceDescriptor source,
        BufferOptions bufferOptions,
        StreamProxyOptions proxyOptions,
        ReconnectOptions reconnectOptions,
        UpstreamStreamConnector upstreamConnector,
        UpstreamFailureStrikeStore strikeStore,
        StreamingRegistry registry,
        StreamingDiagnosticsStore diagnosticsStore,
        ILogger<ChannelStreamSession> logger,
        Func<ChannelSessionKey, ChannelStreamSession, Task> onClosed)
    {
        _source = source;
        _bufferOptions = bufferOptions;
        _proxyOptions = proxyOptions;
        _reconnectOptions = reconnectOptions;
        _upstreamConnector = upstreamConnector;
        _strikeStore = strikeStore;
        _registry = registry;
        _diagnosticsStore = diagnosticsStore;
        _logger = logger;
        _onClosed = onClosed;
        _idleGrace = ResolveIdleGrace(_proxyOptions);

        var maxBytes = Math.Clamp(_bufferOptions.MaxBytesPerSession, 1, _bufferOptions.MaxBytesHardCap);
        _buffer = new RingBuffer(maxBytes);
        RecordDiagnostic(StreamDiagnosticEventKind.SessionCreated, message: "Shared stream session created.");
    }

    public ChannelSessionKey Key => _source.SessionKey;

    public string SessionId => _sessionId;

    public SessionState State => _state;

    public int SubscriberCount => _subscribers.Count;

    public int ExternalSubscriberCount => _subscribers.Values.Count(s => !s.IsInternal);

    public int InternalSubscriberCount => _subscribers.Values.Count(s => s.IsInternal);

    public bool CanPreemptIdleGraceForRemoteIp(string? remoteIp)
    {
        if (string.IsNullOrWhiteSpace(remoteIp))
            return false;

        lock (_gate)
        {
            return _idleCts is not null
                && string.Equals(_pendingStopTrigger, "idle_grace", StringComparison.Ordinal)
                && ShouldScheduleIdleShutdownNoLock()
                && string.Equals(_lastIdleGraceRemoteIp, remoteIp, StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<SubscriberConnection> AttachSubscriberAsync(
        HttpContext context,
        CancellationToken requestCt,
        bool isInternal = false)
    {
        BeginSubscriberAttach();

        try
        {
            EnsureStarted();
            await _headersReadyTcs.Task.WaitAsync(requestCt);

            var subscriber = new SubscriberConnection(
                sessionId: _sessionId,
                requestedRoute: _source.RequestedRoute,
                context: context,
                queueCapacity: _bufferOptions.SubscriberQueueCapacity,
                onCompleted: (s, reason) => RemoveSubscriberAsync(s, reason),
                isInternal: isInternal);

            _subscribers[subscriber.ClientId] = subscriber;

            subscriber.InitializeResponse(_contentType, _cacheControl);
            _ = subscriber.StartAsync(CreateSubscriberStartupSnapshot(), _sessionCts.Token);
            RecordDiagnostic(
                StreamDiagnosticEventKind.SubscriberAttached,
                subscriber: subscriber,
                message: "Subscriber attached.");
            if (!isInternal)
                _registry.UpsertClient(subscriber.Snapshot());
            PublishSnapshots();
            LogSubscriberAttached(subscriber);

            return subscriber;
        }
        finally
        {
            EndSubscriberAttach();
        }
    }

    public IDisposable RetainExternalActivity()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _stopRequested) == 1 || Volatile.Read(ref _closeNotified) == 1)
                return NoopDisposable.Instance;

            _retainedExternalActivityCount++;
            CancelIdleShutdownNoLock();
        }

        PublishSnapshots();
        return new ExternalActivityRetention(this);
    }

    public Task RemoveSubscriberAsync(SubscriberConnection subscriber, SubscriberDisconnectReason reason)
    {
        if (_subscribers.TryRemove(subscriber.ClientId, out _))
        {
            if (!subscriber.IsInternal)
                _registry.RemoveClient(subscriber.ClientId);
        }

        LogSubscriberRemoved(subscriber, reason);
        _lastDisconnectReason = reason.ToString();
        RecordDiagnostic(
            StreamDiagnosticEventKind.SubscriberRemoved,
            subscriber: subscriber,
            disconnectReason: reason,
            message: "Subscriber removed.");

        lock (_gate)
        {
            if (ShouldScheduleIdleShutdownNoLock())
            {
                if (!subscriber.IsInternal)
                    _lastIdleGraceRemoteIp = subscriber.RemoteIp;

                LogStopTrigger(
                    ResolveDisconnectStopTrigger(reason),
                    subscriberDisconnectReason: reason.ToString());
                ScheduleIdleShutdownNoLock();
            }
        }

        PublishSnapshots();
        return Task.CompletedTask;
    }

    public async Task StopAsync(string stopTrigger = "session_closed")
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
            return;

        lock (_gate)
        {
            _pendingStopTrigger = stopTrigger;
            CancelIdleShutdownNoLock();
        }

        LogStopTrigger(stopTrigger);
        _sessionCts.Cancel();

        var subscribers = _subscribers.Values.ToArray();
        foreach (var subscriber in subscribers)
        {
            await subscriber.CompleteAsync(SubscriberDisconnectReason.SessionClosed);
        }

        if (_runTask is not null)
            await _runTask;
        else
        {
            SetState(SessionState.Closed);
            _registry.RemoveSession(_sessionId);
            await NotifyClosedAsync();
        }

        _buffer.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync("shutdown");
        _sessionCts.Dispose();
    }

    private void EnsureStarted()
    {
        bool justStarted = false;

        lock (_gate)
        {
            if (_runTask is not null)
                return;

            _logger.LogInformation(
                "Starting shared stream session {SessionId} for {ProviderId}/{ProviderChannelId}.",
                _sessionId,
                _source.ProviderId,
                _source.ProviderChannelId);
            SetState(SessionState.Connecting);
            _runTask = Task.Run(RunAsync);
            justStarted = true;
        }

        if (justStarted)
        {
            lock (_gate)
            {
                if (ShouldScheduleIdleShutdownNoLock())
                    ScheduleIdleShutdownNoLock();
            }
        }
    }

    private async Task RunAsync()
    {
        DateTimeOffset? outageStartedUtc = null;
        var reconnectAttempt = 0;

        try
        {
            while (!_sessionCts.IsCancellationRequested)
            {
                try
                {
                    SetState(reconnectAttempt == 0 ? SessionState.Connecting : SessionState.Reconnecting);

                    _connectAttemptStartedUtc = DateTimeOffset.UtcNow;
                    _awaitingFirstByte = true;
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.UpstreamConnectStarted,
                        reconnectAttempt: reconnectAttempt,
                        message: "Opening upstream stream connection.");
                    await using var upstream = await _upstreamConnector.ConnectAsync(_source, _sessionCts.Token);
                    _lastUpstreamStatusCode = upstream.StatusCode;
                    _contentType = upstream.ContentType;
                    _cacheControl = upstream.Response?.Headers.CacheControl?.ToString();
                    _headersReadyTcs.TrySetResult(true);
                    Interlocked.Exchange(ref _bytesSinceReconnect, 0);
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.UpstreamConnected,
                        httpStatusCode: upstream.StatusCode,
                        reconnectAttempt: reconnectAttempt,
                        message: "Connected to upstream stream.");

                    if (reconnectAttempt > 0)
                    {
                        _logger.LogInformation(
                            "Stream '{DisplayName}' recovered successfully after {Attempts} reconnect attempt(s).",
                            _source.DisplayName,
                            reconnectAttempt);
                        _buffer.ResetGeneration();
                        ResetMpegTsBoundaryState();
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.ReconnectRecovered,
                            httpStatusCode: upstream.StatusCode,
                            reconnectAttempt: reconnectAttempt,
                            message: "Upstream stream recovered after reconnect.");
                    }
                    else
                    {
                        ResetMpegTsBoundaryState();
                    }

                    reconnectAttempt = 0;
                    outageStartedUtc = null;
                    SetState(SessionState.Live);

                    _logger.LogInformation(
                        "Stream '{DisplayName}' is live — content type: {ContentType}.",
                        _source.DisplayName,
                        _contentType ?? "unknown");

                    PublishSnapshots();
                    await ReadFromUpstreamAsync(upstream, _sessionCts.Token);

                    throw new UpstreamConnectException(
                        "Upstream stream ended.",
                        UpstreamFailureKind.EndOfStream,
                        upstream.StatusCode);
                }
                catch (OperationCanceledException) when (_sessionCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var kind = ex is UpstreamConnectException connectEx
                        ? connectEx.FailureKind
                        : _upstreamConnector.Classify(ex);

                    _lastFailureKind = kind.ToString();
                    _lastUpstreamStatusCode = ex is UpstreamConnectException connectExForStatus
                        ? connectExForStatus.StatusCode
                        : null;
                    _reconnectAttempts++;
                    reconnectAttempt++;
                    LogUpstreamFailure(kind);
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.UpstreamFailure,
                        upstreamFailureKind: kind,
                        httpStatusCode: _lastUpstreamStatusCode,
                        reconnectAttempt: reconnectAttempt,
                        message: ex.Message);
                    _logger.LogWarning(
                        "Session {SessionId} upstream failure kind={FailureKind} attempt={Attempt}.",
                        _sessionId,
                        kind,
                        reconnectAttempt);
                    PublishSnapshots();

                    if (ShouldCooldownImmediately(kind))
                    {
                        var retryAfter = ResolveCooldownDuration(ex);
                        _lastCooldownSeconds = retryAfter.TotalSeconds;
                        _strikeStore.RecordStrike(Key, retryAfter);
                        _logger.LogWarning(
                            "Session {SessionId} entering upstream cooldown for {ProviderId}/{ProviderChannelId}. kind={FailureKind} retryAfterSeconds={RetryAfterSeconds:F0}.",
                            _sessionId,
                            _source.ProviderId,
                            _source.ProviderChannelId,
                            kind,
                            retryAfter.TotalSeconds);

                        if (!_headersReadyTcs.Task.IsCompleted)
                        {
                            _headersReadyTcs.TrySetException(new StreamAdmissionException(
                                $"Upstream source is cooling down for {retryAfter.TotalSeconds:F0}s.",
                                StreamAdmissionFailureKind.Cooldown,
                                StatusCodes.Status503ServiceUnavailable,
                                retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(Math.Min(30, retryAfter.TotalSeconds)))));
                        }

                        MarkPendingStopTrigger("upstream_cooldown");
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.CooldownRecorded,
                            upstreamFailureKind: kind,
                            httpStatusCode: _lastUpstreamStatusCode,
                            cooldownSeconds: retryAfter.TotalSeconds,
                            retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(Math.Min(30, retryAfter.TotalSeconds))),
                            message: "Upstream cooldown recorded.");
                        LogStopTrigger("upstream_cooldown", subscriberDisconnectReason: kind.ToString());
                        SetState(SessionState.Faulted);
                        await ForceCloseSubscribersAsync();
                        break;
                    }

                    if (IsFatal(kind))
                    {
                        _logger.LogError(
                            "Stream '{DisplayName}' encountered a fatal error ({FailureKind}) and cannot recover. Check your provider settings.",
                            _source.DisplayName,
                            kind);

                        if (!_headersReadyTcs.Task.IsCompleted)
                            _headersReadyTcs.TrySetException(ex);

                        MarkPendingStopTrigger("upstream_fault");
                        LogStopTrigger("upstream_fault", subscriberDisconnectReason: kind.ToString());
                        SetState(SessionState.Faulted);
                        await ForceCloseSubscribersAsync();
                        break;
                    }

                    outageStartedUtc ??= DateTimeOffset.UtcNow;
                    var outageDuration = DateTimeOffset.UtcNow - outageStartedUtc.Value;
                    if (outageDuration >= _reconnectOptions.OutageWindow)
                    {
                        _strikeStore.RecordStrike(Key, _reconnectOptions.StrikeCooldown);
                        _headersReadyTcs.TrySetException(new TimeoutException("Reconnect outage window exhausted."));
                        MarkPendingStopTrigger("upstream_fault");
                        LogStopTrigger("upstream_fault", subscriberDisconnectReason: "outage_window_exhausted");
                        SetState(SessionState.Faulted);
                        await ForceCloseSubscribersAsync();
                        _logger.LogWarning(
                            "Session {SessionId} outage window exhausted; entering cooldown for {ProviderId}/{ProviderChannelId}.",
                            _sessionId,
                            _source.ProviderId,
                            _source.ProviderChannelId);
                        break;
                    }

                    var delay = GetReconnectDelay(reconnectAttempt);
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.ReconnectScheduled,
                        upstreamFailureKind: kind,
                        httpStatusCode: _lastUpstreamStatusCode,
                        reconnectAttempt: reconnectAttempt,
                        reconnectDelay: delay,
                        message: "Upstream reconnect scheduled.");
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, _sessionCts.Token);
                }
            }
        }
        finally
        {
            if (_state != SessionState.Faulted)
                SetState(SessionState.Closed);

            PublishSnapshots();
            _registry.RemoveSession(_sessionId);
            await NotifyClosedAsync();
        }
    }

    private async Task ReadFromUpstreamAsync(UpstreamConnection upstream, CancellationToken ct)
    {
        var readBuffer = new byte[Math.Max(188, _bufferOptions.ReadChunkSizeBytes)];

        while (!ct.IsCancellationRequested)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(_reconnectOptions.ReadStallTimeout);

            int bytesRead;
            try
            {
                bytesRead = await upstream.Stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), readCts.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new UpstreamConnectException("Upstream read stalled.", UpstreamFailureKind.TimeoutOrStall, null, ex);
            }

            if (bytesRead == 0)
            {
                throw new UpstreamConnectException("Upstream EOF.", UpstreamFailureKind.EndOfStream);
            }

            _lastUpstreamByteUtc = DateTimeOffset.UtcNow;
            if (_awaitingFirstByte)
            {
                _awaitingFirstByte = false;
                _lastFirstByteLatencyMs = _connectAttemptStartedUtc is { } started
                    ? (_lastUpstreamByteUtc.Value - started).TotalMilliseconds
                    : null;
                RecordDiagnostic(
                    StreamDiagnosticEventKind.FirstUpstreamByte,
                    firstByteLatencyMs: _lastFirstByteLatencyMs,
                    message: "Received first upstream byte after connect.");
            }
            Interlocked.Add(ref _totalBytesRelayed, bytesRead);
            Interlocked.Add(ref _bytesSinceReconnect, bytesRead);
            if (IsMpegTsRelay() && ShouldPacketizeMpegTs(readBuffer.AsSpan(0, bytesRead)))
            {
                var batch = _mpegTsScanner.Process(readBuffer.AsSpan(0, bytesRead));
                if (batch is null)
                    continue;

                if (batch.DroppedByteCount > 0 || batch.SyncLost)
                {
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.MpegTsSyncLost,
                        message: $"MPEG-TS sync rescan dropped {batch.DroppedByteCount} byte(s).");
                }

                if (batch.Data.Length == 0)
                    continue;

                Interlocked.Add(ref _mpegTsBytesSinceReset, batch.Data.Length);
                using var published = _buffer.Write(batch.Data);
                MarkMpegTsSafeStartIfReady(published, batch.StartupKind);
                await PublishToSubscribersAsync(published);
            }
            else
            {
                using var published = _buffer.Write(readBuffer.AsMemory(0, bytesRead));
                await PublishToSubscribersAsync(published);
            }

            var tick = Environment.TickCount64;
            if (tick - _lastPublishTick >= 100)
            {
                _lastPublishTick = tick;
                PublishSnapshots();
            }
        }
    }

    private async Task PublishToSubscribersAsync(BufferLease published)
    {
        List<SubscriberConnection>? slowSubscribers = null;

        foreach (var subscriber in _subscribers.Values)
        {
            var perSubscriber = published.Duplicate();
            if (!subscriber.TryEnqueue(perSubscriber))
            {
                perSubscriber.Dispose();
                slowSubscribers ??= [];
                slowSubscribers.Add(subscriber);
                continue;
            }

            if (!subscriber.IsInternal)
                _registry.UpsertClient(subscriber.Snapshot());
        }

        if (slowSubscribers is not null)
        {
            foreach (var slow in slowSubscribers)
                await slow.CompleteAsync(SubscriberDisconnectReason.SlowClient);
        }
    }

    private BufferSnapshot CreateSubscriberStartupSnapshot()
        => IsMpegTsRelay() ? _buffer.CreateSafeStartSnapshot() : _buffer.CreateLiveEdgeSnapshot();

    private bool IsMpegTsRelay()
        => _contentType?.Contains("MP2T", StringComparison.OrdinalIgnoreCase) == true
           || _contentType?.Contains("mpegts", StringComparison.OrdinalIgnoreCase) == true;

    private void ResetMpegTsBoundaryState()
    {
        _mpegTsScanner.Reset();
        _mpegTsSafeStartSelected = false;
        _mpegTsCandidateSafeStartGeneration = null;
        _mpegTsCandidateSafeStartSequence = null;
        _mpegTsProbeBytes = 0;
        _mpegTsPacketModeKnown = false;
        _mpegTsPacketizeEnabled = false;
        Interlocked.Exchange(ref _mpegTsBytesSinceReset, 0);
    }

    private bool ShouldPacketizeMpegTs(ReadOnlySpan<byte> data)
    {
        if (_mpegTsPacketModeKnown)
            return _mpegTsPacketizeEnabled;

        if (HasLikelyMpegTsSync(data))
        {
            _mpegTsPacketModeKnown = true;
            _mpegTsPacketizeEnabled = true;
            return true;
        }

        _mpegTsProbeBytes += data.Length;
        if (_mpegTsProbeBytes < MpegTsBoundaryScanner.PacketSize * 4)
            return true;

        _mpegTsPacketModeKnown = true;
        _mpegTsPacketizeEnabled = false;
        RecordDiagnostic(
            StreamDiagnosticEventKind.MpegTsPacketizerDisabled,
            message: "MPEG-TS packetizer disabled after probe found no sync byte; using pass-through relay.");
        return false;
    }

    private static bool HasLikelyMpegTsSync(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0x47)
                continue;

            if (i + MpegTsBoundaryScanner.PacketSize >= data.Length
                || data[i + MpegTsBoundaryScanner.PacketSize] == 0x47)
                return true;
        }

        return false;
    }

    private void MarkMpegTsSafeStartIfReady(BufferLease lease, MpegTsStartupKind kind)
    {
        var fallbackBytes = Math.Min(Math.Max(MpegTsBoundaryScanner.PacketSize, _bufferOptions.MaxBytesPerSession / 2), 512 * 1024);

        if (kind == MpegTsStartupKind.PatPmt && _mpegTsCandidateSafeStartSequence is null)
        {
            _mpegTsCandidateSafeStartGeneration = lease.Generation;
            _mpegTsCandidateSafeStartSequence = lease.Sequence;
        }

        var selected = kind is MpegTsStartupKind.H264Idr or MpegTsStartupKind.PatPmt;
        var fallback = false;
        if (!selected && !_mpegTsSafeStartSelected && Interlocked.Read(ref _mpegTsBytesSinceReset) >= fallbackBytes)
        {
            selected = true;
            fallback = true;
        }

        if (!selected)
            return;

        if (kind == MpegTsStartupKind.H264Idr
            && _mpegTsCandidateSafeStartGeneration is { } generation
            && _mpegTsCandidateSafeStartSequence is { } sequence)
            _buffer.MarkSafeStart(generation, sequence);
        else
            _buffer.MarkSafeStart(lease);

        if (!_mpegTsSafeStartSelected)
        {
            _mpegTsSafeStartSelected = true;
            RecordDiagnostic(
                StreamDiagnosticEventKind.MpegTsSafeStartSelected,
                message: $"MPEG-TS safe start selected: {(fallback ? "FallbackPacketBoundary" : kind)}.");
        }
    }

    private void SetState(SessionState state)
    {
        _state = state;
    }

    private bool IsFatal(UpstreamFailureKind kind)
        => kind is UpstreamFailureKind.UpstreamAuth
            or UpstreamFailureKind.UpstreamNotFound
            or UpstreamFailureKind.StartupFatal;

    private static bool ShouldCooldownImmediately(UpstreamFailureKind kind)
        => kind is UpstreamFailureKind.UpstreamProxyAuthRequired
            or UpstreamFailureKind.UpstreamRateLimited;

    private TimeSpan ResolveCooldownDuration(Exception ex)
    {
        if (ex is UpstreamConnectException { RetryAfter: { } retryAfter })
        {
            if (retryAfter <= TimeSpan.Zero)
                return TimeSpan.FromSeconds(1);

            return _reconnectOptions.StrikeCooldown > retryAfter
                ? _reconnectOptions.StrikeCooldown
                : retryAfter;
        }

        return _reconnectOptions.StrikeCooldown > TimeSpan.Zero
            ? _reconnectOptions.StrikeCooldown
            : TimeSpan.FromSeconds(1);
    }

    private TimeSpan GetReconnectDelay(int attempt)
    {
        if (_reconnectOptions.FixedStepBackoffSeconds.Length == 0)
            return TimeSpan.Zero;

        var index = Math.Clamp(attempt - 1, 0, _reconnectOptions.FixedStepBackoffSeconds.Length - 1);
        var seconds = Math.Max(0, _reconnectOptions.FixedStepBackoffSeconds[index]);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task ForceCloseSubscribersAsync()
    {
        foreach (var subscriber in _subscribers.Values)
        {
            await subscriber.CompleteAsync(SubscriberDisconnectReason.SessionClosed);
        }
    }

    private void BeginSubscriberAttach()
    {
        lock (_gate)
        {
            _pendingSubscriberAttaches++;
            CancelIdleShutdownNoLock();
        }
    }

    private void EndSubscriberAttach()
    {
        lock (_gate)
        {
            if (_pendingSubscriberAttaches > 0)
                _pendingSubscriberAttaches--;

            if (ShouldScheduleIdleShutdownNoLock())
                ScheduleIdleShutdownNoLock();
        }
    }

    private void ScheduleIdleShutdown()
    {
        lock (_gate)
        {
            if (ShouldScheduleIdleShutdownNoLock())
                ScheduleIdleShutdownNoLock();
        }
    }

    private void ScheduleIdleShutdownNoLock()
    {
        if (!ShouldScheduleIdleShutdownNoLock())
            return;

        if (_idleGrace <= TimeSpan.Zero)
        {
            _pendingStopTrigger = "idle_grace";
            LogStopTrigger("idle_grace");
            _ = StopSafelyAsync("idle_grace");
            return;
        }

        _pendingStopTrigger = "idle_grace";
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _idleCts = new CancellationTokenSource();
        var idleCts = _idleCts;
        var token = idleCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                LogStopTrigger("idle_grace", delay: _idleGrace);
                await Task.Delay(_idleGrace, token);

                lock (_gate)
                {
                    if (!ReferenceEquals(_idleCts, idleCts) || !ShouldScheduleIdleShutdownNoLock())
                        return;
                }

                LogStopTrigger("idle_grace");
                await StopSafelyAsync("idle_grace");
            }
            catch (OperationCanceledException)
            {
                // no-op
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Idle shutdown task failed unexpectedly for session {SessionId}.", _sessionId);
            }
        }, token);
    }

    private async Task StopSafelyAsync(string stopTrigger)
    {
        try
        {
            await StopAsync(stopTrigger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop stream session {SessionId}.", _sessionId);
        }
    }

    private void CancelIdleShutdown()
    {
        lock (_gate)
        {
            CancelIdleShutdownNoLock();
        }
    }

    private void CancelIdleShutdownNoLock()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _idleCts = null;
        _lastIdleGraceRemoteIp = null;
        if (string.Equals(_pendingStopTrigger, "idle_grace", StringComparison.Ordinal))
            _pendingStopTrigger = null;
    }

    private bool ShouldScheduleIdleShutdownNoLock()
        => !_subscribers.Values.Any(s => !s.IsInternal)
            && _retainedExternalActivityCount == 0
            && _pendingSubscriberAttaches == 0
            && Volatile.Read(ref _stopRequested) == 0;

    private void ReleaseExternalActivityRetention()
    {
        var shouldPublish = false;

        lock (_gate)
        {
            if (_retainedExternalActivityCount > 0)
                _retainedExternalActivityCount--;

            if (ShouldScheduleIdleShutdownNoLock())
                ScheduleIdleShutdownNoLock();

            shouldPublish = Volatile.Read(ref _closeNotified) == 0;
        }

        if (shouldPublish)
            PublishSnapshots();
    }

    private static TimeSpan ResolveIdleGrace(StreamProxyOptions options)
    {
        var idleGrace = options.IdleGrace < TimeSpan.Zero ? TimeSpan.Zero : options.IdleGrace;
        if (options.IdleGraceHardCap > TimeSpan.Zero && idleGrace > options.IdleGraceHardCap)
            return options.IdleGraceHardCap;

        return idleGrace;
    }

    private void LogSubscriberAttached(SubscriberConnection subscriber)
    {
        _logger.LogInformation(
            "Subscriber attached: SessionId={SessionId} DisplayName={DisplayName} RequestedRoute={RequestedRoute} RequestPath={RequestPath} RouteClassification={RouteClassification} ClientId={ClientId} RemoteIp={RemoteIp} UserAgent={UserAgent} Classification={Classification} ExternalSubscriberCount={ExternalSubscriberCount} InternalSubscriberCount={InternalSubscriberCount} PendingAttachCount={PendingAttachCount}",
            _sessionId,
            _source.DisplayName,
            _source.RequestedRoute,
            subscriber.RequestPath,
            RouteClassification,
            subscriber.ClientId,
            subscriber.RemoteIp,
            subscriber.UserAgent,
            StreamLogClassification.ClassifySubscriber(subscriber.IsInternal),
            ExternalSubscriberCount,
            InternalSubscriberCount,
            Volatile.Read(ref _pendingSubscriberAttaches));
    }

    private void LogSubscriberRemoved(SubscriberConnection subscriber, SubscriberDisconnectReason reason)
    {
        var level = reason == SubscriberDisconnectReason.SlowClient ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(
            level,
            "Subscriber removed: SessionId={SessionId} DisplayName={DisplayName} RequestedRoute={RequestedRoute} RequestPath={RequestPath} RouteClassification={RouteClassification} ClientId={ClientId} RemoteIp={RemoteIp} UserAgent={UserAgent} Classification={Classification} DisconnectReason={DisconnectReason} BytesSent={BytesSent} ExternalSubscriberCount={ExternalSubscriberCount} InternalSubscriberCount={InternalSubscriberCount} PendingAttachCount={PendingAttachCount}",
            _sessionId,
            _source.DisplayName,
            _source.RequestedRoute,
            subscriber.RequestPath,
            RouteClassification,
            subscriber.ClientId,
            subscriber.RemoteIp,
            subscriber.UserAgent,
            StreamLogClassification.ClassifySubscriber(subscriber.IsInternal),
            reason,
            subscriber.BytesSent,
            ExternalSubscriberCount,
            InternalSubscriberCount,
            Volatile.Read(ref _pendingSubscriberAttaches));

        if (subscriber.HdhrDiagnostics is not null || string.Equals(RouteClassification, StreamLogClassification.HdhrRoute, StringComparison.Ordinal))
            LogHdhrDisconnectDiagnostics(subscriber, reason);
    }

    private void LogHdhrDisconnectDiagnostics(SubscriberConnection subscriber, SubscriberDisconnectReason reason)
    {
        var diagnostics = subscriber.HdhrDiagnostics;
        var now = DateTimeOffset.UtcNow;
        var timeSinceLastUpstreamByte = GetTimeSinceLastUpstreamByte(now);

        _logger.LogInformation(
            "HDHR disconnect diagnostics: SessionId={SessionId} DisplayName={DisplayName} RequestedRoute={RequestedRoute} RouteClassification={RouteClassification} TunerId={TunerId} ReservationId={ReservationId} StreamKey={StreamKey} VirtualPath={VirtualPath} RemoteIp={RemoteIp} DisconnectReason={DisconnectReason} BytesSent={BytesSent} LastUpstreamByteUtc={LastUpstreamByteUtc} UpstreamRecentlyActive={UpstreamRecentlyActive} TimeSinceLastUpstreamByteMs={TimeSinceLastUpstreamByteMs} ExternalSubscriberCount={ExternalSubscriberCount} InternalSubscriberCount={InternalSubscriberCount}",
            _sessionId,
            _source.DisplayName,
            _source.RequestedRoute,
            RouteClassification,
            diagnostics?.TunerId,
            diagnostics?.ReservationId,
            diagnostics?.StreamKey,
            diagnostics?.VirtualPath ?? subscriber.RequestPath,
            subscriber.RemoteIp,
            reason,
            subscriber.BytesSent,
            _lastUpstreamByteUtc,
            WasUpstreamRecentlyActive(now),
            timeSinceLastUpstreamByte?.TotalMilliseconds,
            ExternalSubscriberCount,
            InternalSubscriberCount);
    }

    private void LogUpstreamFailure(UpstreamFailureKind kind)
    {
        if (kind != UpstreamFailureKind.EndOfStream)
            return;

        var now = DateTimeOffset.UtcNow;
        var timeSinceLastUpstreamByte = GetTimeSinceLastUpstreamByte(now);
        string? pendingStopTrigger;

        lock (_gate)
        {
            pendingStopTrigger = GetPendingStopTriggerNoLock();
        }

        _logger.LogWarning(
            "Upstream EOF context: SessionId={SessionId} DisplayName={DisplayName} RequestedRoute={RequestedRoute} RouteClassification={RouteClassification} ExternalSubscriberCount={ExternalSubscriberCount} InternalSubscriberCount={InternalSubscriberCount} TimeSinceLastUpstreamByteMs={TimeSinceLastUpstreamByteMs} TotalBytesRelayed={TotalBytesRelayed} PendingStopTrigger={PendingStopTrigger}",
            _sessionId,
            _source.DisplayName,
            _source.RequestedRoute,
            RouteClassification,
            ExternalSubscriberCount,
            InternalSubscriberCount,
            timeSinceLastUpstreamByte?.TotalMilliseconds,
            Interlocked.Read(ref _totalBytesRelayed),
            pendingStopTrigger);
    }

    private void LogStopTrigger(string stopTrigger, string? subscriberDisconnectReason = null, TimeSpan? delay = null)
    {
        _lastStopTrigger = stopTrigger;
        RecordDiagnostic(
            StreamDiagnosticEventKind.StopTriggered,
            stopTrigger: stopTrigger,
            reconnectDelay: delay,
            message: "Stream stop trigger recorded.");
        _logger.LogInformation(
            "Stream stop trigger: SessionId={SessionId} DisplayName={DisplayName} RequestedRoute={RequestedRoute} RouteClassification={RouteClassification} ExternalSubscriberCount={ExternalSubscriberCount} InternalSubscriberCount={InternalSubscriberCount} PendingAttachCount={PendingAttachCount} StopTrigger={StopTrigger} SubscriberDisconnectReason={SubscriberDisconnectReason} DelayMs={DelayMs}",
            _sessionId,
            _source.DisplayName,
            _source.RequestedRoute,
            RouteClassification,
            ExternalSubscriberCount,
            InternalSubscriberCount,
            Volatile.Read(ref _pendingSubscriberAttaches),
            stopTrigger,
            subscriberDisconnectReason,
            delay?.TotalMilliseconds);
    }

    private void MarkPendingStopTrigger(string stopTrigger)
    {
        lock (_gate)
        {
            _pendingStopTrigger = stopTrigger;
        }
    }

    private string? GetPendingStopTriggerNoLock()
    {
        if (!string.IsNullOrWhiteSpace(_pendingStopTrigger))
            return _pendingStopTrigger;

        if (_idleCts is not null)
            return "idle_grace";

        return Volatile.Read(ref _stopRequested) == 1
            ? "session_closed"
            : null;
    }

    private TimeSpan? GetTimeSinceLastUpstreamByte(DateTimeOffset now)
        => _lastUpstreamByteUtc is { } last ? now - last : null;

    private bool WasUpstreamRecentlyActive(DateTimeOffset now)
        => GetTimeSinceLastUpstreamByte(now) is { } age
           && age <= _reconnectOptions.ReadStallTimeout;

    private static string ResolveDisconnectStopTrigger(SubscriberDisconnectReason reason)
        => reason switch
        {
            SubscriberDisconnectReason.Retuned => "retune",
            SubscriberDisconnectReason.SessionClosed => "session_closed",
            _ => "client_disconnect",
        };

    private void PublishSnapshots()
    {
        var externalCount = ExternalSubscriberCount;
        var session = new StreamSessionSnapshot(
            SessionId: _sessionId,
            ProviderId: _source.ProviderId,
            ProviderChannelId: _source.ProviderChannelId,
            DisplayName: _source.DisplayName,
            State: _state,
            SubscriberCount: externalCount,
            IsShared: externalCount > 1,
            BufferUsedBytes: _buffer.UsedBytes,
            BufferMaxBytes: _buffer.MaxBytes,
            StartedUtc: _startedUtc,
            LastUpstreamByteUtc: _lastUpstreamByteUtc,
            ReconnectAttempts: _reconnectAttempts,
            LastFailureKind: _lastFailureKind,
            FirstByteLatencyMs: _lastFirstByteLatencyMs,
            BytesSinceReconnect: Interlocked.Read(ref _bytesSinceReconnect),
            LastDisconnectReason: _lastDisconnectReason,
            LastStopTrigger: _lastStopTrigger,
            LastUpstreamStatusCode: _lastUpstreamStatusCode,
            LastCooldownSeconds: _lastCooldownSeconds);

        _registry.UpsertSession(session);
        _registry.UpsertProvider(new StreamProviderSnapshot(
            SessionId: _sessionId,
            ProviderId: _source.ProviderId,
            ProviderChannelId: _source.ProviderChannelId,
            State: _state,
            LastUpstreamByteUtc: _lastUpstreamByteUtc,
            ReconnectAttempts: _reconnectAttempts,
            LastFailureKind: _lastFailureKind,
            ContentType: _contentType,
            FirstByteLatencyMs: _lastFirstByteLatencyMs,
            BytesSinceReconnect: Interlocked.Read(ref _bytesSinceReconnect),
            LastUpstreamStatusCode: _lastUpstreamStatusCode,
            LastCooldownSeconds: _lastCooldownSeconds));
    }

    private async Task NotifyClosedAsync()
    {
        if (Interlocked.Exchange(ref _closeNotified, 1) == 1)
            return;

        _logger.LogInformation(
            "Shared stream session {SessionId} closed with state {State}.",
            _sessionId,
            _state);
        RecordDiagnostic(
            StreamDiagnosticEventKind.SessionClosed,
            stopTrigger: _lastStopTrigger,
            message: "Shared stream session closed.");
        await _onClosed(Key, this);
    }

    private void RecordDiagnostic(
        StreamDiagnosticEventKind kind,
        SubscriberConnection? subscriber = null,
        UpstreamFailureKind? upstreamFailureKind = null,
        int? httpStatusCode = null,
        int? reconnectAttempt = null,
        TimeSpan? reconnectDelay = null,
        double? firstByteLatencyMs = null,
        SubscriberDisconnectReason? disconnectReason = null,
        string? stopTrigger = null,
        double? cooldownSeconds = null,
        int? retryAfterSeconds = null,
        string? message = null)
    {
        _diagnosticsStore.Record(new StreamDiagnosticEvent(
            EventId: Guid.NewGuid().ToString("N"),
            TimestampUtc: DateTimeOffset.UtcNow,
            Kind: kind,
            SessionId: _sessionId,
            ProviderId: _source.ProviderId,
            ProviderChannelId: _source.ProviderChannelId,
            DisplayName: _source.DisplayName,
            RequestedRoute: _source.RequestedRoute,
            RouteClassification: RouteClassification,
            ClientId: subscriber?.ClientId,
            RemoteIp: subscriber?.RemoteIp,
            UserAgent: subscriber?.UserAgent,
            HttpStatusCode: httpStatusCode,
            UpstreamFailureKind: upstreamFailureKind,
            ReconnectAttempt: reconnectAttempt,
            ReconnectDelayMs: reconnectDelay?.TotalMilliseconds,
            FirstByteLatencyMs: firstByteLatencyMs,
            BytesSinceReconnect: Interlocked.Read(ref _bytesSinceReconnect),
            TotalBytesRelayed: Interlocked.Read(ref _totalBytesRelayed),
            DisconnectReason: disconnectReason,
            StopTrigger: stopTrigger,
            CooldownSeconds: cooldownSeconds,
            RetryAfterSeconds: retryAfterSeconds,
            Message: message));
    }

    private string RouteClassification => StreamLogClassification.ClassifyRoute(_source.RequestedRoute);

    private sealed class ExternalActivityRetention(ChannelStreamSession session) : IDisposable
    {
        private ChannelStreamSession? _session = session;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _session, null);
            current?.ReleaseExternalActivityRetention();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
