namespace M3Undle.Web.Application.Backup;

public static class SettingsArchiveFormat
{
    public const string Identifier = "m3undle-settings";
    public const int CurrentVersion = 2;

    /// <summary>Document 2 adds notification configuration. The encrypted envelope stays at version 2.</summary>
    public const int CurrentDocumentVersion = 2;

    public static bool IsSupportedDocumentVersion(int version) => version is 1 or 2;
}

public sealed record SettingsArchiveHeader
{
    public required string FormatIdentifier { get; init; }
    public required int FormatVersion { get; init; }
    public required string Scope { get; init; }
    public required string Kdf { get; init; }
    public required int MemoryKiB { get; init; }
    public required int Iterations { get; init; }
    public required int Parallelism { get; init; }
    public required string Salt { get; init; }
    public required string Nonce { get; init; }
}

public sealed record EncryptedSettingsArchive
{
    public required SettingsArchiveHeader Header { get; init; }
    public required string Ciphertext { get; init; }
    public required string Tag { get; init; }
}

public sealed record SettingsArchiveManifest
{
    public required string FormatIdentifier { get; init; }
    public required int FormatVersion { get; init; }
    public required int DocumentVersion { get; init; }
    public required string Scope { get; init; }
    public required string AppVersion { get; init; }
    public required string? SchemaVersion { get; init; }
    public required string BackupId { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required string? EncryptionKeyId { get; init; }
    public required string? EncryptionKeyFingerprint { get; init; }
    public required IReadOnlyList<string> SettingsEntities { get; init; }
}

public sealed record SettingsArchivePayload
{
    public required SettingsArchiveManifest Manifest { get; init; }
    public required SettingsDocument Document { get; init; }
}

public sealed record SettingsDocument
{
    public int DocumentVersion { get; init; } = SettingsArchiveFormat.CurrentDocumentVersion;
    public required SettingsSiteSettings SiteSettings { get; init; }
    public IReadOnlyList<SettingsProvider> Providers { get; init; } = [];
    public IReadOnlyList<SettingsProfile> Profiles { get; init; } = [];
    public IReadOnlyList<SettingsProfileProvider> ProfileProviders { get; init; } = [];
    public IReadOnlyList<SettingsDownstreamIntegration> DownstreamIntegrations { get; init; } = [];

