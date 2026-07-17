namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Format constants shared between backup creation and restore preflight — kept in one place so
/// the two sides can never drift apart on what identifies a valid, supported archive.
/// </summary>
public static class PortableBackupFormat
{
    public const string Identifier = "m3undle-backup";
    public const string CurrentVersion = "1";
    public const string ArchiveExtension = ".m3undle-backup";
}
