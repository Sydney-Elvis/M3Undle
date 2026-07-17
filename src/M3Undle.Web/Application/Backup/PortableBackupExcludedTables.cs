namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Tables deliberately left out of a portable backup because they are regenerable operational
/// history, not user intent — already-bounded retention (7-day event/health history, last-2
/// fetch-run generations) means excluding them is about regenerability, not size. See
/// .ai_docs/M3Undle_Backup_Restore_Implementation_Plan.md §3. A new table only needs adding
/// here if it is clearly bulk/history/cache; everything else in the schema is backed up as-is
/// by default.
/// </summary>
public static class PortableBackupExcludedTables
{
    public static readonly IReadOnlyList<string> TableNames =
    [
        "fetch_runs",
        "epg_fetch_runs",
        "system_events",
        "stream_channel_health_events",
        "xtream_series_cache",
        "snapshots",
    ];
}
