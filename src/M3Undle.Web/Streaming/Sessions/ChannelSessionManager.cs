using System.Collections.Concurrent;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Upstream;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Sessions;

public sealed class ChannelSessionManager
{
    private readonly BufferOptions _bufferOptions;
    private readonly StreamProxyOptions _proxyOptions;
    private readonly ReconnectOptions _reconnectOptions;
    private readonly UpstreamStreamConnector _upstreamConnector;
    private readonly UpstreamFailureStrikeStore _strikeStore;
    private readonly StreamAdmissionBackoffStore _admissionBackoffStore;
    private readonly StreamingRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChannelSessionManager> _logger;
    private readonly object _admissionGate = new();
    private readonly ConcurrentDictionary<ChannelSessionKey, ChannelStreamSession> _sessions = new();
    private readonly ConcurrentDictionary<ChannelSessionKey, HlsAdmissionSlot> _hlsSlots = new();
    private const int DefaultAdmissionRetryAfterSeconds = 30;
    private static readonly TimeSpan DefaultHlsSlotTtl = TimeSpan.FromSeconds(60);

    public ChannelSessionManager(
        IOptions<BufferOptions> bufferOptions,
        IOptions<StreamProxyOptions> proxyOptions,
        IOptions<ReconnectOptions> reconnectOptions,
        UpstreamStreamConnector upstreamConnector,
        UpstreamFailureStrikeStore strikeStore,
        StreamAdmissionBackoffStore admissionBackoffStore,
        StreamingRegistry registry,
        ILoggerFactory loggerFactory)
    {
        _bufferOptions = bufferOptions.Value;
        _proxyOptions = proxyOptions.Value;
        _reconnectOptions = reconnectOptions.Value;
        _upstreamConnector = upstreamConnector;
        _strikeStore = strikeStore;
        _admissionBackoffStore = admissionBackoffStore;
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChannelSessionManager>();
    }

    public ValueTask<ChannelStreamSession> GetOrCreateAsync(StreamSourceDescriptor source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = source.SessionKey;

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
                return ValueTask.FromResult(existing);
            }

            EvictExpiredHlsSlotsLocked();
            var totalUpstreams = _sessions.Count + CountUniqueHlsUpstreamsLocked();

            var maxSessions = Math.Max(1, _proxyOptions.MaxConcurrentSessions);
            if (totalUpstreams >= maxSessions)
            {
                throw CreateAdmissionException(
                    key,
                    source.DisplayName,
                    StreamAdmissionFailureKind.MaxConcurrentSessions,
                    $"Max concurrent sessions ({maxSessions}) reached.",
                    "server is at the maximum of {Limit} concurrent stream(s).",
                    maxSessions);
            }

            var effectiveProviderCap = source.TunerLimit ?? _proxyOptions.ProviderMaxConcurrentUpstreams;
            if (effectiveProviderCap is { } providerCap and > 0)
            {
                var providerUpstreams = CountProviderUpstreamsLocked(key.ProviderId);
                if (providerUpstreams >= providerCap)
                {
                    throw CreateAdmissionException(
                        key,
                        source.DisplayName,
                        StreamAdmissionFailureKind.ProviderLimit,
                        $"Provider upstream limit ({providerCap}) reached.",
                        "provider has reached its upstream limit of {Limit} stream(s).",
                        providerCap);
                }
            }

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
                _loggerFactory.CreateLogger<ChannelStreamSession>(),
                RemoveIfClosedAsync);

            _sessions[key] = session;
            return ValueTask.FromResult(session);
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
            var totalUpstreams = _sessions.Count + CountUniqueHlsUpstreamsLocked();

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
                var providerUpstreams = CountProviderUpstreamsLocked(key.ProviderId);
                if (providerUpstreams >= providerCap)
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
            var totalUpstreams = _sessions.Count + CountUniqueHlsUpstreamsLocked();

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
                var providerUpstreams = CountProviderUpstreamsLocked(key.ProviderId);
                if (providerUpstreams >= providerCap)
                    throw CreateAdmissionException(
                        key,
                        source.DisplayName,
                        StreamAdmissionFailureKind.ProviderLimit,
                        $"Provider upstream limit ({providerCap}) reached.",
                        "provider has reached its upstream limit of {Limit} stream(s).",
                        providerCap);
            }

            var newSlot = new HlsAdmissionSlot(key, source.DisplayName, effectiveTtl);
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
        if (_hlsSlots.TryGetValue(key, out var slot))
        {
            slot.Touch(ttl ?? DefaultHlsSlotTtl);
            PublishHlsSlotSnapshot(slot);
        }
    }

    public void ReleaseHlsSlot(ChannelSessionKey key)
    {
        if (_hlsSlots.TryRemove(key, out var slot))
        {
            _registry.RemoveSession(slot.SessionId);
            _logger.LogInformation("Released HLS admission slot for {Key}.", key);
        }
    }

    public bool TryGet(ChannelSessionKey key, out ChannelStreamSession? session)
        => _sessions.TryGetValue(key, out session);

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
        lock (_admissionGate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _hlsSlots.Clear();
        }

        if (sessions.Length > 0)
            _logger.LogInformation("Resetting all {Count} active stream session(s).", sessions.Length);

        foreach (var session in sessions)
            await session.StopAsync();
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

    private int CountProviderUpstreamsLocked(string providerId)
    {
        var tsCount = _sessions.Keys.Count(x => x.ProviderId == providerId);
        var hlsCount = _hlsSlots.Keys.Count(x => x.ProviderId == providerId && !_sessions.ContainsKey(x));
        return tsCount + hlsCount;
    }

    private int CountUniqueHlsUpstreamsLocked()
        => _hlsSlots.Keys.Count(k => !_sessions.ContainsKey(k));

    private void EvictExpiredHlsSlotsLocked()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _hlsSlots.Where(x => x.Value.ExpiresUtc <= now).Select(x => x.Value).ToArray();
        foreach (var slot in expired)
        {
            if (_hlsSlots.TryRemove(slot.Key, out _))
                _registry.RemoveSession(slot.SessionId);
        }
    }

    internal sealed class HlsAdmissionSlot(ChannelSessionKey key, string displayName, TimeSpan ttl)
    {
        private long _expiresUnixMs = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();
        private long _lastUpstreamByteUnixMs;

        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public ChannelSessionKey Key { get; } = key;
        public string DisplayName { get; } = displayName;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

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
            Interlocked.Exchange(ref _expiresUnixMs, DateTimeOffset.UtcNow.Add(newTtl).ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref _lastUpstreamByteUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

