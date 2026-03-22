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
    string? LastFailureKind);

