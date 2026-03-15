namespace M3Undle.Web.Streaming.Configuration;

public sealed class ReconnectOptions
{
    public TimeSpan ReadStallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan OutageWindow { get; set; } = TimeSpan.FromSeconds(75);

    public TimeSpan StrikeCooldown { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public int[] FixedStepBackoffSeconds { get; set; } = [0, 1, 2, 5, 10, 15, 30];
}

