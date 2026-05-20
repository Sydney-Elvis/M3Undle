using M3Undle.Web.Streaming.Configuration;

namespace M3Undle.Web.Streaming.Observability;

public enum StreamChannelHealthProfile
{
    Fast = 0,
    Cautious = 1,
    Unstable = 2,
}

public sealed record StreamChannelRecoveryPolicy(
    StreamChannelHealthProfile Profile,
    TimeSpan RecoveryOutputHoldLimit,
    int RecoverySafeStartSearchLimitBytes,
    bool AllowPacketBoundaryRecoveryFallback,
    bool RequireDownstreamRetune,
    string? DownstreamRetuneReason,
    string Reason)
{
    public static StreamChannelRecoveryPolicy FromOptions(ReconnectOptions options)
        => new(
            StreamChannelHealthProfile.Fast,
            options.RecoveryOutputHoldLimit,
            options.RecoverySafeStartSearchLimitBytes,
            options.AllowPacketBoundaryRecoveryFallback,
            RequireDownstreamRetune: false,
            DownstreamRetuneReason: null,
            "No channel health history requires adaptive recovery.");
}

public interface IStreamChannelHealthProfileService
{
    Task<StreamChannelRecoveryPolicy> GetRecoveryPolicyAsync(
        string providerId,
        string providerChannelId,
        ReconnectOptions reconnectOptions,
        CancellationToken ct = default);
}

public sealed class NoopStreamChannelHealthProfileService : IStreamChannelHealthProfileService
{
    public static NoopStreamChannelHealthProfileService Instance { get; } = new();

    private NoopStreamChannelHealthProfileService()
    {
    }

    public Task<StreamChannelRecoveryPolicy> GetRecoveryPolicyAsync(
        string providerId,
        string providerChannelId,
        ReconnectOptions reconnectOptions,
        CancellationToken ct = default)
        => Task.FromResult(StreamChannelRecoveryPolicy.FromOptions(reconnectOptions));
}
