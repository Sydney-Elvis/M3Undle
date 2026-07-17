namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Restore progresses through these states in order, except that a failure at any point after
/// <see cref="CheckpointCreated"/> moves to <see cref="RolledBack"/> instead of continuing.
/// See .ai_docs/M3Undle_Backup_Restore_Implementation_Plan.md §7.5.
/// </summary>
public enum RestoreState
{
    None,
    Requested,
    CheckpointCreated,
    Completed,
    Failed,
    RolledBack,
}
