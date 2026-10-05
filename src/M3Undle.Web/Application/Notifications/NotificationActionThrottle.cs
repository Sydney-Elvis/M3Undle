namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// Sliding-window limiter for expensive administrator actions (test sends, retries). It is keyed by setup and also has a
/// server-wide ceiling, so it holds even when UI authentication is off and every caller looks anonymous.
/// </summary>
public sealed class NotificationActionThrottle(TimeProvider timeProvider)
{
    public const string ServerWideKey = "*";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    /// <summary>Records a hit when allowed. On refusal, <paramref name="retryAfter"/> says when the oldest hit expires.</summary>
    public bool TryAcquire(string bucket, int limit, TimeSpan window, int serverWideLimit, out TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            var own = Prune(bucket, now, window);
            var global = Prune(ServerWideKey + ":" + window.TotalSeconds.ToString("0"), now, window);
            if (own.Count >= limit)
            {
                retryAfter = own.Peek() + window - now;
                return false;
            }

            if (global.Count >= serverWideLimit)
            {
                retryAfter = global.Peek() + window - now;
                return false;
            }

            own.Enqueue(now);
            global.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private Queue<DateTimeOffset> Prune(string key, DateTimeOffset now, TimeSpan window)
    {
        if (!_hits.TryGetValue(key, out var queue))
            _hits[key] = queue = new Queue<DateTimeOffset>();
        while (queue.Count > 0 && queue.Peek() + window <= now)
            queue.Dequeue();
        return queue;
    }
}

public class NotificationRuntimeOptions(EnvironmentVariableService env)
{
    /// <summary>Only an isolated lab may use plain HTTP to a homeserver. Production configuration always requires HTTPS.</summary>
    public virtual bool AllowInsecureMatrixHttp =>
        string.Equals(env.GetValue("M3UNDLE_NOTIFICATIONS_ALLOW_INSECURE_MATRIX_HTTP"), "true", StringComparison.OrdinalIgnoreCase);
}
