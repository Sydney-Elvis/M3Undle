namespace M3Undle.Web.Application.Notifications;

public enum NotificationOperationStatus
{
    Ok,
    NotFound,

    /// <summary>A stale revision, or the target is in a state that does not allow the action.</summary>
    Conflict,
    Invalid,
    RateLimited,
}

public sealed record NotificationOperationResult(
    NotificationOperationStatus Status,
    string? Message = null,
    IDictionary<string, string[]>? Errors = null,
    int? Revision = null,
    TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Status == NotificationOperationStatus.Ok;
}

/// <summary>An empty password means "keep the stored one"; <see cref="ClearPassword"/> is the only way to remove it.</summary>
public sealed record SmtpSetupRequest(
    string? Host,
    int Port,
    string TlsMode,
    string AuthMode,
    string? Username,
    string? Password,
    bool ClearPassword,
    string? SenderAddress,
    string? SenderName,
    IReadOnlyList<string> Recipients);

public sealed record MatrixSetupRequest(
    string? HomeserverUrl,
    string? RoomId,
    string? AccessToken,
    bool ClearAccessToken,
    bool AllowInsecureHttp);

public sealed record NotificationSettingsUpdate(
    bool SendingEnabled,
    bool Paused,
    int FailureDelayMinutes,
    int OverdueGraceMinutes,
    int ReminderIntervalHours,
    int CoverageWarnHours,
    int CoverageWarnPercent,
    int CoverageRecoverHours,
    int CoverageRecoverPercent,
    int CoverageGapMinutes,
    int RetentionDays);

public sealed record NotificationRouteUpdate(
    string? DestinationKind,
    bool SendRecovery,
    bool SendReminders,
    int? FailureDelayMinutes,
    int? ReminderIntervalHours);

public sealed record NotificationTargetTestResult(string Label, string Outcome, string? ErrorCode);

/// <summary>Safe outcome of a real test send. Only transport acceptance is claimed, never receipt.</summary>
public sealed record NotificationTestResult(
    NotificationOperationStatus Status,
    string VerificationStatus,
    string Summary,
    IReadOnlyList<NotificationTargetTestResult> Targets,
    int? TestedRevision,
    bool VerificationApplied,
    TimeSpan? RetryAfter = null);
