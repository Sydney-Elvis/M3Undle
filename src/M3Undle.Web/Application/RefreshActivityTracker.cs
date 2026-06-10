namespace M3Undle.Web.Application;

/// <summary>
/// Thread-safe singleton that carries the current fetch activity description
/// from ProviderFetcher into the Dashboard UI during a refresh run.
/// </summary>
public sealed class RefreshActivityTracker
{
    private volatile string? _activity;

    public string? CurrentActivity => _activity;
    public void Set(string? activity) => _activity = activity;
    public void Clear() => _activity = null;
}
