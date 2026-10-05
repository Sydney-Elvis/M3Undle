namespace M3Undle.Web.Application.Notifications;

/// <summary>Bounded, jittered exponential backoff shared by every provider. Pure, so it is tested without a clock.</summary>
public static class NotificationRetryPolicy
{
    public const int MaxAttemptsPerCycle = 8;
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan NetworkDeadline = TimeSpan.FromSeconds(30);

    /// <param name="attemptsMade">Attempts already made in this cycle, at least 1.</param>
    /// <param name="retryAfter">Server guidance; a longer value wins over the computed backoff.</param>
    /// <param name="jitter">A value in [0,1); maps to ±20%.</param>
    public static TimeSpan NextDelay(int attemptsMade, TimeSpan? retryAfter, double jitter)
    {
        var exponent = Math.Clamp(attemptsMade - 1, 0, 20);
        var seconds = Math.Min(BaseDelay.TotalSeconds * Math.Pow(2, exponent), MaxDelay.TotalSeconds);
        var factor = 0.8 + Math.Clamp(jitter, 0d, 1d) * 0.4;
        var computed = TimeSpan.FromSeconds(Math.Min(seconds * factor, MaxDelay.TotalSeconds));
        return retryAfter is { } guided && guided > computed ? guided : computed;
    }
}
