namespace M3Undle.Web.Contracts;

public sealed class BackupSummaryResponse
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class CreateBackupResponse
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? AppVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public IReadOnlyDictionary<string, int> RowsRemovedByTable { get; set; } = new Dictionary<string, int>();
    public double DurationSeconds { get; set; }
}

public sealed class ValidateBackupResponse
{
    public bool Success { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = [];
    public string? BackupId { get; set; }
    public string? AppVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public DateTime? CreatedUtc { get; set; }
}

public sealed class BackupScheduleResponse
{
    public bool Enabled { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
}

public sealed class SetBackupScheduleRequest
{
    public bool Enabled { get; set; }
}

public sealed class StageRestoreRequest
{
    public string FileName { get; set; } = string.Empty;
}

public sealed class RestoreStatusResponse
{
    public string State { get; set; } = "None";
    public string? BackupId { get; set; }
    public string? ArchiveFileName { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public string? ErrorMessage { get; set; }
}
