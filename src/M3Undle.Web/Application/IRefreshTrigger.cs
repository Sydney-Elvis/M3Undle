namespace M3Undle.Web.Application;

public interface IRefreshTrigger
{
    /// <summary>Whether a refresh run is currently executing.</summary>
    bool IsRefreshing { get; }

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
}

