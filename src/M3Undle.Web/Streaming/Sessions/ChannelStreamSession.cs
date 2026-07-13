using System.Collections.Concurrent;
using M3Undle.Core.M3u;
using M3Undle.Core.MpegTs;
using M3Undle.Web.Application;
using M3Undle.Web.Observability;
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
    private readonly IStreamChannelHealthEventRecorder _healthEventRecorder;
    private readonly IStreamChannelHealthProfileService _healthProfileService;
    private readonly IEventService _eventService;
    private readonly M3UndleMetrics? _metrics;
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
    private static readonly TimeSpan MpegTsKeepaliveInterval = TimeSpan.FromMilliseconds(500);
    private static readonly byte[] MpegTsNullPacket = CreateMpegTsNullPacket();

    private StreamSourceDescriptor _source;
    private SessionState _state = SessionState.Initializing;
    private Task? _runTask;
    private long _lastPublishTick;
    private string? _contentType;
    private string? _cacheControl;
    private string _relayMode = UpstreamRelayModes.Direct;
    private string? _lastRelayFallbackReason;
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
    private bool _recoveryOutputHoldActive;
    private DateTimeOffset? _recoveryOutputHoldStartedUtc;
    private long _recoveryBytesSuppressed;
    private StreamChannelRecoveryPolicy? _currentRecoveryPolicy;
    private string? _relayPolicy;
    private string? _relayDecisionReason;
    private string? _lastSafeStartKind;
    private double? _lastRecoveryOutputHeldMs;
    private DateTimeOffset? _lastRecoveryStartedUtc;
    private long? _lastRelayedVideoDts90k;
    private long? _recoveryTrimTargetDts90k;
    private bool _recoveryTrimEvaluated;
    private bool _recoveryTrimActive;
    private long _recoveryTrimmedBytes;
    private string _lastRecoveryTrimOutcome = RecoveryTrimOutcomes.NotApplicable;
    private double? _lastRecoveryTrimRewindSeconds;
    private DateTimeOffset? _lastRecoveryTrimAbandonedUtc;
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
        IStreamChannelHealthEventRecorder healthEventRecorder,
        IStreamChannelHealthProfileService healthProfileService,
        IEventService eventService,
        ILogger<ChannelStreamSession> logger,
        Func<ChannelSessionKey, ChannelStreamSession, Task> onClosed,
        M3UndleMetrics? metrics = null)
    {
        _source = source;
        _bufferOptions = bufferOptions;
        _proxyOptions = proxyOptions;
        _reconnectOptions = reconnectOptions;
        _upstreamConnector = upstreamConnector;
        _strikeStore = strikeStore;
        _registry = registry;
        _diagnosticsStore = diagnosticsStore;
        _healthEventRecorder = healthEventRecorder;
        _healthProfileService = healthProfileService;
        _eventService = eventService;
        _logger = logger;
        _onClosed = onClosed;
        _metrics = metrics;
        _idleGrace = ResolveIdleGrace(_proxyOptions);

        var maxBytes = Math.Clamp(_bufferOptions.MaxBytesPerSession, 1, _bufferOptions.MaxBytesHardCap);
        _buffer = new RingBuffer(maxBytes);
        RecordDiagnostic(StreamDiagnosticEventKind.SessionCreated, message: "Shared stream session created.");
        _metrics?.RecordStreamStarted(_source.ProviderId, ResolveStreamType(_source.StreamUrl), ResolveProtocol(_source.StreamUrl));
    }

    public ChannelSessionKey Key => _source.SessionKey;

    public string SessionId => _sessionId;

    public SessionState State => _state;

    public int SubscriberCount => _subscribers.Count;

    public int ExternalSubscriberCount => _subscribers.Values.Count(s => !s.IsInternal);

    public int InternalSubscriberCount => _subscribers.Values.Count(s => s.IsInternal);

    public bool IsAcceptingSubscribers
        => Volatile.Read(ref _stopRequested) == 0
           && Volatile.Read(ref _closeNotified) == 0
           && !_sessionCts.IsCancellationRequested;

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

    public bool IsIdleGraceWithNoConsumers
    {
        get
        {
            lock (_gate)
            {
                return string.Equals(_pendingStopTrigger, "idle_grace", StringComparison.Ordinal)
                    && ShouldScheduleIdleShutdownNoLock();
            }
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
            if (!IsAcceptingSubscribers)
                throw new OperationCanceledException(_sessionCts.Token);

            EnsureStarted();
            await _headersReadyTcs.Task.WaitAsync(requestCt);

            if (!IsAcceptingSubscribers)
                throw new OperationCanceledException(_sessionCts.Token);

            var subscriber = new SubscriberConnection(
                sessionId: _sessionId,
                requestedRoute: _source.RequestedRoute,
                context: context,
                queueCapacity: _bufferOptions.SubscriberQueueCapacity,
                writeStallTimeout: _bufferOptions.WriteStallTimeout,
                onCompleted: (s, reason) => RemoveSubscriberAsync(s, reason),
                isInternal: isInternal);

            _subscribers[subscriber.ClientId] = subscriber;

            subscriber.InitializeResponse(_contentType, _cacheControl);
            _ = subscriber.StartAsync(CreateSubscriberStartupSnapshot(subscriber.IsInternal), _sessionCts.Token);
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
                    if (_currentRecoveryPolicy is null || reconnectAttempt == 0)
                    {
                        _currentRecoveryPolicy = await _healthProfileService.GetRecoveryPolicyAsync(
                            _source.ProviderId,
                            _source.ProviderChannelId,
                            _reconnectOptions,
                            _sessionCts.Token);
                        PublishSnapshots();
                    }

                    await using var upstream = await _upstreamConnector.ConnectAsync(_source, _currentRecoveryPolicy, _sessionCts.Token);
                    _lastUpstreamStatusCode = upstream.StatusCode;
                    _contentType = upstream.ContentType;
                    _cacheControl = upstream.Response?.Headers.CacheControl?.ToString();
                    _relayMode = upstream.RelayMode;
                    _lastRelayFallbackReason = upstream.RelayFallbackReason;
                    _relayPolicy = upstream.RelayPolicy;
                    _relayDecisionReason = upstream.RelayDecisionReason;
                    _headersReadyTcs.TrySetResult(true);
                    Interlocked.Exchange(ref _bytesSinceReconnect, 0);
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.UpstreamConnected,
                        httpStatusCode: upstream.StatusCode,
                        reconnectAttempt: reconnectAttempt,
                        message: "Connected to upstream stream.");
                    if (!string.Equals(_relayMode, UpstreamRelayModes.Direct, StringComparison.Ordinal))
                    {
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.FfmpegRelayStarted,
                            httpStatusCode: upstream.StatusCode,
                            reconnectAttempt: reconnectAttempt,
                            message: $"FFmpeg relay started: {_relayMode}.");
                    }
                    else if (!string.IsNullOrWhiteSpace(_lastRelayFallbackReason))
                    {
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.FfmpegRelayFallbackToDirect,
                            httpStatusCode: upstream.StatusCode,
                            reconnectAttempt: reconnectAttempt,
                            message: $"FFmpeg relay fallback to direct: {_lastRelayFallbackReason}.");
                        await PublishUnstableProviderEventAsync(
                            $"FFmpeg relay fell back to direct streaming ({_lastRelayFallbackReason}).");
                    }

                    var recoveredAttempt = reconnectAttempt;
                    var shouldHoldRecoveredOutput = recoveredAttempt > 0 && IsMpegTsRelay();
                    if (recoveredAttempt > 0)
                    {
                        _metrics?.RecordStreamReconnect(_source.ProviderId);
                        _logger.LogInformation(
                            "Stream '{DisplayName}' recovered successfully after {Attempts} reconnect attempt(s).",
                            _source.DisplayName,
                            recoveredAttempt);
                        _buffer.ResetGeneration();
                        ResetMpegTsBoundaryState();
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.ReconnectRecovered,
                            httpStatusCode: upstream.StatusCode,
                            reconnectAttempt: recoveredAttempt,
                            message: "Upstream stream recovered after reconnect.");
                        if (shouldHoldRecoveredOutput)
                        {
                            _currentRecoveryPolicy = await _healthProfileService.GetRecoveryPolicyAsync(
                                _source.ProviderId,
                                _source.ProviderChannelId,
                                _reconnectOptions,
                                _sessionCts.Token);
                            BeginRecoveryOutputHold(recoveredAttempt);
                        }

                        await PublishRecoveredProviderEventIfNeededAsync(CancellationToken.None);
                    }
                    else
                    {
                        ResetMpegTsBoundaryState();
                        _currentRecoveryPolicy = await _healthProfileService.GetRecoveryPolicyAsync(
                            _source.ProviderId,
                            _source.ProviderChannelId,
                            _reconnectOptions,
                            _sessionCts.Token);
                    }

                    reconnectAttempt = 0;
                    outageStartedUtc = null;
                    if (!_recoveryOutputHoldActive)
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
                    _metrics?.RecordStreamFailure(_source.ProviderId);
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
                        var retryAfter = ResolveCooldownDuration(kind, ex);
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
                        await PublishUnstableProviderEventAsync(
                            $"Stream entered cooldown after {kind} ({Math.Ceiling(retryAfter.TotalSeconds):F0}s).");
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

                        var fatalStopTrigger = GetPendingStopTrigger() == "recovery_failed_unsafe"
                            ? "recovery_failed_unsafe"
                            : "upstream_fault";
                        MarkPendingStopTrigger(fatalStopTrigger);
                        LogStopTrigger(fatalStopTrigger, subscriberDisconnectReason: kind.ToString());
                        SetState(SessionState.Faulted);
                        await ForceCloseSubscribersAsync();
                        break;
                    }

                    outageStartedUtc ??= DateTimeOffset.UtcNow;
                    var outageDuration = DateTimeOffset.UtcNow - outageStartedUtc.Value;
                    if (outageDuration >= _reconnectOptions.OutageWindow)
                    {
                        var cooldown = ResolveCooldownDuration(kind, ex);
                        _lastCooldownSeconds = cooldown.TotalSeconds;
                        _strikeStore.RecordStrike(Key, cooldown);
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.CooldownRecorded,
                            upstreamFailureKind: kind,
                            httpStatusCode: _lastUpstreamStatusCode,
                            cooldownSeconds: cooldown.TotalSeconds,
                            retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(Math.Min(30, cooldown.TotalSeconds))),
                            message: "Upstream cooldown recorded after outage window exhaustion.");
                        _headersReadyTcs.TrySetException(new TimeoutException("Reconnect outage window exhausted."));
                        MarkPendingStopTrigger("upstream_fault");
                        LogStopTrigger("upstream_fault", subscriberDisconnectReason: "outage_window_exhausted");
                        SetState(SessionState.Faulted);
                        await ForceCloseSubscribersAsync();
                        _logger.LogWarning(
                            "Session {SessionId} outage window exhausted; entering cooldown for {ProviderId}/{ProviderChannelId}. kind={FailureKind} cooldownSeconds={CooldownSeconds:F0}.",
                            _sessionId,
                            _source.ProviderId,
                            _source.ProviderChannelId,
                            kind,
                            cooldown.TotalSeconds);
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

            _metrics?.RecordStreamEnded(_source.ProviderId, ResolveStreamType(_source.StreamUrl), ResolveProtocol(_source.StreamUrl));
            PublishSnapshots();
            _registry.RemoveSession(_sessionId);
            await NotifyClosedAsync();
        }
    }

    private async Task ReadFromUpstreamAsync(UpstreamConnection upstream, CancellationToken ct)
    {
        var readBuffer = new byte[Math.Max(188, _bufferOptions.ReadChunkSizeBytes)];
        Task<int>? pendingReadTask = null;
        var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stallCts.CancelAfter(ResolveCurrentStallTimeout());

        try
        {
            while (!ct.IsCancellationRequested)
            {
                pendingReadTask ??= upstream.Stream
                    .ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), stallCts.Token)
                    .AsTask();

                var completed = await Task.WhenAny(pendingReadTask, Task.Delay(MpegTsKeepaliveInterval, ct));
                if (!ReferenceEquals(completed, pendingReadTask))
                {
                    ct.ThrowIfCancellationRequested();

                    if (ShouldInjectMpegTsKeepalive())
                        await PublishMpegTsKeepaliveAsync();

                    continue;
                }

                int bytesRead;
                try
                {
                    bytesRead = await pendingReadTask;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new UpstreamConnectException("Upstream read stalled.", UpstreamFailureKind.TimeoutOrStall, null, ex);
                }
                finally
                {
                    pendingReadTask = null;
                }

                if (bytesRead == 0)
                {
                    throw new UpstreamConnectException("Upstream EOF.", UpstreamFailureKind.EndOfStream);
                }

                var resetStallTimer = await PublishUpstreamBytesAsync(readBuffer.AsMemory(0, bytesRead));
                if (resetStallTimer)
                {
                    stallCts.Dispose();
                    stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stallCts.CancelAfter(ResolveCurrentStallTimeout());
                }
            }
        }
        finally
        {
            stallCts.Dispose();
        }
    }

    private async Task<bool> PublishUpstreamBytesAsync(ReadOnlyMemory<byte> data)
    {
        var bytesRead = data.Length;
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

        var resetStallTimer = true;
        if (IsMpegTsRelay() && ShouldPacketizeMpegTs(data.Span))
        {
            var batch = _mpegTsScanner.Process(data.Span);
            if (batch is null)
                return false;

            if (batch.DroppedByteCount > 0 || batch.SyncLost)
            {
                RecordDiagnostic(
                    StreamDiagnosticEventKind.MpegTsSyncLost,
                    message: $"MPEG-TS sync rescan dropped {batch.DroppedByteCount} byte(s).");
            }

            if (batch.Data.Length == 0)
                return false;

            resetStallTimer = HasNonNullTsContent(batch.Data);
            Interlocked.Add(ref _mpegTsBytesSinceReset, batch.Data.Length);
            using var published = _buffer.Write(batch.Data);
            var suppressForOverlapTrim = ShouldSuppressSafeStartForOverlapTrim(batch);
            var safeStart = MarkMpegTsSafeStartIfReady(
                published,
                batch.StartupKind,
                batch.Data.Length,
                batch.HasKnownH264VideoStream,
                suppressForOverlapTrim);
            if (_recoveryOutputHoldActive)
            {
                _recoveryBytesSuppressed += batch.Data.Length;
                if (safeStart.Selected)
                {
                    await ResumeRecoveryOutputAsync(safeStart.Kind);
                    UpdateLastRelayedVideoDts(batch);
                }
                else if (IsRecoveryHoldLimitExceeded())
                {
                    await FailRecoveryOutputAsync();
                }

                return resetStallTimer;
            }

            await PublishToSubscribersAsync(published, isAtSafeBoundary: safeStart.Selected);
            UpdateLastRelayedVideoDts(batch);
        }
        else
        {
            if (_recoveryOutputHoldActive)
                await FailRecoveryOutputAsync();

            using var published = _buffer.Write(data);
            await PublishToSubscribersAsync(published);
        }

        PublishSnapshotsIfDue();
        return resetStallTimer;
    }

    private async Task PublishToSubscribersAsync(BufferLease published, bool isAtSafeBoundary = false)
    {
        List<SubscriberConnection>? slowSubscribers = null;

        foreach (var subscriber in _subscribers.Values)
        {
            // A subscriber in resync-pending state had its queue overflow; it is waiting
            // for the queue to drain and a clean TS boundary to arrive so delivery can
            // resume from a correct splice point (no mid-PES byte drops).
            if (subscriber.IsResyncPending)
            {
                if (isAtSafeBoundary && subscriber.QueueDepth <= subscriber.LowWaterMark)
                {
                    // Queue has drained enough and a clean IDR boundary arrived — resume.
                    subscriber.ClearResyncPending();
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.SubscriberQueueFull,
                        subscriber: subscriber,
                        queueDepth: subscriber.QueueDepth,
                        message: "Subscriber resynced at clean TS boundary; resuming delivery.");
                    // Fall through to normal enqueue below.
                }
                else if (subscriber.RegisterResyncDrainProgress() || subscriber.QueueDepth == 0)
                {
                    // The queue drained since the last tick, OR it has reached zero — meaning
                    // the pump consumed all buffered items and is simply waiting for the next
                    // IDR boundary we have yet to produce. In resync mode no new items are
                    // enqueued, so a stable depth > 0 means the pump is stuck (accumulate
                    // grace), but a stable depth = 0 means the client is healthy (reset grace).
                    subscriber.ResetSlowClientGrace();
                    continue;
                }
                else
                {
                    // No drain progress since the last tick — the client is genuinely stuck.
                    // Advance the grace timer as if each skipped chunk were an overflow; if the
                    // stall outlasts the grace period the subscriber is evicted as a slow client.
                    var resyncOverflow = subscriber.RegisterQueueOverflow(_bufferOptions.SlowClientGracePeriod);
                    if (resyncOverflow == SubscriberQueueOverflowResult.GraceExceeded)
                    {
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.SubscriberQueueFull,
                            subscriber: subscriber,
                            disconnectReason: SubscriberDisconnectReason.SlowClient,
                            queueDepth: Math.Max(1, subscriber.QueueDepth),
                            message: "Subscriber resync stalled beyond slow-client grace; removing as slow client.");
                        slowSubscribers ??= [];
                        slowSubscribers.Add(subscriber);
                    }
                    continue;
                }
            }

            var perSubscriber = published.Duplicate();
            if (!subscriber.TryEnqueue(perSubscriber))
            {
                perSubscriber.Dispose();
                if (subscriber.IsCompleted)
                    continue;

                // Queue is full — enter resync mode instead of dropping arbitrary bytes.
                // Dropping bytes mid-stream corrupts MPEG-TS continuity and PES framing;
                // resync waits for the next clean IDR boundary so the client can recover
                // from a valid decode point. The grace period caps how long we wait before
                // giving up and evicting a subscriber that never drains.
                var overflow = subscriber.RegisterQueueOverflow(_bufferOptions.SlowClientGracePeriod);
                if (overflow == SubscriberQueueOverflowResult.GraceExceeded)
                {
                    RecordDiagnostic(
                        StreamDiagnosticEventKind.SubscriberQueueFull,
                        subscriber: subscriber,
                        disconnectReason: SubscriberDisconnectReason.SlowClient,
                        queueDepth: Math.Max(1, subscriber.QueueDepth),
                        message: "Subscriber queue stayed full beyond slow-client grace; removing subscriber as slow client.");
                    slowSubscribers ??= [];
                    slowSubscribers.Add(subscriber);
                }
                else
                {
                    if (overflow == SubscriberQueueOverflowResult.EnteredGrace)
                    {
                        subscriber.SetResyncPending();
                        RecordDiagnostic(
                            StreamDiagnosticEventKind.SubscriberQueueFull,
                            subscriber: subscriber,
                            queueDepth: Math.Max(1, subscriber.QueueDepth),
                            message: "Subscriber queue full; entering resync mode — waiting for next clean TS boundary.");
                    }
                }
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

    private BufferSnapshot CreateSubscriberStartupSnapshot(bool isInternal)
    {
        if (_recoveryOutputHoldActive)
            return _buffer.CreateLiveEdgeSnapshot();

        if (IsMpegTsRelay())
        {
            // Start all TS subscribers (internal HLS relay and external direct) at the most
            // recent PAT/PMT + IDR boundary so they start 2–5 s behind live at a clean
            // decode point rather than dumping the entire ring buffer. Falls back to the
            // full snapshot before the first IDR has been indexed (early seconds of a new
            // stream) so the client still gets initial data during that window.
            var safeStart = _buffer.CreateSafeStartSnapshot();
            return safeStart.Chunks.Count > 0 ? safeStart : _buffer.CreateSnapshot();
        }

        return _buffer.CreateLiveEdgeSnapshot();
    }

    private bool IsMpegTsRelay()
        => _contentType?.Contains("MP2T", StringComparison.OrdinalIgnoreCase) == true
           || _contentType?.Contains("mpegts", StringComparison.OrdinalIgnoreCase) == true;

    private bool ShouldInjectMpegTsKeepalive()
        => IsMpegTsRelay() && !_recoveryOutputHoldActive && HasNonHdhrExternalSubscriber();

    private bool HasNonHdhrExternalSubscriber()
        => _subscribers.Values.Any(subscriber =>
            !subscriber.IsInternal
            && StreamLogClassification.ClassifyRoute(subscriber.RequestPath) != StreamLogClassification.HdhrRoute);

    private async Task PublishMpegTsKeepaliveAsync()
    {
        using var published = _buffer.Write(MpegTsNullPacket);
        await PublishToSubscribersAsync(published);
        PublishSnapshotsIfDue();
    }

    private void PublishSnapshotsIfDue()
    {
        var tick = Environment.TickCount64;
        if (tick - _lastPublishTick < 100)
            return;

        _lastPublishTick = tick;
        PublishSnapshots();
    }

    private TimeSpan ResolveCurrentStallTimeout()
    {
        if (!IsMpegTsRelay())
            return _reconnectOptions.ReadStallTimeout;

        // Issue #96: FFmpeg relay (clean remux / HLS->TS) reconnects to the provider
        // internally, so give it longer before M3Undle tears the process down and
        // reconnects the whole session — tearing it down too eagerly fights FFmpeg's
        // own recovery. Direct relay uses the short content-stall timeout so M3Undle
        // detects a stall and reconnects quickly, minimizing the on-screen freeze.
        var contentTimeout = IsFfmpegRelay()
            ? _reconnectOptions.FfmpegRelayStallTimeout
            : _reconnectOptions.ContentStallTimeout;

        if (contentTimeout <= TimeSpan.Zero)
            return _reconnectOptions.ReadStallTimeout;

        return contentTimeout < _reconnectOptions.ReadStallTimeout
            ? contentTimeout
            : _reconnectOptions.ReadStallTimeout;
    }

    private static bool HasNonNullTsContent(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset + MpegTsBoundaryScanner.PacketSize <= data.Length; offset += MpegTsBoundaryScanner.PacketSize)
        {
            if (data[offset] != 0x47)
                continue;

            var pid = ((data[offset + 1] & 0x1f) << 8) | data[offset + 2];
            if (pid != 0x1fff)
                return true;
        }

        return false;
    }

    private static byte[] CreateMpegTsNullPacket()
    {
        var packet = new byte[MpegTsBoundaryScanner.PacketSize];
        Array.Fill(packet, (byte)0xff);
        packet[0] = 0x47;
        packet[1] = 0x1f;
        packet[2] = 0xff;
        packet[3] = 0x10;
        return packet;
    }

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

    private SafeStartDecision MarkMpegTsSafeStartIfReady(
        BufferLease lease,
        MpegTsStartupKind kind,
        int batchLength,
        bool hasKnownH264VideoStream,
        bool suppressSelection = false)
    {
        var fallbackBytes = ResolveRecoverySafeStartSearchLimitBytes();

        if (kind == MpegTsStartupKind.PatPmt)
        {
            _mpegTsCandidateSafeStartGeneration = lease.Generation;
            _mpegTsCandidateSafeStartSequence = batchLength == MpegTsBoundaryScanner.PacketSize
                ? Math.Max(0, lease.Sequence - 1)
                : lease.Sequence;
        }

        // An active overlap trim keeps holding through replayed pre-failure content;
        // the PAT/PMT candidate above must keep rolling forward, but neither an IDR
        // from the replayed span nor the byte-count fallback may resume output.
        if (suppressSelection)
            return SafeStartDecision.NotSelected;

        var selected = kind == MpegTsStartupKind.H264Idr
            || (kind == MpegTsStartupKind.PatPmt && !hasKnownH264VideoStream);
        var fallback = false;
        if (!selected
            && ResolveRecoveryPolicy().AllowPacketBoundaryRecoveryFallback
            && !_mpegTsSafeStartSelected
            && Interlocked.Read(ref _mpegTsBytesSinceReset) >= fallbackBytes)
        {
            selected = true;
            fallback = true;
        }

        if (!selected)
            return SafeStartDecision.NotSelected;

        var safeStartKind = fallback ? "FallbackPacketBoundary" : kind.ToString();

        if (kind is MpegTsStartupKind.H264Idr or MpegTsStartupKind.PatPmt
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
                safeStartKind: safeStartKind,
                message: $"MPEG-TS safe start selected: {safeStartKind}.");
        }

        return new SafeStartDecision(true, safeStartKind);
    }

    private void BeginRecoveryOutputHold(int reconnectAttempt)
    {
        // A trim still active here means the upstream failed again before the replay
        // reached the pre-failure position — the batch-driven trim budgets never got a
        // chance to fire. Resolve it as abandoned so the outcome is recorded and the
        // retry cooldown below stops a flapping source from re-entering a fresh trim
        // (with a fresh budget) on every reconnect, which would suppress output forever.
        if (_recoveryTrimActive)
            AbandonRecoveryOverlapTrim("because the upstream failed again during the replayed span");

        var holdStartedUtc = DateTimeOffset.UtcNow;
        _recoveryOutputHoldActive = true;
        _recoveryOutputHoldStartedUtc = holdStartedUtc;
        _lastRecoveryStartedUtc = holdStartedUtc;
        _recoveryBytesSuppressed = 0;
        var trimOnRetryCooldown = _lastRecoveryTrimAbandonedUtc is { } lastAbandonedUtc
            && holdStartedUtc - lastAbandonedUtc < _reconnectOptions.RecoveryOverlapTrimRetryCooldown;
        _recoveryTrimTargetDts90k = _reconnectOptions.EnableRecoveryOverlapTrim && !trimOnRetryCooldown
            ? _lastRelayedVideoDts90k
            : null;
        if (trimOnRetryCooldown && _reconnectOptions.EnableRecoveryOverlapTrim && _lastRelayedVideoDts90k is not null)
        {
            _logger.LogInformation(
                "Recovery overlap trim on retry cooldown: SessionId={SessionId} DisplayName={DisplayName} a trim was abandoned {SecondsSinceAbandoned:F1}s ago; using the standard safe-start resume for this recovery.",
                _sessionId,
                _source.DisplayName,
                (holdStartedUtc - _lastRecoveryTrimAbandonedUtc!.Value).TotalSeconds);
        }
        _recoveryTrimEvaluated = false;
        _recoveryTrimActive = false;
        _recoveryTrimmedBytes = 0;
        _lastRecoveryTrimOutcome = _recoveryTrimTargetDts90k is null
            ? RecoveryTrimOutcomes.NotApplicable
            : RecoveryTrimOutcomes.None;
        _lastRecoveryTrimRewindSeconds = null;
        _currentRecoveryPolicy ??= StreamChannelRecoveryPolicy.FromOptions(_reconnectOptions);
        var policy = ResolveRecoveryPolicy();
        SetState(SessionState.HoldingOutput);
        RecordDiagnostic(
            StreamDiagnosticEventKind.RecoveryStarted,
            reconnectAttempt: reconnectAttempt,
            recoveryHoldLimit: policy.RecoveryOutputHoldLimit,
            message: "MPEG-TS recovery output hold started after reconnect.");
        RecordDiagnostic(
            StreamDiagnosticEventKind.RecoveryOutputHeld,
            reconnectAttempt: reconnectAttempt,
            recoveryHoldLimit: policy.RecoveryOutputHoldLimit,
            message: "Downstream output is held while recovered MPEG-TS is scanned for a safe start.");
        _logger.LogInformation(
            "Recovery hold started: SessionId={SessionId} DisplayName={DisplayName} ProviderId={ProviderId} ProviderChannelId={ProviderChannelId} RelayMode={RelayMode} ReconnectAttempt={ReconnectAttempt} HoldLimitMs={HoldLimitMs} SearchLimitBytes={SearchLimitBytes} HealthProfile={HealthProfile} PacketBoundaryFallbackAllowed={PacketBoundaryFallbackAllowed} PolicyReason={PolicyReason}",
            _sessionId,
            _source.DisplayName,
            _source.ProviderId,
            _source.ProviderChannelId,
            _relayMode,
            reconnectAttempt,
            policy.RecoveryOutputHoldLimit.TotalMilliseconds,
            ResolveRecoverySafeStartSearchLimitBytes(),
            policy.Profile,
            policy.AllowPacketBoundaryRecoveryFallback,
            policy.Reason);
        PublishSnapshots();
    }

    private async Task ResumeRecoveryOutputAsync(string safeStartKind)
    {
        if (!_recoveryOutputHoldActive)
            return;

        var heldDuration = GetRecoveryHoldDuration();
        var recoveryDuration = _lastRecoveryStartedUtc is { } recoveryStartedUtc
            ? DateTimeOffset.UtcNow - recoveryStartedUtc
            : (TimeSpan?)null;
        RecordDiagnostic(
            StreamDiagnosticEventKind.RecoverySafeStartFound,
            outputHeld: heldDuration,
            recoveryDuration: recoveryDuration,
            safeStartKind: safeStartKind,
            bytesSuppressed: _recoveryBytesSuppressed,
            recoveryHoldLimit: ResolveRecoveryPolicy().RecoveryOutputHoldLimit,
            message: "Recovered MPEG-TS safe start found.");

        var snapshot = _buffer.CreateSafeStartSnapshot();
        try
        {
            var firstChunk = true;
            foreach (var lease in snapshot.Chunks)
            {
                using (lease)
                {
                    // The snapshot starts at a verified safe boundary; mark the first chunk
                    // so that any subscriber in resync-pending state can resume here.
                    await PublishToSubscribersAsync(lease, isAtSafeBoundary: firstChunk);
                    firstChunk = false;
                }
            }
        }
        finally
        {
            foreach (var lease in snapshot.Chunks)
                lease.Dispose();
        }

        _recoveryOutputHoldActive = false;
        _recoveryOutputHoldStartedUtc = null;
        _lastSafeStartKind = safeStartKind;
        _lastRecoveryOutputHeldMs = heldDuration.TotalMilliseconds;
        SetState(SessionState.Live);
        RecordDiagnostic(
            StreamDiagnosticEventKind.RecoveryOutputResumed,
            outputHeld: heldDuration,
            recoveryDuration: recoveryDuration,
            safeStartKind: safeStartKind,
            bytesSuppressed: _recoveryBytesSuppressed,
            recoveryHoldLimit: ResolveRecoveryPolicy().RecoveryOutputHoldLimit,
            message: "Downstream output resumed from recovered MPEG-TS safe start.");
        _logger.LogInformation(
            "Recovery resumed: SessionId={SessionId} DisplayName={DisplayName} ProviderId={ProviderId} ProviderChannelId={ProviderChannelId} RelayMode={RelayMode} SafeStartKind={SafeStartKind} OutputHeldMs={OutputHeldMs} BytesSuppressed={BytesSuppressed} TrimOutcome={TrimOutcome} TrimRewindSeconds={TrimRewindSeconds} TrimmedBytes={TrimmedBytes}",
            _sessionId,
            _source.DisplayName,
            _source.ProviderId,
            _source.ProviderChannelId,
            _relayMode,
            safeStartKind,
            heldDuration.TotalMilliseconds,
            _recoveryBytesSuppressed,
            _lastRecoveryTrimOutcome,
            _lastRecoveryTrimRewindSeconds,
            _recoveryTrimmedBytes);
        PublishSnapshots();
    }

    private Task FailRecoveryOutputAsync()
    {
        var heldDuration = GetRecoveryHoldDuration();
        var recoveryDuration = _lastRecoveryStartedUtc is { } recoveryStartedUtc
            ? DateTimeOffset.UtcNow - recoveryStartedUtc
            : (TimeSpan?)null;
        var bytesSuppressed = _recoveryBytesSuppressed;
        var searchLimitExceeded = bytesSuppressed >= ResolveRecoverySafeStartSearchLimitBytes();
        RecordDiagnostic(
            searchLimitExceeded
                ? StreamDiagnosticEventKind.RecoveryFailedUnsafe
                : StreamDiagnosticEventKind.RecoveryHoldLimitExceeded,
            outputHeld: heldDuration,
            recoveryDuration: recoveryDuration,
            bytesSuppressed: bytesSuppressed,
            recoveryHoldLimit: ResolveRecoveryPolicy().RecoveryOutputHoldLimit,
            message: searchLimitExceeded
                ? "MPEG-TS recovery safe-start search limit was exceeded."
                : "MPEG-TS recovery output hold limit was exceeded.");
        _logger.LogWarning(
            "Recovery failed — forced retune: SessionId={SessionId} DisplayName={DisplayName} ProviderId={ProviderId} ProviderChannelId={ProviderChannelId} RelayMode={RelayMode} OutputHeldMs={OutputHeldMs} BytesSuppressed={BytesSuppressed} SearchLimitExceeded={SearchLimitExceeded} HealthProfile={HealthProfile} PacketBoundaryFallbackAllowed={PacketBoundaryFallbackAllowed} PolicyReason={PolicyReason}",
            _sessionId,
            _source.DisplayName,
            _source.ProviderId,
            _source.ProviderChannelId,
            _relayMode,
            heldDuration.TotalMilliseconds,
            bytesSuppressed,
            searchLimitExceeded,
            ResolveRecoveryPolicy().Profile,
            ResolveRecoveryPolicy().AllowPacketBoundaryRecoveryFallback,
            ResolveRecoveryPolicy().Reason);
        RecordDiagnostic(
            StreamDiagnosticEventKind.RecoveryForcedRetune,
            outputHeld: heldDuration,
            recoveryDuration: recoveryDuration,
            bytesSuppressed: bytesSuppressed,
            recoveryHoldLimit: ResolveRecoveryPolicy().RecoveryOutputHoldLimit,
            stopTrigger: "recovery_failed_unsafe",
            message: "Closing downstream subscribers because recovery could not resume safely.");
        MarkPendingStopTrigger("recovery_failed_unsafe");
        LogStopTrigger("recovery_failed_unsafe");
        SetState(SessionState.Faulted);
        _recoveryOutputHoldActive = false;
        _recoveryOutputHoldStartedUtc = null;

        throw new UpstreamConnectException(
            "Recovered MPEG-TS stream did not reach a safe restart point before the recovery hold limit.",
            UpstreamFailureKind.StartupFatal);
    }

    private bool IsRecoveryHoldLimitExceeded()
    {
        if (!_recoveryOutputHoldActive)
            return false;

        // An active overlap trim intentionally suppresses far more than the standard
        // safe-start budgets allow; it enforces its own limits and abandoning it resets
        // the standard budgets, so the forced-retune path must not fire here.
        if (_recoveryTrimActive)
            return false;

        return GetRecoveryHoldDuration() >= ResolveRecoveryPolicy().RecoveryOutputHoldLimit
            || _recoveryBytesSuppressed >= ResolveRecoverySafeStartSearchLimitBytes();
    }

    private TimeSpan GetRecoveryHoldDuration()
        => _recoveryOutputHoldStartedUtc is { } started
            ? DateTimeOffset.UtcNow - started
            : TimeSpan.Zero;

    private void UpdateLastRelayedVideoDts(MpegTsPacketBatch batch)
    {
        if (batch.LatestVideoDts90k is { } dts)
            _lastRelayedVideoDts90k = dts;
    }

    private bool ShouldSuppressSafeStartForOverlapTrim(MpegTsPacketBatch batch)
    {
        if (!_recoveryOutputHoldActive || _recoveryTrimTargetDts90k is not { } target)
            return false;

        if (!_recoveryTrimEvaluated)
        {
            if (!batch.HasKnownH264VideoStream || batch.LatestVideoDts90k is not { } firstDts)
                return false;

            _recoveryTrimEvaluated = true;
            var deltaSeconds = MpegTsTimestamp.DeltaSeconds(target, firstDts);
            if (deltaSeconds >= 0)
            {
                _lastRecoveryTrimOutcome = RecoveryTrimOutcomes.None;
                return false;
            }

            if (deltaSeconds < -_reconnectOptions.RecoveryOverlapTrimMaxRewindSeconds)
            {
                _lastRecoveryTrimOutcome = RecoveryTrimOutcomes.NotApplicable;
                _logger.LogInformation(
                    "Recovery overlap trim skipped: SessionId={SessionId} DisplayName={DisplayName} reconnected stream is {RewindSeconds:F1}s behind the pre-failure position, beyond the {MaxRewindSeconds}s trim window; treating as an unrelated timeline.",
                    _sessionId,
                    _source.DisplayName,
                    -deltaSeconds,
                    _reconnectOptions.RecoveryOverlapTrimMaxRewindSeconds);
                return false;
            }

            _recoveryTrimActive = true;
            _lastRecoveryTrimRewindSeconds = -deltaSeconds;
            _logger.LogInformation(
                "Recovery overlap detected: SessionId={SessionId} DisplayName={DisplayName} reconnected stream is {RewindSeconds:F1}s behind the pre-failure position; holding output through the replayed span.",
                _sessionId,
                _source.DisplayName,
                -deltaSeconds);
        }

        if (!_recoveryTrimActive)
            return false;

        if (GetRecoveryHoldDuration() >= _reconnectOptions.RecoveryOverlapTrimHoldLimit
            || _recoveryTrimmedBytes >= _reconnectOptions.RecoveryOverlapTrimMaxBytes)
        {
            AbandonRecoveryOverlapTrim("without reaching the pre-failure position within the trim budget");
            return false;
        }

        if (batch.IdrDts90k is { } idrDts && MpegTsTimestamp.Delta(target, idrDts) >= 0)
        {
            CompleteRecoveryOverlapTrim(target, idrDts);
            return false;
        }

        _recoveryTrimmedBytes += batch.Data.Length;
        return true;
    }

    private void CompleteRecoveryOverlapTrim(long targetDts90k, long resumeIdrDts90k)
    {
        _recoveryTrimActive = false;
        _lastRecoveryTrimOutcome = RecoveryTrimOutcomes.Trimmed;
        var overshootSeconds = MpegTsTimestamp.DeltaSeconds(targetDts90k, resumeIdrDts90k);
        RecordDiagnostic(
            StreamDiagnosticEventKind.RecoveryOverlapTrimmed,
            outputHeld: GetRecoveryHoldDuration(),
            bytesSuppressed: _recoveryTrimmedBytes,
            message: $"Recovery overlap trimmed: {_lastRecoveryTrimRewindSeconds:F1}s of replayed content ({_recoveryTrimmedBytes} byte(s)) suppressed; resuming at IDR {overshootSeconds:F1}s past the pre-failure position.");
        _logger.LogInformation(
            "Recovery overlap trimmed: SessionId={SessionId} DisplayName={DisplayName} RewindSeconds={RewindSeconds:F1} TrimmedBytes={TrimmedBytes} ResumeOvershootSeconds={ResumeOvershootSeconds:F1}",
            _sessionId,
            _source.DisplayName,
            _lastRecoveryTrimRewindSeconds,
            _recoveryTrimmedBytes,
            overshootSeconds);
    }

    private void AbandonRecoveryOverlapTrim(string reason)
    {
        _recoveryTrimActive = false;
        _lastRecoveryTrimOutcome = RecoveryTrimOutcomes.Abandoned;
        _lastRecoveryTrimAbandonedUtc = DateTimeOffset.UtcNow;
        var heldDuration = GetRecoveryHoldDuration();
        RecordDiagnostic(
            StreamDiagnosticEventKind.RecoveryOverlapTrimAbandoned,
            outputHeld: heldDuration,
            bytesSuppressed: _recoveryTrimmedBytes,
            message: $"Recovery overlap trim abandoned after {heldDuration.TotalMilliseconds:F0} ms / {_recoveryTrimmedBytes} byte(s) {reason}; falling back to the standard safe-start resume.");
        _logger.LogWarning(
            "Recovery overlap trim abandoned: SessionId={SessionId} DisplayName={DisplayName} RewindSeconds={RewindSeconds:F1} TrimmedBytes={TrimmedBytes} HeldMs={HeldMs:F0} Reason={Reason}",
            _sessionId,
            _source.DisplayName,
            _lastRecoveryTrimRewindSeconds,
            _recoveryTrimmedBytes,
            heldDuration.TotalMilliseconds,
            reason);

        // Restart the standard hold budgets so the fallback resume gets its normal
        // window instead of tripping the forced-retune limits the trim already spent.
        _recoveryOutputHoldStartedUtc = DateTimeOffset.UtcNow;
        _recoveryBytesSuppressed = 0;
        Interlocked.Exchange(ref _mpegTsBytesSinceReset, 0);
    }

    private int ResolveRecoverySafeStartSearchLimitBytes()
    {
        var policy = ResolveRecoveryPolicy();
        var configuredSearchLimit = Math.Max(
            MpegTsBoundaryScanner.PacketSize,
            policy.RecoverySafeStartSearchLimitBytes);
        var currentFallbackWindow = Math.Min(
            Math.Max(MpegTsBoundaryScanner.PacketSize, _bufferOptions.MaxBytesPerSession / 2),
            Math.Max(512 * 1024, configuredSearchLimit));
        return Math.Max(
            MpegTsBoundaryScanner.PacketSize,
            Math.Min(currentFallbackWindow, configuredSearchLimit));
    }

    private StreamChannelRecoveryPolicy ResolveRecoveryPolicy()
        => _currentRecoveryPolicy ?? StreamChannelRecoveryPolicy.FromOptions(_reconnectOptions);

    private bool IsFfmpegRelay()
        => !string.Equals(_relayMode, UpstreamRelayModes.Direct, StringComparison.Ordinal);

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

    private TimeSpan ResolveCooldownDuration(UpstreamFailureKind kind, Exception ex)
    {
        if (ex is UpstreamConnectException { RetryAfter: { } retryAfter })
        {
            if (retryAfter <= TimeSpan.Zero)
                return TimeSpan.FromSeconds(1);

            return retryAfter;
        }

        var fallback = kind switch
        {
            UpstreamFailureKind.UpstreamRateLimited => _reconnectOptions.RateLimitFallbackCooldown,
            UpstreamFailureKind.UpstreamProxyAuthRequired => _reconnectOptions.ProxyAuthFallbackCooldown,
            UpstreamFailureKind.UpstreamServerError => _reconnectOptions.UpstreamServerErrorFallbackCooldown,
            UpstreamFailureKind.Transport
                or UpstreamFailureKind.TimeoutOrStall
                or UpstreamFailureKind.EndOfStream => _reconnectOptions.TransportFallbackCooldown,
            _ => _reconnectOptions.UpstreamServerErrorFallbackCooldown,
        };

        return CapFallbackCooldown(fallback);
    }

    private TimeSpan CapFallbackCooldown(TimeSpan fallback)
    {
        if (fallback <= TimeSpan.Zero)
            fallback = TimeSpan.FromSeconds(1);

        return _reconnectOptions.StrikeCooldown > TimeSpan.Zero && fallback > _reconnectOptions.StrikeCooldown
            ? _reconnectOptions.StrikeCooldown
            : fallback;
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

    private static string ResolveStreamType(string streamUrl)
    {
        var kind = LiveClassifier.ClassifyContent(streamUrl);
        return kind is "vod" or "series" ? kind : "live";
    }

    private static string ResolveProtocol(string streamUrl)
        => streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "mpegts";

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
        var level = reason is SubscriberDisconnectReason.SlowClient or SubscriberDisconnectReason.WriteStall
            ? LogLevel.Warning
            : LogLevel.Information;
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

    private string? GetPendingStopTrigger()
    {
        lock (_gate)
        {
            return GetPendingStopTriggerNoLock();
        }
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
            TotalBytesRelayed: Interlocked.Read(ref _totalBytesRelayed),
            LastDisconnectReason: _lastDisconnectReason,
            LastStopTrigger: _lastStopTrigger,
            LastUpstreamStatusCode: _lastUpstreamStatusCode,
            LastCooldownSeconds: _lastCooldownSeconds,
                            RelayMode: _relayMode,
                            LastRelayFallbackReason: _lastRelayFallbackReason,
                            RelayPolicy: _relayPolicy,
                            RelayDecisionReason: _relayDecisionReason,
                            LastSafeStartKind: _lastSafeStartKind,
            LastRecoveryOutputHeldMs: _lastRecoveryOutputHeldMs,
            LastRecoveryStartedUtc: _lastRecoveryStartedUtc,
            HealthProfile: _currentRecoveryPolicy?.Profile,
            HealthProfileReason: _currentRecoveryPolicy?.Reason);

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
            LastCooldownSeconds: _lastCooldownSeconds,
            RelayMode: _relayMode,
            LastRelayFallbackReason: _lastRelayFallbackReason,
            RelayPolicy: _relayPolicy,
            RelayDecisionReason: _relayDecisionReason,
            LastSafeStartKind: _lastSafeStartKind,
            LastRecoveryOutputHeldMs: _lastRecoveryOutputHeldMs,
            LastRecoveryStartedUtc: _lastRecoveryStartedUtc));
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
        RecordCleanWatchIfEligible();
        await _onClosed(Key, this);
    }

    private void RecordCleanWatchIfEligible()
    {
        if (_state != SessionState.Closed
            || _reconnectAttempts > 0
            || !string.IsNullOrWhiteSpace(_lastFailureKind)
            || _lastUpstreamByteUtc is null
            || Interlocked.Read(ref _totalBytesRelayed) <= 0)
            return;

        var duration = DateTimeOffset.UtcNow - _startedUtc;
        if (duration <= TimeSpan.Zero)
            return;

        RecordDiagnostic(
            StreamDiagnosticEventKind.CleanWatchCompleted,
            cleanWatchDuration: duration,
            message: "Shared stream session completed without an observed upstream health incident.");
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
        int? queueDepth = null,
        TimeSpan? outputHeld = null,
        TimeSpan? recoveryDuration = null,
        string? safeStartKind = null,
        long? bytesSuppressed = null,
        TimeSpan? recoveryHoldLimit = null,
        TimeSpan? cleanWatchDuration = null,
        string? message = null)
    {
        var diagnosticEvent = new StreamDiagnosticEvent(
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
            BytesSent: subscriber?.BytesSent,
            QueueDepth: queueDepth ?? subscriber?.QueueDepth,
            DisconnectReason: disconnectReason,
            StopTrigger: stopTrigger,
            CooldownSeconds: cooldownSeconds,
            RetryAfterSeconds: retryAfterSeconds,
            OutputHeldMs: outputHeld?.TotalMilliseconds,
            RecoveryDurationMs: recoveryDuration?.TotalMilliseconds,
            SafeStartKind: safeStartKind,
            BytesSuppressed: bytesSuppressed,
            RecoveryHoldLimitMs: recoveryHoldLimit?.TotalMilliseconds,
            CleanWatchDurationMs: cleanWatchDuration?.TotalMilliseconds,
            RelayMode: _relayMode,
            Message: message);
        _diagnosticsStore.Record(diagnosticEvent);
        _healthEventRecorder.Record(diagnosticEvent);
    }

    private async Task PublishUnstableProviderEventAsync(string detail)
    {
        try
        {
            await _eventService.PublishAsync(
                SystemEventSeverity.Warning,
                SystemEventTypes.ProviderStreamUnstable,
                $"Provider stream unstable: {_source.DisplayName}",
                detail,
                providerId: _source.ProviderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish ProviderStreamUnstable event for provider {ProviderId}.",
                _source.ProviderId);
        }
    }

    private async Task PublishRecoveredProviderEventIfNeededAsync(CancellationToken ct)
    {
        try
        {
            var hasUnstable = await _eventService.HasEventAsync(
                SystemEventTypes.ProviderStreamUnstable,
                providerId: _source.ProviderId,
                ct: ct);

            if (!hasUnstable)
                return;

            await _eventService.PublishAsync(
                SystemEventSeverity.Info,
                SystemEventTypes.ProviderStreamRecovered,
                $"Provider stream recovered: {_source.DisplayName}",
                "Upstream stream recovered after reconnect.",
                providerId: _source.ProviderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish ProviderStreamRecovered event for provider {ProviderId}.",
                _source.ProviderId);
        }
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

    private readonly record struct SafeStartDecision(bool Selected, string Kind)
    {
        public static SafeStartDecision NotSelected => new(false, string.Empty);
    }

    private static class RecoveryTrimOutcomes
    {
        public const string NotApplicable = "NotApplicable";
        public const string None = "None";
        public const string Trimmed = "Trimmed";
        public const string Abandoned = "Abandoned";
    }
}
