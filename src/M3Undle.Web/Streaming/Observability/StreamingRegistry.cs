using System.Collections.Concurrent;
using M3Undle.Web.Streaming.Configuration;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Observability;

public sealed class StreamingRegistry(IOptions<StreamProxyOptions> options)
{
    private readonly StreamProxyOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, StreamSessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamClientSnapshot> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamProviderSnapshot> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(DateTimeOffset EndedUtc, StreamSessionSnapshot Snapshot)> _recentEnded = new();

    public IReadOnlyList<StreamSessionSnapshot> GetActiveSessions()
        => _sessions.Values
            .Where(x => !x.IsInternal)
            .Select(AggregateVisibleSession)
            .OrderBy(x => x.StartedUtc)
            .ToArray();

    public IReadOnlyList<StreamSessionSnapshot> GetRecentEndedSessions()
    {
        PruneRecentEnded();
        return _recentEnded.Select(x => x.Snapshot).OrderByDescending(x => x.StartedUtc).ToArray();
    }

    public StreamSessionSnapshot? TryGetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var snapshot)
            ? snapshot.IsInternal ? snapshot : AggregateVisibleSession(snapshot)
            : null;

    public IReadOnlyList<StreamClientSnapshot> GetActiveClients()
        => _clients.Values.Where(x => !x.IsInternal).OrderBy(x => x.ConnectedUtc).ToArray();

    public IReadOnlyList<StreamProviderSnapshot> GetActiveProviderStreams()
        => _providers.Values.OrderBy(x => x.SessionId).ToArray();

    public void UpsertSession(StreamSessionSnapshot snapshot)
    {
        _sessions[snapshot.SessionId] = snapshot;
        PruneRecentEnded();
    }

    public void RemoveSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var snapshot))
        {
            if (!snapshot.IsInternal)
                _recentEnded.Enqueue((DateTimeOffset.UtcNow, snapshot));
        }

        _providers.TryRemove(sessionId, out _);
        PruneRecentEnded();
    }

    public void UpsertClient(StreamClientSnapshot snapshot)
        => _clients[snapshot.ClientId] = snapshot;

    public void RemoveClient(string clientId)
        => _clients.TryRemove(clientId, out _);

    public void UpsertProvider(StreamProviderSnapshot snapshot)
        => _providers[snapshot.SessionId] = snapshot;

    public void RemoveProvider(string sessionId)
        => _providers.TryRemove(sessionId, out _);

    private void PruneRecentEnded()
    {
        var retention = TimeSpan.FromSeconds(Math.Clamp(_options.DetailedStatusRetentionSeconds, 0, 3600));
        if (retention <= TimeSpan.Zero)
        {
            while (_recentEnded.TryDequeue(out _)) { }
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - retention;
        while (_recentEnded.TryPeek(out var head) && head.EndedUtc < cutoff)
        {
            _recentEnded.TryDequeue(out _);
        }
    }

    private StreamSessionSnapshot AggregateVisibleSession(StreamSessionSnapshot snapshot)
    {
        var additionalSubscribers = _sessions.Values
            .Where(x => x.IsInternal
                && !string.IsNullOrWhiteSpace(x.ParentStreamSessionId)
                && string.Equals(x.ParentStreamSessionId, snapshot.SessionId, StringComparison.Ordinal))
            .Sum(x => x.SubscriberCount);

        if (additionalSubscribers == 0)
            return snapshot;

        var totalSubscribers = snapshot.SubscriberCount + additionalSubscribers;
        return snapshot with
        {
            SubscriberCount = totalSubscribers,
            IsShared = totalSubscribers > 1,
        };
    }
}

