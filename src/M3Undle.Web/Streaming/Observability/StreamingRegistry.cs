using System.Collections.Concurrent;
using M3Undle.Web.Streaming.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace M3Undle.Web.Streaming.Observability;

public sealed class StreamingRegistry(IOptions<StreamProxyOptions> options) : IHostedService
{
    private readonly StreamProxyOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, StreamSessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamClientSnapshot> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamProviderSnapshot> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(DateTimeOffset EndedUtc, StreamSessionSnapshot Snapshot)> _recentEnded = new();

    // Rate calculation — written only by the background timer, read on GetActive* calls.
    private readonly ConcurrentDictionary<string, long> _sessionUpstreamRates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastSessionBytes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _clientSendRates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastClientBytes = new(StringComparer.Ordinal);

    private DateTimeOffset _lastRateSampleAt = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private Task? _rateTask;

    // IHostedService -------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _rateTask = RunRateCalculatorAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        if (_rateTask is not null)
            await _rateTask.ConfigureAwait(false);
    }

    private async Task RunRateCalculatorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                ComputeRates();
        }
        catch (OperationCanceledException) { }
    }

    private void ComputeRates()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _lastRateSampleAt).TotalSeconds;
        if (elapsed < 1.0) return;
        _lastRateSampleAt = now;

        foreach (var (sessionId, snapshot) in _sessions)
        {
            var current = snapshot.TotalBytesRelayed;
            if (_lastSessionBytes.TryGetValue(sessionId, out var prev) && current >= prev)
                _sessionUpstreamRates[sessionId] = (long)((current - prev) / elapsed);
            _lastSessionBytes[sessionId] = current;
        }

        foreach (var (clientId, snapshot) in _clients)
        {
            var current = snapshot.BytesSent;
            if (_lastClientBytes.TryGetValue(clientId, out var prev) && current >= prev)
                _clientSendRates[clientId] = (long)((current - prev) / elapsed);
            _lastClientBytes[clientId] = current;
        }
    }

    // Read -----------------------------------------------------------------

    public IReadOnlyList<StreamSessionSnapshot> GetActiveSessions()
        => _sessions.Values
            .Where(x => !x.IsInternal)
            .Select(x => EnrichSessionRate(AggregateVisibleSession(x)))
            .OrderBy(x => x.StartedUtc)
            .ToArray();

    public IReadOnlyList<StreamSessionSnapshot> GetRecentEndedSessions()
    {
        PruneRecentEnded();
        return _recentEnded.Select(x => x.Snapshot).OrderByDescending(x => x.StartedUtc).ToArray();
    }

    public StreamSessionSnapshot? TryGetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var snapshot)
            ? EnrichSessionRate(snapshot.IsInternal ? snapshot : AggregateVisibleSession(snapshot))
            : null;

    public IReadOnlyList<StreamClientSnapshot> GetActiveClients()
        => _clients.Values
            .Where(x => !x.IsInternal)
            .Select(x => _clientSendRates.TryGetValue(x.ClientId, out var rate) && rate > 0
                ? x with { BytesPerSecond = rate }
                : x)
            .OrderBy(x => x.ConnectedUtc)
            .ToArray();

    public IReadOnlyList<StreamProviderSnapshot> GetActiveProviderStreams()
        => _providers.Values.OrderBy(x => x.SessionId).ToArray();

    // Write ----------------------------------------------------------------

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
        _sessionUpstreamRates.TryRemove(sessionId, out _);
        _lastSessionBytes.TryRemove(sessionId, out _);
        PruneRecentEnded();
    }

    public void UpsertClient(StreamClientSnapshot snapshot)
        => _clients[snapshot.ClientId] = snapshot;

    public void RemoveClient(string clientId)
    {
        _clients.TryRemove(clientId, out _);
        _clientSendRates.TryRemove(clientId, out _);
        _lastClientBytes.TryRemove(clientId, out _);
    }

    public void UpsertProvider(StreamProviderSnapshot snapshot)
        => _providers[snapshot.SessionId] = snapshot;

    public void RemoveProvider(string sessionId)
        => _providers.TryRemove(sessionId, out _);

    // Helpers --------------------------------------------------------------

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
        var hlsSubscribers = _sessions.Values
            .Where(x => x.IsInternal
                && !string.IsNullOrWhiteSpace(x.ParentStreamSessionId)
                && string.Equals(x.ParentStreamSessionId, snapshot.SessionId, StringComparison.Ordinal))
            .Sum(x => x.SubscriberCount);

        if (hlsSubscribers == 0)
            return snapshot;

        var totalSubscribers = snapshot.SubscriberCount + hlsSubscribers;
        return snapshot with
        {
            SubscriberCount = totalSubscribers,
            IsShared = totalSubscribers > 1,
            InferredHlsSubscriberCount = hlsSubscribers,
        };
    }

    private StreamSessionSnapshot EnrichSessionRate(StreamSessionSnapshot snapshot)
        => _sessionUpstreamRates.TryGetValue(snapshot.SessionId, out var rate) && rate > 0
            ? snapshot with { UpstreamBytesPerSecond = rate }
            : snapshot;
}
