namespace M3Undle.Web.Application.Backup;

/// <summary>Result of validating an archive without mutating anything — see plan §7.2.</summary>
public sealed record PortableRestorePreflightResult(
    bool Success,
    IReadOnlyList<string> Errors,
    PortableBackupManifest? Manifest)
{
    public static PortableRestorePreflightResult Failed(IReadOnlyList<string> errors, PortableBackupManifest? manifest = null)
        => new(false, errors, manifest);

    public static PortableRestorePreflightResult Succeeded(PortableBackupManifest manifest)
        => new(true, [], manifest);
}

/// <summary>Result of staging a restore request (preflight + writing the Requested marker).</summary>
public sealed record PortableRestoreStageResult(bool Success, IReadOnlyList<string> Errors, PortableBackupManifest? Manifest)
{
    public static PortableRestoreStageResult Failed(IReadOnlyList<string> errors) => new(false, errors, null);
    public static PortableRestoreStageResult Succeeded(PortableBackupManifest manifest) => new(true, [], manifest);
}

/// <summary>Result of actually applying a restore — see plan §7.6-§7.7.</summary>
public sealed record PortableRestoreResult(bool Success, string? ErrorMessage, bool RolledBack, string? BackupId)
{
    public static PortableRestoreResult Failed(string errorMessage, bool rolledBack) => new(false, errorMessage, rolledBack, null);
    public static PortableRestoreResult Succeeded(string backupId) => new(true, null, false, backupId);
}
