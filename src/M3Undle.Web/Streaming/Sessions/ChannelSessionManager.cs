using System.Collections.Concurrent;
using System.Collections.Generic;
using M3Undle.Web.Application;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Observability;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Sessions;

public sealed class ChannelSessionManager : IHostedService, IDisposable
{
    private readonly BufferOptions _bufferOptions;
    private readonly StreamProxyOptions _proxyOptions;
    private readonly ReconnectOptions _reconnectOptions;
    private readonly UpstreamStreamConnector _upstreamConnector;
    private readonly UpstreamFailureStrikeStore _strikeStore;
    private readonly StreamAdmissionBackoffStore _admissionBackoffStore;
    private readonly StreamingRegistry _registry;
    private readonly StreamingDiagnosticsStore _diagnosticsStore;
    private readonly IStreamChannelHealthEventRecorder _healthEventRecorder;
    private readonly IStreamChannelHealthProfileService _healthProfileService;
    private readonly IEventService _eventService;
    private readonly M3UndleMetrics? _metrics;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChannelSessionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _admissionGate = new();
    private readonly ConcurrentDictionary<ChannelSessionKey, ChannelStreamSession> _sessions = new();
    private readonly ConcurrentDictionary<ChannelSessionKey, HlsAdmissionSlot> _hlsSlots = new();
    private readonly Dictionary<ChannelSessionKey, Task> _pendingIdlePreemptions = [];
    private CancellationTokenSource? _hlsSweepCts;
    private Task? _hlsSweepTask;
    private const int DefaultAdmissionRetryAfterSeconds = 30;
    private static readonly TimeSpan DefaultHlsSlotTtl = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultHlsSlotSweepInterval = TimeSpan.FromSeconds(15);

