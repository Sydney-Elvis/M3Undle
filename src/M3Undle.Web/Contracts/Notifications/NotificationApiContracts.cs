namespace M3Undle.Web.Contracts.Notifications;

// Safe contracts only: secrets appear as presence flags, never as values, and nothing here carries raw upstream text.

public sealed record NotificationsOverviewResponse(
    NotificationSettingsDto Settings,
    IReadOnlyList<NotificationDestinationDto> Destinations,
    IReadOnlyList<NotificationRouteDto> Routes,
    NotificationStatusSummaryDto Summary);

public sealed record NotificationSettingsDto(
    int Revision,
    bool SendingEnabled,
    bool Paused,
    bool RequiresActivation,
    bool SendingAllowed,
    int FailureDelayMinutes,
    int OverdueGraceMinutes,
    int ReminderIntervalHours,
    int CoverageWarnHours,
    int CoverageWarnPercent,
    int CoverageRecoverHours,
    int CoverageRecoverPercent,
    int CoverageGapMinutes,
    int RetentionDays);

public sealed record NotificationDestinationDto(
    string Kind,
    string DisplayName,
    bool Enabled,
    int ConfigRevision,
    string VerificationStatus,
    string? VerificationDetail,
    bool VerifiedCurrent,
    DateTime? VerifiedUtc,
    bool Complete,
    NotificationMatrixDto? Matrix,
    NotificationSmtpDto? Smtp);

public sealed record NotificationMatrixDto(
    string? HomeserverUrl,
    string? RoomId,
    bool HasAccessToken,
    string? BotUserId,
    string? DeviceId,
    bool AllowInsecureHttp,
    bool InsecureHttpPermitted);

public sealed record NotificationSmtpDto(
    string? Host,
    int Port,
    string TlsMode,
    string AuthMode,
    string? Username,
    bool HasPassword,
    string? SenderAddress,
    string? SenderName,
    IReadOnlyList<string> Recipients);

public sealed record NotificationRouteDto(
    string Key,
    string Label,
    string Trigger,
    string SubjectKind,
    bool IsIncident,
    bool Available,
    string? DestinationKind,
    int Revision,
    bool SendRecovery,
    bool SendReminders,
    int? FailureDelayMinutes,
    int? ReminderIntervalHours,
    string? Issue);

public sealed record NotificationStatusSummaryDto(
    int Pending,
    int RetryScheduled,
    int Failed,
    int Uncertain,
    bool CapacityReached,
    IReadOnlyDictionary<string, DateTime?> LastAcceptedUtcByKind);

public sealed record NotificationDeliveryDto(
    string Id,
    int Revision,
    string ProviderKind,
    string Target,
    string NotificationKey,
    string NotificationLabel,
    string Kind,
    string Title,
    string State,
    int AttemptCount,
    DateTime CreatedUtc,
    DateTime DueUtc,
    DateTime? AcceptedUtc,
    string? ErrorCode,
    string? ErrorText,
    string? SuppressedReason,
    bool CanRetry,
    bool CanDismiss,
    bool RetryNeedsAcknowledgement);

public sealed record NotificationDeliveryPageResponse(
    IReadOnlyList<NotificationDeliveryDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record NotificationSettingsRequest(
    int ExpectedRevision,
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

public sealed record NotificationRouteRequest(
    int ExpectedRevision,
    string? DestinationKind,
    bool SendRecovery = true,
    bool SendReminders = true,
    int? FailureDelayMinutes = null,
    int? ReminderIntervalHours = null);

public sealed record SmtpDestinationRequest(
    string? Host,
    int Port,
    string TlsMode,
    string AuthMode,
    string? Username,
    string? Password,
    bool ClearPassword,
    string? SenderAddress,
    string? SenderName,
    IReadOnlyList<string>? Recipients);

public sealed record MatrixDestinationRequest(
    string? HomeserverUrl,
    string? RoomId,
    string? AccessToken,
    bool ClearAccessToken,
    bool AllowInsecureHttp);

public sealed record NotificationDestinationRequest(
    int ExpectedRevision,
    SmtpDestinationRequest? Smtp,
    MatrixDestinationRequest? Matrix);

public sealed record NotificationRevisionRequest(int ExpectedRevision);

public sealed record NotificationEnabledRequest(int ExpectedRevision, bool Enabled);

public sealed record NotificationRetryRequest(int ExpectedRevision, bool AcknowledgeDuplicateRisk = false);

public sealed record NotificationChangeResponse(string? Message, int? Revision);

public sealed record NotificationTestResponse(
    string VerificationStatus,
    string Summary,
    IReadOnlyList<NotificationTestTargetDto> Targets,
    int? TestedRevision,
    bool VerificationApplied);

public sealed record NotificationTestTargetDto(string Label, string Outcome, string? ErrorCode);
