namespace M3Undle.Web.Application.Backup;

/// <summary>
/// The manifest.json entry packaged inside every .m3undle-backup archive. Read during restore
/// preflight before the archive is trusted with anything else — format version, schema version,
/// and the database checksum all gate whether the rest of the archive is even opened.
/// </summary>
public sealed record PortableBackupManifest
{
    public required string FormatIdentifier { get; init; }
    public required string FormatVersion { get; init; }
    public required string AppVersion { get; init; }
    public required string? SchemaVersion { get; init; }
    public required string BackupId { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required string? EncryptionKeyId { get; init; }
    public required string? EncryptionKeyFingerprint { get; init; }
    public required IReadOnlyList<string> ExcludedTables { get; init; }
    public required string DatabaseSha256 { get; init; }
}