    public ChannelSessionManager(
        IOptions<BufferOptions> bufferOptions,
        IOptions<StreamProxyOptions> proxyOptions,
        IOptions<ReconnectOptions> reconnectOptions,
        UpstreamStreamConnector upstreamConnector,
        UpstreamFailureStrikeStore strikeStore,
        StreamAdmissionBackoffStore admissionBackoffStore,
        StreamingRegistry registry,
        StreamingDiagnosticsStore diagnosticsStore,
        IStreamChannelHealthEventRecorder healthEventRecorder,
        IStreamChannelHealthProfileService healthProfileService,
        IEventService eventService,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        M3UndleMetrics? metrics = null)
    {
        _bufferOptions = bufferOptions.Value;
        _proxyOptions = proxyOptions.Value;
        _reconnectOptions = reconnectOptions.Value;
        _upstreamConnector = upstreamConnector;
        _strikeStore = strikeStore;
        _admissionBackoffStore = admissionBackoffStore;
        _registry = registry;
        _diagnosticsStore = diagnosticsStore;
        _healthEventRecorder = healthEventRecorder;
        _healthProfileService = healthProfileService;
        _eventService = eventService;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChannelSessionManager>();
        _timeProvider = timeProvider;
        _metrics = metrics;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_admissionGate)
        {
            if (_hlsSweepTask is not null)
                return Task.CompletedTask;

            _hlsSweepCts = new CancellationTokenSource();
            _hlsSweepTask = Task.Run(() => SweepExpiredHlsSlotsLoopAsync(_hlsSweepCts.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? sweepCts;
        Task? sweepTask;

        lock (_admissionGate)
        {
            sweepCts = _hlsSweepCts;
            sweepTask = _hlsSweepTask;
            _hlsSweepCts = null;
            _hlsSweepTask = null;
        }

        if (sweepCts is null || sweepTask is null)
            return;

        try
        {
            sweepCts.Cancel();
            await sweepTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            sweepCts.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_admissionGate)
        {
            _hlsSweepCts?.Cancel();
            _hlsSweepCts?.Dispose();
            _hlsSweepCts = null;
            _hlsSweepTask = null;
        }
    }

    public ValueTask<ChannelStreamSession> GetOrCreateAsync(StreamSourceDescriptor source, CancellationToken ct)
    {
        return GetOrCreateCoreAsync(source, ct);
    }

    private async ValueTask<ChannelStreamSession> GetOrCreateCoreAsync(StreamSourceDescriptor source, CancellationToken ct)
    {
        var key = source.SessionKey;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task? preemptionTask = null;

            lock (_admissionGate)
            {
                ThrowIfCoolingDown(key, source.DisplayName, logWarning: true);

                if (_sessions.TryGetValue(key, out var existing))
                {
                    _logger.LogDebug(
                        "Joining existing session {SessionId} for '{DisplayName}' ({SubscriberCount} viewer(s) already watching).",
                        existing.SessionId,
                        source.DisplayName,
                        existing.SubscriberCount);
                    return existing;
                }

                EvictExpiredHlsSlotsLocked();
                var maxSessions = Math.Max(1, _proxyOptions.MaxConcurrentSessions);
                var activeUpstreams = CountActiveSessionUpstreamsLocked() + CountUniqueHlsUpstreamsLocked();
                if (activeUpstreams >= maxSessions)
                {
                    throw CreateAdmissionException(
                        key,
                        source.DisplayName,
                        StreamAdmissionFailureKind.MaxConcurrentSessions,
                        $"Max concurrent sessions ({maxSessions}) reached.",
                        "server is at the maximum of {Limit} concurrent stream(s).",
                        maxSessions);
                }

                var trackedUpstreams = _sessions.Count + CountUniqueHlsUpstreamsLocked();
                var shouldPreemptTrackedCapacity = trackedUpstreams >= maxSessions;

                var effectiveProviderCap = source.TunerLimit ?? _proxyOptions.ProviderMaxConcurrentUpstreams;
                if (effectiveProviderCap is { } providerCap and > 0)
                {
                    var providerUpstreams = CountProviderSessionsLocked(key.ProviderId, includeIdleGrace: false)
                        + CountProviderHlsUpstreamsLocked(key.ProviderId);
                    if (providerUpstreams >= providerCap)
                    {
                        _metrics?.RecordProviderStreamLimitReached(key.ProviderId);
                        throw CreateAdmissionException(
                            key,
                            source.DisplayName,
                            StreamAdmissionFailureKind.ProviderLimit,
                            $"Provider upstream limit ({providerCap}) reached.",
                            "provider has reached its upstream limit of {Limit} stream(s).",
                            providerCap);
                    }

                    var trackedProviderUpstreams = CountProviderSessionsLocked(key.ProviderId, includeIdleGrace: true)
                        + CountProviderHlsUpstreamsLocked(key.ProviderId);
                    if (trackedProviderUpstreams >= providerCap)
                        preemptionTask = TryBeginProviderIdleCapacityPreemptionLocked(source);
                }

                if (preemptionTask is null && shouldPreemptTrackedCapacity)
                    preemptionTask = TryBeginIdleCapacityPreemptionLocked(source);

                if (preemptionTask is null)
                {
                    _logger.LogInformation(
                        "Opening new stream session for '{DisplayName}' ({ActiveSessions} session(s) now active).",
                        source.DisplayName,
                        _sessions.Count + 1);

                    var session = new ChannelStreamSession(
                        source,
                        _bufferOptions,
                        _proxyOptions,
                        _reconnectOptions,
                        _upstreamConnector,
                        _strikeStore,
                        _registry,
                        _diagnosticsStore,
                        _healthEventRecorder,
                        _healthProfileService,
                        _eventService,
                        _loggerFactory.CreateLogger<ChannelStreamSession>(),
                        RemoveIfClosedAsync,
                        _metrics);

                    _sessions[key] = session;
                    return session;
                }
            }

            await preemptionTask.WaitAsync(ct);
        }
    }

    public void CheckAdmission(StreamSourceDescriptor source)
    {
        var key = source.SessionKey;

        lock (_admissionGate)
        {
            ThrowIfCoolingDown(key, source.DisplayName, logWarning: false);

            if (_sessions.ContainsKey(key) || _hlsSlots.ContainsKey(key))
                return;

            EvictExpiredHlsSlotsLocked();
            var totalUpstreams = CountActiveSessionUpstreamsLocked() + CountUniqueHlsUpstreamsLocked();

            var maxSessions = Math.Max(1, _proxyOptions.MaxConcurrentSessions);
            if (totalUpstreams >= maxSessions)
                throw CreateAdmissionException(
                    key,
                    source.DisplayName,
                    StreamAdmissionFailureKind.MaxConcurrentSessions,
                    $"Max concurrent sessions ({maxSessions}) reached.",
                    "server is at the maximum of {Limit} concurrent stream(s).",
                    maxSessions);

            var effectiveProviderCap = source.TunerLimit ?? _proxyOptions.ProviderMaxConcurrentUpstreams;
            if (effectiveProviderCap is { } providerCap and > 0)
            {
                var providerUpstreams = CountProviderSessionsLocked(key.ProviderId, includeIdleGrace: false)
                    + CountProviderHlsUpstreamsLocked(key.ProviderId);
                if (providerUpstreams >= providerCap)
                {
                    _metrics?.RecordProviderStreamLimitReached(key.ProviderId);
                    throw CreateAdmissionException(
                        key,
                        source.DisplayName,
                        StreamAdmissionFailureKind.ProviderLimit,
                        $"Provider upstream limit ({providerCap}) reached.",
                        "provider has reached its upstream limit of {Limit} stream(s).",
                        providerCap);
                }
            }
        }
    }

    internal async ValueTask<ChannelStreamSession?> TryGetOrCreateForGeneratedHlsAsync(
        StreamSourceDescriptor source,
        bool useSharedSession,
        CancellationToken ct)
    {
        if (_sessions.TryGetValue(source.SessionKey, out var existing))
            return existing;

        if (!useSharedSession)
            return null;

        try
        {
            return await GetOrCreateAsync(source, ct);
        }
        catch (StreamAdmissionException)
        {
            return null;
        }
    }

    public HlsSlotReservation ReserveHlsSlot(StreamSourceDescriptor source, TimeSpan? ttl = null)
    {
        var key = source.SessionKey;
        var effectiveTtl = ttl ?? DefaultHlsSlotTtl;

        lock (_admissionGate)
        {
            ThrowIfCoolingDown(key, source.DisplayName, logWarning: false);

            if (_sessions.ContainsKey(key) || _hlsSlots.ContainsKey(key))
            {
                if (_hlsSlots.TryGetValue(key, out var existingSlot))
                    existingSlot.Touch(effectiveTtl);

                return new HlsSlotReservation(this, key);
            }

            EvictExpiredHlsSlotsLocked();
            var totalUpstreams = CountActiveSessionUpstreamsLocked() + CountUniqueHlsUpstreamsLocked();

            var maxSessions = Math.Max(1, _proxyOptions.MaxConcurrentSessions);
            if (totalUpstreams >= maxSessions)
                throw CreateAdmissionException(
                    key,
                    source.DisplayName,
                    StreamAdmissionFailureKind.MaxConcurrentSessions,
                    $"Max concurrent sessions ({maxSessions}) reached.",
                    "server is at the maximum of {Limit} concurrent stream(s).",
                    maxSessions);

            var effectiveProviderCap = source.TunerLimit ?? _proxyOptions.ProviderMaxConcurrentUpstreams;
            if (effectiveProviderCap is { } providerCap and > 0)
            {
                var providerUpstreams = CountProviderSessionsLocked(key.ProviderId, includeIdleGrace: false)
                    + CountProviderHlsUpstreamsLocked(key.ProviderId);
                if (providerUpstreams >= providerCap)
                    throw CreateAdmissionException(
                        key,
                        source.DisplayName,
                        StreamAdmissionFailureKind.ProviderLimit,
                        $"Provider upstream limit ({providerCap}) reached.",
                        "provider has reached its upstream limit of {Limit} stream(s).",
                        providerCap);
            }

            var newSlot = new HlsAdmissionSlot(
                key,
                source.DisplayName,
                source.RequestedRoute,
                source.RemoteIp,
                source.UserAgent,
                effectiveTtl,
                _timeProvider);
            _hlsSlots[key] = newSlot;
            _logger.LogInformation(
                "Reserved HLS admission slot for '{DisplayName}' ({TotalUpstreams} upstream(s) now tracked).",
                source.DisplayName,
                totalUpstreams + 1);
            PublishHlsSlotSnapshot(newSlot);

            return new HlsSlotReservation(this, key);
        }
    }

    public void TouchHlsSlot(ChannelSessionKey key, TimeSpan? ttl = null)
    {
        lock (_admissionGate)
        {
            if (_hlsSlots.TryGetValue(key, out var slot))
            {
                slot.Touch(ttl ?? DefaultHlsSlotTtl);
                PublishHlsSlotSnapshot(slot);
            }
        }
    }

    public void ReleaseHlsSlot(ChannelSessionKey key)
    {
        if (_hlsSlots.TryRemove(key, out var slot))
        {
            _registry.RemoveClient(slot.SessionId);
            _registry.RemoveSession(slot.SessionId);
            _logger.LogInformation("Released HLS admission slot for {Key}.", key);
        }
    }

    public bool TryGet(ChannelSessionKey key, out ChannelStreamSession? session)
        => _sessions.TryGetValue(key, out session);

    public IDisposable? RetainGeneratedHlsActivity(ChannelSessionKey key, string? parentStreamSessionId)
    {
        if (string.IsNullOrWhiteSpace(parentStreamSessionId))
            return null;

        if (!_sessions.TryGetValue(key, out var session)
            || !string.Equals(session.SessionId, parentStreamSessionId, StringComparison.Ordinal))
        {
            return null;
        }

        return session.RetainExternalActivity();
    }

    public Task RemoveIfClosedAsync(ChannelSessionKey key, ChannelStreamSession session)
    {
        lock (_admissionGate)
        {
            if (_sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session))
                _sessions.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public async Task ResetAllAsync()
    {
        ChannelStreamSession[] sessions;
        HlsAdmissionSlot[] hlsSlots;
        lock (_admissionGate)
        {
            sessions = _sessions.Values.ToArray();
            hlsSlots = _hlsSlots.Values.ToArray();
            _sessions.Clear();
            _hlsSlots.Clear();
        }

        if (sessions.Length > 0)
            _logger.LogInformation("Resetting all {Count} active stream session(s).", sessions.Length);

        foreach (var slot in hlsSlots)
        {
            _registry.RemoveClient(slot.SessionId);
            _registry.RemoveSession(slot.SessionId);
        }

        foreach (var session in sessions)
            await session.StopAsync();
    }

    internal int SweepExpiredHlsSlots()
    {
        lock (_admissionGate)
        {
            return EvictExpiredHlsSlotsLocked();
        }
    }

    private StreamAdmissionException CreateAdmissionException(
        ChannelSessionKey key,
        string displayName,
        StreamAdmissionFailureKind failureKind,
        string message,
        string warningDetail,
        int limit)
    {
        var observation = _admissionBackoffStore.Observe(
            key,
            failureKind,
            TimeSpan.FromSeconds(DefaultAdmissionRetryAfterSeconds));

        if (observation.IsRepeated)
        {
            _logger.LogDebug(
                "Repeated stream request rejected for '{DisplayName}' — " + warningDetail + " Retry window remains active for {Seconds:F0}s.",
                displayName,
                limit,
                observation.Remaining.TotalSeconds);
        }
        else
        {
            _logger.LogWarning(
                "Stream request rejected for '{DisplayName}' — " + warningDetail,
                displayName,
                limit);
        }

        return new StreamAdmissionException(
            message,
            failureKind,
            StatusCodes.Status503ServiceUnavailable,
            retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(Math.Min(DefaultAdmissionRetryAfterSeconds, observation.Remaining.TotalSeconds))));
    }

    private void ThrowIfCoolingDown(ChannelSessionKey key, string displayName, bool logWarning)
    {
        if (!_strikeStore.IsCoolingDown(key, out var cooldownRemaining))
            return;

        if (logWarning)
        {
            _logger.LogWarning(
                "Stream request rejected for '{DisplayName}' — upstream is in cooldown for {Seconds:F0}s more. Try again shortly.",
                displayName,
                cooldownRemaining.TotalSeconds);
        }

        _diagnosticsStore.Record(new StreamDiagnosticEvent(
            EventId: Guid.NewGuid().ToString("N"),
            TimestampUtc: _timeProvider.GetUtcNow(),
            Kind: StreamDiagnosticEventKind.AdmissionRejected,
            ProviderId: key.ProviderId,
            ProviderChannelId: key.ProviderChannelId,
            DisplayName: displayName,
            CooldownSeconds: cooldownRemaining.TotalSeconds,
            RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling(Math.Min(30, cooldownRemaining.TotalSeconds))),
            Message: "Stream request rejected because upstream source is cooling down."));

        throw new StreamAdmissionException(
            $"Upstream source is cooling down for {cooldownRemaining.TotalSeconds:F0}s.",
            StreamAdmissionFailureKind.Cooldown,
            StatusCodes.Status503ServiceUnavailable,
            retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(Math.Min(30, cooldownRemaining.TotalSeconds))));
    }

    private void PublishHlsSlotSnapshot(HlsAdmissionSlot slot)
    {
        _registry.UpsertSession(new StreamSessionSnapshot(
            SessionId: slot.SessionId,
            ProviderId: slot.Key.ProviderId,
            ProviderChannelId: slot.Key.ProviderChannelId,
            DisplayName: slot.DisplayName,
            State: SessionState.Live,
            SubscriberCount: 1,
            IsShared: false,
            BufferUsedBytes: 0,
            BufferMaxBytes: 0,
            StartedUtc: slot.StartedUtc,
            LastUpstreamByteUtc: slot.LastUpstreamByteUtc,
            ReconnectAttempts: 0,
            LastFailureKind: null));

        _registry.UpsertClient(new StreamClientSnapshot(
            ClientId: slot.SessionId,
            SessionId: slot.SessionId,
            RequestedRoute: slot.RequestedRoute,
            RemoteIp: slot.RemoteIp,
            UserAgent: slot.UserAgent,
            ConnectedUtc: slot.StartedUtc,
            BytesSent: 0,
            QueueDepth: 0));

        _registry.UpsertProvider(new StreamProviderSnapshot(
            SessionId: slot.SessionId,
            ProviderId: slot.Key.ProviderId,
            ProviderChannelId: slot.Key.ProviderChannelId,
            State: SessionState.Live,
            LastUpstreamByteUtc: slot.LastUpstreamByteUtc,
            ReconnectAttempts: 0,
            LastFailureKind: null,
            ContentType: "application/vnd.apple.mpegurl"));
    }

    private int CountActiveSessionUpstreamsLocked()
        => _sessions.Values.Count(s => !s.IsIdleGraceWithNoConsumers);

    private int CountProviderSessionsLocked(string providerId, bool includeIdleGrace)
        => _sessions.Values.Count(s =>
            s.Key.ProviderId == providerId
            && (includeIdleGrace || !s.IsIdleGraceWithNoConsumers));

    private int CountProviderHlsUpstreamsLocked(string providerId)
        => _hlsSlots.Keys.Count(x => x.ProviderId == providerId && !_sessions.ContainsKey(x));

    private Task? TryBeginIdleCapacityPreemptionLocked(StreamSourceDescriptor source)
    {
        var candidate = _sessions.Values.FirstOrDefault(session =>
            !session.Key.Equals(source.SessionKey)
            && session.IsIdleGraceWithNoConsumers);

        return TryBeginIdlePreemptionLocked(
            candidate,
            source,
            "idle_capacity_preempted",
            IdlePreemptionReason.SessionCapacity);
    }

    private Task? TryBeginProviderIdleCapacityPreemptionLocked(StreamSourceDescriptor source)
    {
        var candidate = _sessions.Values.FirstOrDefault(session =>
            !session.Key.Equals(source.SessionKey)
            && string.Equals(session.Key.ProviderId, source.SessionKey.ProviderId, StringComparison.Ordinal)
            && session.IsIdleGraceWithNoConsumers);

        return TryBeginIdlePreemptionLocked(
            candidate,
            source,
            "provider_idle_capacity_preempted",
            IdlePreemptionReason.ProviderCapacity);
    }

    private Task? TryBeginIdlePreemptionLocked(
        ChannelStreamSession? candidate,
        StreamSourceDescriptor source,
        string stopTrigger,
        IdlePreemptionReason reason)
    {
        if (candidate is null)
            return null;

        if (_pendingIdlePreemptions.TryGetValue(candidate.Key, out var existingTask))
            return existingTask;

        Task preemptionTask = null!;
        preemptionTask = PreemptAsync();
        _pendingIdlePreemptions[candidate.Key] = preemptionTask;
        return preemptionTask;

        async Task PreemptAsync()
        {
            try
            {
                if (reason == IdlePreemptionReason.ProviderCapacity)
                {
                    _logger.LogInformation(
                        "Preempting provider idle-grace session {SessionId} for {ProviderId}/{ProviderChannelId} so '{DisplayName}' can open without exceeding the tracked provider cap.",
                        candidate.SessionId,
                        candidate.Key.ProviderId,
                        candidate.Key.ProviderChannelId,
                        source.DisplayName);
                }
                else
                {
                    _logger.LogInformation(
                        "Preempting idle-grace session {SessionId} for {ProviderId}/{ProviderChannelId} so '{DisplayName}' can open without exceeding the tracked session cap.",
                        candidate.SessionId,
                        candidate.Key.ProviderId,
                        candidate.Key.ProviderChannelId,
                        source.DisplayName);
                }

                await candidate.StopAsync(stopTrigger);
            }
            finally
            {
                lock (_admissionGate)
                {
                    if (_pendingIdlePreemptions.TryGetValue(candidate.Key, out var current)
                        && ReferenceEquals(current, preemptionTask))
                    {
                        _pendingIdlePreemptions.Remove(candidate.Key);
                    }
                }
            }
        }
    }

    private enum IdlePreemptionReason
    {
        SessionCapacity,
        ProviderCapacity,
    }

    private int CountUniqueHlsUpstreamsLocked()
        => _hlsSlots.Keys.Count(k => !_sessions.ContainsKey(k));

    private async Task SweepExpiredHlsSlotsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DefaultHlsSlotSweepInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var evicted = SweepExpiredHlsSlots();
                if (evicted > 0)
                    _logger.LogDebug("Evicted {Count} expired HLS admission slot(s).", evicted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private int EvictExpiredHlsSlotsLocked()
    {
        var now = _timeProvider.GetUtcNow();
        var expired = _hlsSlots.Where(x => x.Value.ExpiresUtc <= now).Select(x => x.Value).ToArray();
        var removed = 0;
        foreach (var slot in expired)
        {
            if (_hlsSlots.TryRemove(slot.Key, out _))
            {
                _registry.RemoveClient(slot.SessionId);
                _registry.RemoveSession(slot.SessionId);
                removed++;
            }
        }

        return removed;
    }

    internal sealed class HlsAdmissionSlot(
        ChannelSessionKey key,
        string displayName,
        string requestedRoute,
        string? remoteIp,
        string? userAgent,
        TimeSpan ttl,
        TimeProvider timeProvider)
    {
        private readonly TimeProvider _timeProvider = timeProvider;
        private long _expiresUnixMs = timeProvider.GetUtcNow().Add(ttl).ToUnixTimeMilliseconds();
        private long _lastUpstreamByteUnixMs;

        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public ChannelSessionKey Key { get; } = key;
        public string DisplayName { get; } = displayName;
        public string RequestedRoute { get; } = requestedRoute;
        public string? RemoteIp { get; } = remoteIp;
        public string? UserAgent { get; } = userAgent;
        public DateTimeOffset StartedUtc { get; } = timeProvider.GetUtcNow();

        public DateTimeOffset ExpiresUtc
            => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _expiresUnixMs));

        public DateTimeOffset? LastUpstreamByteUtc
        {
            get
            {
                var ms = Interlocked.Read(ref _lastUpstreamByteUnixMs);
                return ms == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }
        }

        public void Touch(TimeSpan newTtl)
        {
            var now = _timeProvider.GetUtcNow();
            Interlocked.Exchange(ref _expiresUnixMs, now.Add(newTtl).ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref _lastUpstreamByteUnixMs, now.ToUnixTimeMilliseconds());
        }
    }

    public sealed class HlsSlotReservation(ChannelSessionManager manager, ChannelSessionKey key) : IDisposable
    {
        private int _disposed;

        public ChannelSessionKey Key { get; } = key;

        public void Touch(TimeSpan? ttl = null) => manager.TouchHlsSlot(Key, ttl);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                manager.ReleaseHlsSlot(Key);
        }
    }
}
