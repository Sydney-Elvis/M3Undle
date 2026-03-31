using System.Collections.Concurrent;
using M3Undle.Web.Streaming.Models;

namespace M3Undle.Web.Streaming.Sessions;

public sealed class StreamAdmissionBackoffStore
{
    private readonly ConcurrentDictionary<AdmissionBackoffKey, DateTimeOffset> _windows = new();
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastSweep;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public StreamAdmissionBackoffStore()
        : this(TimeProvider.System)
    {
    }

    internal StreamAdmissionBackoffStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    internal AdmissionBackoffObservation Observe(
        ChannelSessionKey key,
        StreamAdmissionFailureKind failureKind,
        TimeSpan retryWindow)
    {
        var scopedKey = failureKind switch
        {
            StreamAdmissionFailureKind.MaxConcurrentSessions => AdmissionBackoffKey.Global(failureKind),
            StreamAdmissionFailureKind.ProviderLimit => AdmissionBackoffKey.Provider(key.ProviderId, failureKind),
            _ => AdmissionBackoffKey.Channel(key, failureKind),
        };

        return ObserveCore(scopedKey, retryWindow);
    }

    private AdmissionBackoffObservation ObserveCore(AdmissionBackoffKey backoffKey, TimeSpan retryWindow)
    {
        if (retryWindow <= TimeSpan.Zero)
            return new AdmissionBackoffObservation(IsRepeated: false, Remaining: TimeSpan.Zero);

        var now = _timeProvider.GetUtcNow();
        SweepExpired(now);

        if (_windows.TryGetValue(backoffKey, out var activeUntil))
        {
            if (activeUntil > now)
                return new AdmissionBackoffObservation(IsRepeated: true, Remaining: activeUntil - now);

            _windows.TryRemove(backoffKey, out _);
        }

        _windows[backoffKey] = now.Add(retryWindow);
        return new AdmissionBackoffObservation(IsRepeated: false, Remaining: retryWindow);
    }

    private void SweepExpired(DateTimeOffset now)
    {
        if (_lastSweep != default && now - _lastSweep < SweepInterval)
            return;

        _lastSweep = now;

        foreach (var kvp in _windows)
        {
            if (kvp.Value <= now)
                _windows.TryRemove(kvp.Key, out _);
        }
    }

    internal void ClearAll() => _windows.Clear();

    internal readonly record struct AdmissionBackoffObservation(bool IsRepeated, TimeSpan Remaining);

    private readonly record struct AdmissionBackoffKey(
        string Scope,
        string ScopeId,
        StreamAdmissionFailureKind FailureKind)
    {
        public static AdmissionBackoffKey Global(StreamAdmissionFailureKind failureKind)
            => new("global", string.Empty, failureKind);

        public static AdmissionBackoffKey Provider(string providerId, StreamAdmissionFailureKind failureKind)
            => new("provider", providerId, failureKind);

        public static AdmissionBackoffKey Channel(ChannelSessionKey sessionKey, StreamAdmissionFailureKind failureKind)
            => new("channel", sessionKey.ToString(), failureKind);
    }
}
