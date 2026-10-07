namespace M3Undle.Web.Data.Entities;

public static class NotificationProviderKinds
{
    public const string Matrix = "matrix";
    public const string Smtp = "smtp";
}

public static class NotificationDeliveryStates
{
    public const string Pending = "Pending";
    public const string Claimed = "Claimed";
    public const string RetryScheduled = "RetryScheduled";
    public const string Accepted = "Accepted";
    public const string Failed = "Failed";
    public const string Uncertain = "Uncertain";
    public const string Suppressed = "Suppressed";
    public const string Dismissed = "Dismissed";
}

public static class NotificationOccurrenceKinds
{
    public const string Opening = "Opening";
    public const string Reminder = "Reminder";
    public const string Recovery = "Recovery";
    public const string OneTime = "OneTime";
    public const string ResolvedSummary = "ResolvedSummary";
}

public static class NotificationIncidentStates
{
    public const string Active = "Active";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";
}

public static class NotificationVerificationStates
{
    public const string Unverified = "Unverified";
    public const string Verified = "Verified";
    public const string Partial = "Partial";
    public const string Failed = "Failed";
    public const string Uncertain = "Uncertain";
}

/// <summary>Singleton (id 1): global send gate, activation epoch and policy defaults.</summary>
public sealed class NotificationSettings
{
    public int Id { get; set; } = 1;
    public bool SendingEnabled { get; set; }
    public bool Paused { get; set; }
    public bool RequiresActivation { get; set; }
    public int ActivationEpoch { get; set; }
    public int FailureDelayMinutes { get; set; } = 10;
    public int OverdueGraceMinutes { get; set; } = 15;
    public int ReminderIntervalHours { get; set; } = 6;
    public int CoverageWarnHours { get; set; } = 12;
    public int CoverageWarnPercent { get; set; } = 90;
    public int CoverageRecoverHours { get; set; } = 14;
    public int CoverageRecoverPercent { get; set; } = 95;
    public int CoverageGapMinutes { get; set; } = 30;
    public int RetentionDays { get; set; } = 30;

    /// <summary>Per-instance secret used only to group failed sign-ins by account without storing what was typed.</summary>
    public string IdentifierSalt { get; set; } = string.Empty;

    public int Revision { get; set; } = 1;

    /// <summary>Set while the nonterminal-delivery ceiling is rejecting materialization; cleared when space returns.</summary>
    public DateTime? CapacitySuppressedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

public sealed class NotificationDestination
{
    public string DestinationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>Bumped by any change to configuration, targets or verification inputs.</summary>
    public int ConfigRevision { get; set; } = 1;

    /// <summary>Bumped only when the actual delivery identity (room, recipients, sender, credential) changes.</summary>
    public int DeliveryIdentityRevision { get; set; } = 1;

    public int? VerifiedRevision { get; set; }
    public DateTime? VerifiedUtc { get; set; }
    public string VerificationStatus { get; set; } = NotificationVerificationStates.Unverified;
    public string? VerificationDetail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public NotificationMatrixSettings? Matrix { get; set; }
    public NotificationSmtpSettings? Smtp { get; set; }
    public ICollection<NotificationEmailRecipient> Recipients { get; set; } = new List<NotificationEmailRecipient>();
}

public sealed class NotificationMatrixSettings
{
    public string DestinationId { get; set; } = string.Empty;
    public string? HomeserverUrl { get; set; }
    public string? RoomId { get; set; }
    public string? AccessTokenEncrypted { get; set; }
    public string? BotUserId { get; set; }
    public string? DeviceId { get; set; }

    /// <summary>Isolated-lab only; production configuration requires HTTPS.</summary>
    public bool AllowInsecureHttp { get; set; }

    public NotificationDestination Destination { get; set; } = null!;
}

public sealed class NotificationSmtpSettings
{
    public string DestinationId { get; set; } = string.Empty;
    public string? Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>starttls | tls</summary>
    public string TlsMode { get; set; } = "starttls";

    /// <summary>none | password</summary>
    public string AuthMode { get; set; } = "password";

    public string? Username { get; set; }
    public string? PasswordEncrypted { get; set; }
    public string? SenderAddress { get; set; }
    public string? SenderName { get; set; }

    public NotificationDestination Destination { get; set; } = null!;
}

public sealed class NotificationEmailRecipient
{
    public string RecipientId { get; set; } = string.Empty;
    public string DestinationId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;

    /// <summary>Canonical identity used for uniqueness; the displayed/sent address keeps its original case.</summary>
    public string CanonicalKey { get; set; } = string.Empty;

    public int SortOrder { get; set; }
    public NotificationDestination Destination { get; set; } = null!;
}

/// <summary>One row per catalog key. A null destination means external delivery is Off.</summary>
public sealed class NotificationRoute
{
    public string NotificationKey { get; set; } = string.Empty;
    public string? DestinationId { get; set; }
    public int Revision { get; set; } = 1;
    public bool SendRecovery { get; set; } = true;
    public bool SendReminders { get; set; } = true;
    public int? FailureDelayMinutes { get; set; }
    public int? ReminderIntervalHours { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public NotificationDestination? Destination { get; set; }
}

/// <summary>Ordered, immutable evidence about a subject, staged in the same commit as its source outcome.</summary>
public sealed class NotificationConditionObservation
{
    public long ObservationId { get; set; }

