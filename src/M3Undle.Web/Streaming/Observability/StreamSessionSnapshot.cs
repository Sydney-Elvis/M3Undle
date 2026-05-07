using M3Undle.Web.Streaming.Models;

namespace M3Undle.Web.Streaming.Observability;

public sealed record StreamSessionSnapshot(
    string SessionId,
    string ProviderId,
    string ProviderChannelId,
    string DisplayName,
    SessionState State,
    int SubscriberCount,
    bool IsShared,
    int BufferUsedBytes,
    int BufferMaxBytes,
    DateTimeOffset StartedUtc,
    DateTimeOffset? LastUpstreamByteUtc,
    int ReconnectAttempts,
    string? LastFailureKind,
    bool IsInternal = false,
    string? ParentStreamSessionId = null,
    int InferredHlsSubscriberCount = 0,
    double? FirstByteLatencyMs = null,
    long BytesSinceReconnect = 0,
    string? LastDisconnectReason = null,
    string? LastStopTrigger = null,
    int? LastUpstreamStatusCode = null,
    double? LastCooldownSeconds = null,
    string RelayMode = "Direct",
    string? LastRelayFallbackReason = null);
