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

    // Sanity limits enforced during preflight and upload. Generous relative to a real pruned
    // configuration database (typically well under 100 MB compressed), but absolute — a cap on
    // actual bytes written during extraction is a stronger zip-bomb defense than any
    // compression-ratio heuristic, since the archive's declared sizes can simply lie.
    public const long MaxArchiveSizeBytes = 100L * 1024 * 1024;
    public const long MaxDatabaseSizeBytes = 1024L * 1024 * 1024;
    public const long MaxMetadataEntrySizeBytes = 4L * 1024 * 1024;
    public const int MaxEntryCount = 8;
}
