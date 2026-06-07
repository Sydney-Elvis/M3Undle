using M3Undle.Web.Streaming.Models;

namespace M3Undle.Web.Streaming.Observability;

public sealed record StreamProviderSnapshot(
    string SessionId,
    string ProviderId,
    string ProviderChannelId,
    SessionState State,
    DateTimeOffset? LastUpstreamByteUtc,
    int ReconnectAttempts,
    string? LastFailureKind,
    string? ContentType,
    double? FirstByteLatencyMs = null,
    long BytesSinceReconnect = 0,
    int? LastUpstreamStatusCode = null,
    double? LastCooldownSeconds = null,
    string RelayMode = "Direct",
    string? LastRelayFallbackReason = null,
    string? RelayPolicy = null,
    string? RelayDecisionReason = null,
    string? LastSafeStartKind = null,
    double? LastRecoveryOutputHeldMs = null,
    DateTimeOffset? LastRecoveryStartedUtc = null);
