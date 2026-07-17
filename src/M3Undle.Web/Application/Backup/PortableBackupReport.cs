namespace M3Undle.Web.Application.Backup;

/// <summary>The backup-report.json entry packaged inside every .m3undle-backup archive.</summary>
public sealed record PortableBackupReport
{
    public required string BackupId { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required IReadOnlyDictionary<string, int> RowsRemovedByTable { get; init; }
    public required long DatabaseSizeBytes { get; init; }
    public required double DurationSeconds { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>One row in the backup list — enough to render the UI table without opening the archive.</summary>
public sealed record PortableBackupSummary(string FileName, long SizeBytes, DateTime CreatedUtc);

public sealed record PortableBackupResult(
    bool Success,
    string? ErrorMessage,
    string? FilePath,
    PortableBackupManifest? Manifest,
    PortableBackupReport? Report)
{
    public static PortableBackupResult Failed(string errorMessage) => new(false, errorMessage, null, null, null);

    public static PortableBackupResult Succeeded(string filePath, PortableBackupManifest manifest, PortableBackupReport report)
        => new(true, null, filePath, manifest, report);
}
