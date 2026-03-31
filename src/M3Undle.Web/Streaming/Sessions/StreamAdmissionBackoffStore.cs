using System.Collections.Concurrent;
using M3Undle.Web.Streaming.Models;

namespace M3Undle.Web.Streaming.Sessions;

public sealed class StreamAdmissionBackoffStore
{
    private readonly ConcurrentDictionary<AdmissionBackoffKey, DateTimeOffset> _windows = new();
    private readonly TimeProvider _timeProvider;

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
        if (retryWindow <= TimeSpan.Zero)
            return new AdmissionBackoffObservation(IsRepeated: false, Remaining: TimeSpan.Zero);

        var backoffKey = new AdmissionBackoffKey(key, failureKind);
        var now = _timeProvider.GetUtcNow();

        if (_windows.TryGetValue(backoffKey, out var activeUntil))
        {
            if (activeUntil > now)
                return new AdmissionBackoffObservation(IsRepeated: true, Remaining: activeUntil - now);

            _windows.TryRemove(backoffKey, out _);
        }

        _windows[backoffKey] = now.Add(retryWindow);
        return new AdmissionBackoffObservation(IsRepeated: false, Remaining: retryWindow);
    }

    internal void ClearAll() => _windows.Clear();

    internal readonly record struct AdmissionBackoffObservation(bool IsRepeated, TimeSpan Remaining);

    private readonly record struct AdmissionBackoffKey(
        ChannelSessionKey SessionKey,
        StreamAdmissionFailureKind FailureKind);
}