    /// <summary>Absent in version 1 documents, which import as disabled and empty notifications.</summary>
    public SettingsNotifications? Notifications { get; init; }
}

/// <summary>
/// Notification configuration as user intent. Verification state, delivery history, incidents and the activation epoch
/// are deliberately absent: an imported setup must be re-tested and explicitly resumed before it can send anything.
/// </summary>
public sealed record SettingsNotifications
{
    public bool SendingEnabled { get; init; }
    public SettingsNotificationPolicy Policy { get; init; } = new();
    public IReadOnlyList<SettingsNotificationDestination> Destinations { get; init; } = [];
    public IReadOnlyList<SettingsNotificationRoute> Routes { get; init; } = [];
}

public sealed record SettingsNotificationPolicy
{
    public int FailureDelayMinutes { get; init; } = 10;
    public int OverdueGraceMinutes { get; init; } = 15;
    public int ReminderIntervalHours { get; init; } = 6;
    public int CoverageWarnHours { get; init; } = 12;
    public int CoverageWarnPercent { get; init; } = 90;
    public int CoverageRecoverHours { get; init; } = 14;
    public int CoverageRecoverPercent { get; init; } = 95;
    public int CoverageGapMinutes { get; init; } = 30;
    public int RetentionDays { get; init; } = 30;
}

public sealed record SettingsNotificationDestination
{
    public required string SourceId { get; init; }
    public required string Kind { get; init; }
    public bool Enabled { get; init; }
    public string? HomeserverUrl { get; init; }
    public string? RoomId { get; init; }
    public string? AccessTokenEncrypted { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public string? TlsMode { get; init; }
    public string? AuthMode { get; init; }
    public string? Username { get; init; }
    public string? PasswordEncrypted { get; init; }
    public string? SenderAddress { get; init; }
    public string? SenderName { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];
}

public sealed record SettingsNotificationRoute
{
    public required string NotificationKey { get; init; }
    public string? DestinationSourceId { get; init; }
    public bool SendRecovery { get; init; } = true;
    public bool SendReminders { get; init; } = true;
    public int? FailureDelayMinutes { get; init; }
    public int? ReminderIntervalHours { get; init; }
}

public sealed record SettingsSiteSettings
{
    public bool StreamingEnabled { get; init; }
    public int StreamMaxConcurrentSessions { get; init; }
    public int StreamIdleGraceSeconds { get; init; }
    public int StreamIdleGraceHardCapSeconds { get; init; }
    public int StreamBufferMaxBytesPerSession { get; init; }
    public int StreamBufferMaxBytesHardCap { get; init; }
    public int StreamBufferReadChunkSizeBytes { get; init; }
    public int StreamReconnectReadStallTimeoutSeconds { get; init; }
    public int StreamReconnectOutageWindowSeconds { get; init; }
    public int StreamReconnectConnectTimeoutSeconds { get; init; }
    public bool HdhrEnabled { get; init; }
    public int? HdhrTunerCountOverride { get; init; }
    public string? HdhrAdvertisedBaseUrl { get; init; }
    public bool HdhrDiscoveryEnabled { get; init; }
    public bool HdhrSsdpEnabled { get; init; }
    public bool HdhrSiliconDustDiscoveryEnabled { get; init; }
    public string? HdhrFriendlyName { get; init; }
    public string? HdhrAllowedNetworks { get; init; }
    public bool GeneratedHlsEnabled { get; init; }
    public string? GeneratedHlsFfmpegPath { get; init; }
    public required string RefreshScheduleKind { get; init; }
    public bool RefreshStartupCatchup { get; init; }
    public int EventRetentionDays { get; init; }
    public bool ObservabilityMetricsEnabled { get; init; }
    public required string ObservabilityMetricsMode { get; init; }
    public bool ObservabilityMetricsEnableChannelLabels { get; init; }
    public string? ObservabilityMetricsLocalAllowedCidrs { get; init; }
    public bool XtreamCompatibilityEnabled { get; init; }
}

public sealed record SettingsProvider
{
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public required string PlaylistUrl { get; init; }
    public string? XmltvUrl { get; init; }
    public string? HeadersJson { get; init; }
    public string? UserAgent { get; init; }
    public int TimeoutSeconds { get; init; }
    public int? MaxConcurrentStreams { get; init; }
    public bool IncludeVod { get; init; }
    public bool IncludeSeries { get; init; }
    public bool ForceMpegTs { get; init; }
    public required string CleanRelayMode { get; init; }
    public string? XtreamBaseUrl { get; init; }
    public string? XtreamUsername { get; init; }
    public string? XtreamEncryptedPassword { get; init; }
    public bool XtreamIncludeXmltv { get; init; }
}

public sealed record SettingsProfile
{
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public bool IsActive { get; init; }
    public required string OutputName { get; init; }
    public required string MergeMode { get; init; }
    public string? RefreshScheduleKindOverride { get; init; }
    public bool? RefreshStartupCatchupOverride { get; init; }
}

public sealed record SettingsProfileProvider
{
    public required string ProfileSourceId { get; init; }
    public required string ProviderSourceId { get; init; }
    public int Priority { get; init; }
    public bool Enabled { get; init; }
}

public sealed record SettingsDownstreamIntegration
{
    public required string SourceId { get; init; }
    public string? ProfileSourceId { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string BaseUrl { get; init; }
    public string? ApiKeyEncrypted { get; init; }
    public string? WebhookHeadersJson { get; init; }
    public bool TriggerOnLineupUpdate { get; init; }
    public bool TriggerOnGuideUpdate { get; init; }
    public bool Enabled { get; init; }
}

public sealed record SettingsArchivePreflightResult(
    bool IsSettingsArchive,
    bool Success,
    IReadOnlyList<string> Errors,
    SettingsArchiveManifest? Manifest,
    SettingsDocument? Document)
{
    public static SettingsArchivePreflightResult NotSettingsArchive() => new(false, false, [], null, null);
    public static SettingsArchivePreflightResult Failed(IReadOnlyList<string> errors, SettingsArchiveManifest? manifest = null)
        => new(true, false, errors, manifest, null);
    public static SettingsArchivePreflightResult Succeeded(SettingsArchiveManifest manifest, SettingsDocument document)
        => new(true, true, [], manifest, document);
}

public sealed record SettingsArchiveResult(
    bool Success,
    string? ErrorMessage,
    string? FilePath,
    SettingsArchiveManifest? Manifest)
{
    public static SettingsArchiveResult Failed(string errorMessage) => new(false, errorMessage, null, null);
    public static SettingsArchiveResult Succeeded(string filePath, SettingsArchiveManifest manifest) => new(true, null, filePath, manifest);
}

public sealed record SettingsImportResult(bool Success, IReadOnlyList<string> Errors, IReadOnlyDictionary<string, int> AppliedCounts)
{
    public static SettingsImportResult Failed(params string[] errors) => new(false, errors, new Dictionary<string, int>());
    public static SettingsImportResult Succeeded(IReadOnlyDictionary<string, int> appliedCounts) => new(true, [], appliedCounts);
}
