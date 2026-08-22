namespace M3Undle.Web.Application.Backup;

public static class SettingsArchiveFormat
{
    public const string Identifier = "m3undle-settings";
    public const string CurrentVersion = "1";
    public const int CurrentDocumentVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string DocumentEntryName = "settings.json";
}

public sealed record SettingsArchiveManifest
{
    public required string FormatIdentifier { get; init; }
    public required string FormatVersion { get; init; }
    public required int DocumentVersion { get; init; }
    public required string Scope { get; init; }
    public required string AppVersion { get; init; }
    public required string? SchemaVersion { get; init; }
    public required string BackupId { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required string? EncryptionKeyId { get; init; }
    public required string? EncryptionKeyFingerprint { get; init; }
    public required IReadOnlyList<string> SettingsEntities { get; init; }
    public required string SettingsSha256 { get; init; }
}

public sealed record SettingsDocument
{
    public int DocumentVersion { get; init; } = SettingsArchiveFormat.CurrentDocumentVersion;
    public required SettingsSiteSettings SiteSettings { get; init; }
    public IReadOnlyList<SettingsProvider> Providers { get; init; } = [];
    public IReadOnlyList<SettingsProfile> Profiles { get; init; } = [];
    public IReadOnlyList<SettingsProfileProvider> ProfileProviders { get; init; } = [];
    public IReadOnlyList<SettingsDownstreamIntegration> DownstreamIntegrations { get; init; } = [];
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
