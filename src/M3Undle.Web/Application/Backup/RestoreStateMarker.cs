namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Tracks restore progress across the process restart between staging and applying a restore.
/// Deliberately a plain file, not a database row: the database itself is what a restore
/// replaces, so state describing the restore can't live inside it.
/// </summary>
public sealed record RestoreStateMarker
{
    public required RestoreState State { get; init; }
    public required string BackupId { get; init; }
    public required string ArchiveFileName { get; init; }
    public required DateTime UpdatedUtc { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RollbackCheckpointFileName { get; init; }

    /// <summary>
    /// Set only for restores triggered by M3UNDLE_RESTORE_FILE. Lets the one-shot check compare
    /// against the current environment variable value without needing a separate marker file —
    /// a normal container restart that leaves the variable set must not re-trigger the same restore.
    /// </summary>
    public string? SourceEnvironmentPath { get; init; }
}