    /// <summary>Evidence stream, e.g. <c>epg.source_check</c>.</summary>
    public string EvidenceKey { get; set; } = string.Empty;
    public string SubjectKind { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public DateTime CompletedUtc { get; set; }

    /// <summary>ok | unchanged | failed for real checks; other producers define their own.</summary>
    public string Outcome { get; set; } = string.Empty;
    public string? SafeDetail { get; set; }
    public bool Consumed { get; set; }
    public DateTime? ConsumedUtc { get; set; }
}

public sealed class NotificationIncident
{
    public string IncidentId { get; set; } = string.Empty;
    public string NotificationKey { get; set; } = string.Empty;
    public string SubjectKind { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string? SubjectLabel { get; set; }
    public int Generation { get; set; } = 1;
    public string State { get; set; } = NotificationIncidentStates.Active;
    public string Severity { get; set; } = "Warning";
    public DateTime FirstUnhealthyUtc { get; set; }
    public DateTime LastObservedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public string? Reason { get; set; }
    public string? SafeDetail { get; set; }

    /// <summary>Consecutive evaluations that met recovery thresholds; used for hysteresis.</summary>
    public int ConsecutiveHealthy { get; set; }

    /// <summary>When the condition first counted as sustained (delay elapsed or critical), whether or not anything was sent.</summary>
    public DateTime? OpenedUtc { get; set; }

    public DateTime? LastReminderUtc { get; set; }
    public int ReminderSequence { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class NotificationOccurrence
{
    public string OccurrenceId { get; set; } = string.Empty;

    /// <summary>Producer-supplied identity; unique, so a transition can be captured at most once.</summary>
    public string OccurrenceKey { get; set; } = string.Empty;
    public string NotificationKey { get; set; } = string.Empty;
    public string? IncidentId { get; set; }
    public int? Generation { get; set; }
    public string Kind { get; set; } = NotificationOccurrenceKinds.OneTime;
    public int Sequence { get; set; }
    public int PolicyRevision { get; set; }
    public int ActivationEpoch { get; set; }
    public string Severity { get; set; } = "Info";
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? SubjectLabel { get; set; }
    public string? LinkPath { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Set once deliveries (or a deliberate none) have been materialized from current routing.</summary>
    public DateTime? MaterializedUtc { get; set; }
}

/// <summary>Per target/generation record of whether an opening was accepted; survives history cleanup.</summary>
public sealed class NotificationIncidentTarget
{
    public string IncidentTargetId { get; set; } = string.Empty;
    public string IncidentId { get; set; } = string.Empty;
    public int Generation { get; set; }
    public string DestinationId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public int DeliveryIdentityRevision { get; set; }
    public DateTime? OpeningAcceptedUtc { get; set; }
    public bool OpeningUncertain { get; set; }
    public DateTime? RecoveryQueuedUtc { get; set; }
    public DateTime? RecoveryAcceptedUtc { get; set; }
    public int LastReminderSequence { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public NotificationIncident Incident { get; set; } = null!;
}

public sealed class NotificationDelivery
{
    public string DeliveryId { get; set; } = string.Empty;
    public string OccurrenceId { get; set; } = string.Empty;
    public string DestinationId { get; set; } = string.Empty;
    public string ProviderKind { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string TargetLabel { get; set; } = string.Empty;
    public int DeliveryIdentityRevision { get; set; }
    public int ConfigRevision { get; set; }
    public int RouteRevision { get; set; }
    public string State { get; set; } = NotificationDeliveryStates.Pending;
    public int AttemptCount { get; set; }
    public int CycleNumber { get; set; } = 1;
    public DateTime DueUtc { get; set; }
    public string? ClaimOwner { get; set; }
    public DateTime? ClaimExpiresUtc { get; set; }
    public DateTime? TransportStartedUtc { get; set; }
    public DateTime? AcceptedUtc { get; set; }
    public string? RemoteReference { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorText { get; set; }
    public string? SuppressedReason { get; set; }
    public string PayloadTitle { get; set; } = string.Empty;
    public string PayloadBody { get; set; } = string.Empty;
    public string? PayloadLinkPath { get; set; }

    /// <summary>Stable per delivery; correlation only, not deduplication.</summary>
    public string MessageId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? DismissedUtc { get; set; }

    /// <summary>Application-managed concurrency token; SQLite has no rowversion.</summary>
    public int Revision { get; set; } = 1;

    public NotificationOccurrence Occurrence { get; set; } = null!;
    public NotificationDestination Destination { get; set; } = null!;
}

/// <summary>Bounded per-channel facts needed to evaluate future guide coverage without re-parsing the cache.</summary>
public sealed class EpgNotificationCoverage
{
    public string EpgNotificationCoverageId { get; set; } = string.Empty;
    public string EpgSourceId { get; set; } = string.Empty;
    public string XmltvChannelId { get; set; } = string.Empty;
    public bool IsRelevant { get; set; }
    public string? RelevanceContext { get; set; }

    /// <summary>Normalized, merged programme intervals as "startUnixSeconds-stopUnixSeconds;..." (bounded).</summary>
    public string IntervalsEncoded { get; set; } = string.Empty;
    public int IntervalCount { get; set; }
    public string? EvidenceRevision { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
