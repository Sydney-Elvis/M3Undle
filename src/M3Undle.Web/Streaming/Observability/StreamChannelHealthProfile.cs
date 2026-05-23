using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Upstream;

namespace M3Undle.Web.Streaming.Observability;

public enum StreamChannelHealthProfile
{
    Stable = 0,
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
            StreamChannelHealthProfile.Stable,
            options.RecoveryOutputHoldLimit,
            options.RecoverySafeStartSearchLimitBytes,
            options.AllowPacketBoundaryRecoveryFallback,
            RequireDownstreamRetune: false,
            DownstreamRetuneReason: null,
            "No channel health history requires adaptive recovery.");
}

public sealed record StreamRelayPolicyDecision(
    string ProviderRelayPolicy,
    string SelectedRelayMode,
    string Reason)
{
    public static StreamRelayPolicyDecision Direct(string providerRelayPolicy, string reason)
        => new(providerRelayPolicy, UpstreamRelayModes.Direct, reason);

    public static StreamRelayPolicyDecision CleanRemux(string providerRelayPolicy, string reason)
        => new(providerRelayPolicy, UpstreamRelayModes.FfmpegCleanRemux, reason);
}

public sealed record StreamChannelHealthEvidence(
    string ProviderId,
    string ProviderChannelId,
    StreamChannelRecoveryPolicy RecoveryPolicy,
    StreamRelayPolicyDecision AutoRelayDecision,
    int UpstreamFailures,
    int RecoveryResumes,
    int FallbackRecoveryResumes,
    int IdrRecoveryResumes,
    int ClientAbortAfterRecovery,
    int ForcedRetunes,
    int TsSyncLoss,
    int CleanWatchEvents,
    TimeSpan CleanWatchDuration,
    DateTime? LastAdverseEventUtc,
    DateTime? LastCleanWatchUtc,
    StreamChannelHealthTrendResult Trend);

public interface IStreamChannelHealthProfileService
{
    Task<StreamChannelRecoveryPolicy> GetRecoveryPolicyAsync(
        string providerId,
        string providerChannelId,
        ReconnectOptions reconnectOptions,
        CancellationToken ct = default);

    StreamRelayPolicyDecision GetRelayPolicyDecision(
        string providerRelayPolicy,
        StreamChannelRecoveryPolicy recoveryPolicy);

    Task<StreamChannelHealthEvidence> GetEvidenceAsync(
        string providerId,
        string providerChannelId,
        ReconnectOptions reconnectOptions,
        CancellationToken ct = default);

    void Invalidate(string providerId, string providerChannelId);
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

    public StreamRelayPolicyDecision GetRelayPolicyDecision(
        string providerRelayPolicy,
        StreamChannelRecoveryPolicy recoveryPolicy)
        => StreamRelayPolicyDecision.Direct(Configuration.CleanRelayModes.Normalize(providerRelayPolicy),
            "No channel health profile service is available; using direct relay.");

    public Task<StreamChannelHealthEvidence> GetEvidenceAsync(
        string providerId,
        string providerChannelId,
        ReconnectOptions reconnectOptions,
        CancellationToken ct = default)
    {
        var policy = StreamChannelRecoveryPolicy.FromOptions(reconnectOptions);
        return Task.FromResult(new StreamChannelHealthEvidence(
            providerId,
            providerChannelId,
            policy,
            GetRelayPolicyDecision(Configuration.CleanRelayModes.Auto, policy),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            TimeSpan.Zero,
            LastAdverseEventUtc: null,
            LastCleanWatchUtc: null,
            StreamChannelHealthTrendResult.Insufficient("No channel health profile service is available.")));
    }

    public void Invalidate(string providerId, string providerChannelId)
    {
    }
}
