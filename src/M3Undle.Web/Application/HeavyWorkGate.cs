namespace M3Undle.Web.Application;

/// <summary>
/// Serializes the app's heavy provider-facing work so only one such operation runs at a time:
/// a snapshot refresh (playlist fetch + channel sync + EPG fetch/compile) or a round of
/// background Xtream series expansion.
/// </summary>
/// <remarks>
/// <para>
/// Without this, background series expansion ran completely outside
/// <c>SnapshotRefreshService</c>'s execution gate and hammered the same provider a refresh was
/// fetching from. Panels rate-limit on total request volume, so the overlap pushed providers
/// into 503s that failed the refresh's own lineup fetch.
/// </para>
/// <para>
/// Background expansion acquires this per <em>round</em> (a few hundred series), not per job, so
/// a waiting refresh is delayed by at most one round instead of a multi-minute job.
/// </para>
/// <para>
/// <b>Deadlock hazard — inline expansion must never acquire this.</b> Inline expansion runs
/// inside <c>XtreamLineupClient</c>, which the snapshot refresh calls while it already holds the
/// gate; acquiring again from there would self-deadlock. Only the background job path gates its
/// rounds. Callers that already hold the gate must not re-enter — this is not reentrant.
/// </para>
/// </remarks>
public sealed class HeavyWorkGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True when the gate is currently held — for logging/diagnostics only.</summary>
    public bool IsHeld => _gate.CurrentCount == 0;

    /// <summary>
    /// Acquires the gate, returning a handle that releases it on dispose. Waiters are served in
    /// the order they arrived, so a refresh queued while a round is running is granted the gate
    /// before that job's next round.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Guard against a double-dispose releasing a permit that was never taken.
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
