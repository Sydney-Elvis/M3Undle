using System.Collections.Concurrent;
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
    private int _closeNotified;
    private int _stopRequested;
    private CancellationTokenSource? _idleCts;

    public ChannelStreamSession(
        StreamSourceDescriptor source,
        BufferOptions bufferOptions,
        StreamProxyOptions proxyOptions,
        ReconnectOptions reconnectOptions,
        UpstreamStreamConnector upstreamConnector,
        UpstreamFailureStrikeStore strikeStore,
        StreamingRegistry registry,
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
        _logger = logger;
        _onClosed = onClosed;
        _idleGrace = ResolveIdleGrace(_proxyOptions);

        var maxBytes = Math.Clamp(_bufferOptions.MaxBytesPerSession, 1, _bufferOptions.MaxBytesHardCap);
        _buffer = new RingBuffer(maxBytes);
    }

    public ChannelSessionKey Key => _source.SessionKey;

    public string SessionId => _sessionId;

    public SessionState State => _state;

    public int SubscriberCount => _subscribers.Count;

    public async Task<SubscriberConnection> AttachSubscriberAsync(HttpContext context, CancellationToken requestCt)
    {
        EnsureStarted();
        await _headersReadyTcs.Task.WaitAsync(requestCt);

        var subscriber = new SubscriberConnection(
            sessionId: _sessionId,
            requestedRoute: _source.RequestedRoute,
            context: context,
            queueCapacity: _bufferOptions.SubscriberQueueCapacity,
            onCompleted: (s, reason) => RemoveSubscriberAsync(s, reason));

        _subscribers[subscriber.ClientId] = subscriber;
        CancelIdleShutdown();

        subscriber.InitializeResponse(_contentType, _cacheControl);
        var snapshot = _buffer.CreateSnapshot();
        _ = subscriber.StartAsync(snapshot, _sessionCts.Token);
        _registry.UpsertClient(subscriber.Snapshot());
        PublishSnapshots();
        return subscriber;
    }

    public Task RemoveSubscriberAsync(SubscriberConnection subscriber, SubscriberDisconnectReason reason)
    {
        if (_subscribers.TryRemove(subscriber.ClientId, out _))
        {
            _registry.RemoveClient(subscriber.ClientId);
        }

        if (_subscribers.IsEmpty)
            ScheduleIdleShutdown();

        PublishSnapshots();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
            return;

        CancelIdleShutdown();
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
        await StopAsync();
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
            ScheduleIdleShutdown();
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

                    await using var upstream = await _upstreamConnector.ConnectAsync(_source, _sessionCts.Token);
                    _contentType = upstream.ContentType;
                    _cacheControl = upstream.Response.Headers.CacheControl?.ToString();
                    _headersReadyTcs.TrySetResult(true);

                    if (reconnectAttempt > 0)
                        _buffer.ResetGeneration();

                    reconnectAttempt = 0;
                    outageStartedUtc = null;
                    SetState(SessionState.Live);
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
                    _reconnectAttempts++;
                    reconnectAttempt++;
                    _logger.LogWarning(
                        "Session {SessionId} upstream failure kind={FailureKind} attempt={Attempt}.",
                        _sessionId,
                        kind,
                        reconnectAttempt);
                    PublishSnapshots();

                    if (IsFatal(kind))
                    {
                        if (!_headersReadyTcs.Task.IsCompleted)
                            _headersReadyTcs.TrySetException(ex);

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
            using var published = _buffer.Write(readBuffer.AsMemory(0, bytesRead));
            var slowSubscribers = new List<SubscriberConnection>();

            foreach (var subscriber in _subscribers.Values)
            {
                var perSubscriber = published.Duplicate();
                if (!subscriber.TryEnqueue(perSubscriber))
                {
                    perSubscriber.Dispose();
                    slowSubscribers.Add(subscriber);
                    continue;
                }

                _registry.UpsertClient(subscriber.Snapshot());
            }

            foreach (var slow in slowSubscribers)
                await slow.CompleteAsync(SubscriberDisconnectReason.SlowClient);

            var tick = Environment.TickCount64;
            if (tick - _lastPublishTick >= 100)
            {
                _lastPublishTick = tick;
                PublishSnapshots();
            }
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

    private void ScheduleIdleShutdown()
    {
        if (_idleGrace <= TimeSpan.Zero)
        {
            _ = StopAsync();
            return;
        }

        lock (_gate)
        {
            _idleCts?.Cancel();
            _idleCts?.Dispose();
            _idleCts = new CancellationTokenSource();
            var token = _idleCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_idleGrace, token);
                    await StopAsync();
                }
                catch (OperationCanceledException)
                {
                    // no-op
                }
            }, token);
        }
    }

    private void CancelIdleShutdown()
    {
        lock (_gate)
        {
            _idleCts?.Cancel();
            _idleCts?.Dispose();
            _idleCts = null;
        }
    }

    private static TimeSpan ResolveIdleGrace(StreamProxyOptions options)
    {
        var idleGrace = options.IdleGrace < TimeSpan.Zero ? TimeSpan.Zero : options.IdleGrace;
        if (options.IdleGraceHardCap > TimeSpan.Zero && idleGrace > options.IdleGraceHardCap)
            return options.IdleGraceHardCap;

        return idleGrace;
    }

    private void PublishSnapshots()
    {
        var session = new StreamSessionSnapshot(
            SessionId: _sessionId,
            ProviderId: _source.ProviderId,
            ProviderChannelId: _source.ProviderChannelId,
            DisplayName: _source.DisplayName,
            State: _state,
            SubscriberCount: _subscribers.Count,
            IsShared: _subscribers.Count > 1,
            BufferUsedBytes: _buffer.UsedBytes,
            BufferMaxBytes: _buffer.MaxBytes,
            StartedUtc: _startedUtc,
            LastUpstreamByteUtc: _lastUpstreamByteUtc,
            ReconnectAttempts: _reconnectAttempts,
            LastFailureKind: _lastFailureKind);

        _registry.UpsertSession(session);
        _registry.UpsertProvider(new StreamProviderSnapshot(
            SessionId: _sessionId,
            ProviderId: _source.ProviderId,
            ProviderChannelId: _source.ProviderChannelId,
            State: _state,
            LastUpstreamByteUtc: _lastUpstreamByteUtc,
            ReconnectAttempts: _reconnectAttempts,
            LastFailureKind: _lastFailureKind,
            ContentType: _contentType));
    }

    private async Task NotifyClosedAsync()
    {
        if (Interlocked.Exchange(ref _closeNotified, 1) == 1)
            return;

        _logger.LogInformation(
            "Shared stream session {SessionId} closed with state {State}.",
            _sessionId,
            _state);
        await _onClosed(Key, this);
    }
}
