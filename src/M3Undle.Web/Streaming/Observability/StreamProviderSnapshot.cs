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
    string? ContentType);

