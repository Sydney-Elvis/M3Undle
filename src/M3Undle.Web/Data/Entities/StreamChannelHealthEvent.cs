namespace M3Undle.Web.Data.Entities;

public sealed class StreamChannelHealthEvent
{
    public string StreamChannelHealthEventId { get; set; } = null!;
    public string ProviderId { get; set; } = null!;
    public string ProviderChannelId { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string EventKind { get; set; } = null!;
    public DateTime EventUtc { get; set; }
    public string? SessionId { get; set; }
    public string? RelayMode { get; set; }
    public string? RouteClassification { get; set; }
    public string? UpstreamFailureKind { get; set; }
    public int? ReconnectAttempt { get; set; }
    public double? StallDurationMs { get; set; }
    public double? RecoveryDurationMs { get; set; }
    public double? SafeStartWaitMs { get; set; }
    public double? OutputHeldMs { get; set; }
    public string? SafeStartKind { get; set; }
    public string? ClientDisconnectReason { get; set; }
    public bool ClientAbortAfterRecovery { get; set; }
    public double? ClientAbortAfterRecoveryDelayMs { get; set; }
    public bool ForcedRetune { get; set; }
    public bool TsSyncLoss { get; set; }
    public long? BytesSuppressed { get; set; }
    public double? CleanWatchDurationMs { get; set; }
}
