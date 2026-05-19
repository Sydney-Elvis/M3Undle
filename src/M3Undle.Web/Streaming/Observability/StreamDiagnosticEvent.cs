using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Subscribers;

namespace M3Undle.Web.Streaming.Observability;

public sealed record StreamDiagnosticEvent(
    string EventId,
    DateTimeOffset TimestampUtc,
    StreamDiagnosticEventKind Kind,
    string? SessionId = null,
    string? ProviderId = null,
    string? ProviderChannelId = null,
    string? DisplayName = null,
    string? RequestedRoute = null,
    string? RouteClassification = null,
    string? ClientId = null,
    string? RemoteIp = null,
    string? UserAgent = null,
    int? HttpStatusCode = null,
    UpstreamFailureKind? UpstreamFailureKind = null,
    int? ReconnectAttempt = null,
    double? ReconnectDelayMs = null,
    double? FirstByteLatencyMs = null,
    long? BytesSinceReconnect = null,
    long? TotalBytesRelayed = null,
    long? BytesSent = null,
    int? QueueDepth = null,
    SubscriberDisconnectReason? DisconnectReason = null,
    string? StopTrigger = null,
    double? CooldownSeconds = null,
    int? RetryAfterSeconds = null,
    double? OutputHeldMs = null,
    double? RecoveryDurationMs = null,
    string? SafeStartKind = null,
    long? BytesSuppressed = null,
    double? RecoveryHoldLimitMs = null,
    double? ClientAbortAfterRecoveryDelayMs = null,
    string? RelayMode = null,
    string? Message = null);
