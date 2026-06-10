namespace M3Undle.Web.Application;

public interface IRefreshTrigger
{
    /// <summary>Whether a refresh run is currently executing.</summary>
    bool IsRefreshing { get; }

    /// <summary>UTC time the current refresh run started, or <c>null</c> if no refresh is active.</summary>
    DateTime? RefreshStartedAt { get; }

    /// <summary>
    /// Short human-readable description of what the refresh is currently doing,
    /// e.g. "Retrying… (attempt 2 of 6)". <c>null</c> when idle or during normal fetching.
    /// </summary>
    string? CurrentActivity { get; }

    /// <summary>
    /// Request an immediate full refresh (fetch from provider + rebuild snapshot).
    /// Returns <c>true</c> when the request was queued; <c>false</c> when a refresh is already
    /// in progress (caller should return HTTP 409).
    /// </summary>
    bool TriggerRefresh();

    /// <summary>
    /// Request a snapshot build from already-synced DB data, without re-fetching from the provider.
    /// Returns <c>true</c> when the request was queued; <c>false</c> when a refresh is already
    /// in progress (caller should return HTTP 409).
    /// </summary>
    bool TriggerBuildOnly();

    /// <summary>
    /// Cancel the currently running refresh/build, if any.
    /// No-op if nothing is running.
    /// </summary>
    void CancelRefresh();
}

