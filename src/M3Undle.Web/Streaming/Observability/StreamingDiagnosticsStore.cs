using System.Collections.Concurrent;
using M3Undle.Web.Streaming.Configuration;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Observability;

public sealed class StreamingDiagnosticsStore(IOptions<StreamProxyOptions> options)
{
    private readonly StreamProxyOptions _options = options.Value;
    private readonly ConcurrentQueue<StreamDiagnosticEvent> _events = new();
    private readonly object _pruneGate = new();

    public void Record(StreamDiagnosticEvent diagnosticEvent)
    {
        _events.Enqueue(diagnosticEvent);
        Prune();
    }

    public IReadOnlyList<StreamDiagnosticEvent> Query(
        string? sessionId = null,
        string? providerId = null,
        string? providerChannelId = null,
        StreamDiagnosticEventKind? kind = null,
        DateTimeOffset? sinceUtc = null)
    {
        Prune();

        IEnumerable<StreamDiagnosticEvent> query = _events;
        if (!string.IsNullOrWhiteSpace(sessionId))
            query = query.Where(x => string.Equals(x.SessionId, sessionId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(providerId))
            query = query.Where(x => string.Equals(x.ProviderId, providerId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(providerChannelId))
            query = query.Where(x => string.Equals(x.ProviderChannelId, providerChannelId, StringComparison.Ordinal));
        if (kind is not null)
            query = query.Where(x => x.Kind == kind);
        if (sinceUtc is not null)
            query = query.Where(x => x.TimestampUtc >= sinceUtc.Value);

        return query.OrderBy(x => x.TimestampUtc).ToArray();
    }

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
    }

    private void Prune()
    {
        lock (_pruneGate)
        {
            var maxEvents = Math.Clamp(_options.DiagnosticsMaxEvents, 0, 10_000);
            var retention = TimeSpan.FromSeconds(Math.Clamp(_options.DiagnosticsRetentionSeconds, 0, 86_400));
            var cutoff = retention <= TimeSpan.Zero ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow - retention;

            while (_events.TryPeek(out var head)
                   && (_events.Count > maxEvents || head.TimestampUtc < cutoff))
            {
                _events.TryDequeue(out _);
            }
        }
    }
}
