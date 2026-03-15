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
    private readonly StreamingRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _admissionGate = new();
    private readonly ConcurrentDictionary<ChannelSessionKey, ChannelStreamSession> _sessions = new();

    public ChannelSessionManager(
        IOptions<BufferOptions> bufferOptions,
        IOptions<StreamProxyOptions> proxyOptions,
        IOptions<ReconnectOptions> reconnectOptions,
        UpstreamStreamConnector upstreamConnector,
        UpstreamFailureStrikeStore strikeStore,
        StreamingRegistry registry,
        ILoggerFactory loggerFactory)
    {
        _bufferOptions = bufferOptions.Value;
        _proxyOptions = proxyOptions.Value;
        _reconnectOptions = reconnectOptions.Value;
        _upstreamConnector = upstreamConnector;
        _strikeStore = strikeStore;
        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    public ValueTask<ChannelStreamSession> GetOrCreateAsync(StreamSourceDescriptor source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = source.SessionKey;

        if (_strikeStore.IsCoolingDown(key, out var cooldownRemaining))
        {
            throw new StreamAdmissionException(
                $"Upstream source is cooling down for {cooldownRemaining.TotalSeconds:F0}s.",
                StatusCodes.Status503ServiceUnavailable,
                retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(Math.Min(30, cooldownRemaining.TotalSeconds))));
        }

        lock (_admissionGate)
        {
            if (_sessions.TryGetValue(key, out var existing))
                return ValueTask.FromResult(existing);

            var maxSessions = Math.Max(1, _proxyOptions.MaxConcurrentSessions);
            if (_sessions.Count >= maxSessions)
            {
                throw new StreamAdmissionException(
                    $"Max concurrent sessions ({maxSessions}) reached.",
                    StatusCodes.Status503ServiceUnavailable,
                    retryAfterSeconds: 30);
            }

            var effectiveProviderCap = source.TunerLimit ?? _proxyOptions.ProviderMaxConcurrentUpstreams;
            if (effectiveProviderCap is { } providerCap and > 0)
            {
                var providerSessionCount = _sessions.Keys.Count(x => x.ProviderId == key.ProviderId);
                if (providerSessionCount >= providerCap)
                {
                    throw new StreamAdmissionException(
                        $"Provider upstream limit ({providerCap}) reached.",
                        StatusCodes.Status503ServiceUnavailable,
                        retryAfterSeconds: 30);
                }
            }

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

    public bool TryGet(ChannelSessionKey key, out ChannelStreamSession? session)
        => _sessions.TryGetValue(key, out session);

    public Task RemoveIfClosedAsync(ChannelSessionKey key, ChannelStreamSession session)
    {
        if (_sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session))
        {
            _sessions.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}

