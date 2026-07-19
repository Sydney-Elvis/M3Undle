# Back Up and Restore

Open **Settings → Backup & Restore** to create, download, inspect, upload, and restore M3Undle backup archives.

## What a backup contains

The page states that a backup includes configuration, mappings, users, and credentials. Credentials remain encrypted under the host's encryption key.

Provider and EPG history, logs, and caches are excluded because they rebuild after restore. The backup report lists excluded rows from fetch runs, EPG fetch runs, the Xtream series cache, and snapshots.

## Create a backup

Select **Back Up Now**. Completed backups appear in the **Backups** table with their creation time and archive size.

Use **Weekly backup** under **Weekly Schedule** to have M3Undle create a backup automatically about once a week.

## Inspect an existing backup

Each backup row provides:

- **Download** — download the `.m3undle-backup` archive.
- **Validate** — check the archive before relying on it or restoring it.
- **Report** — display backup metadata.
- **Restore** — begin restoring that archive.

The report shows creation time, application version, schema version, encryption-key classification, database size, duration, and excluded-row counts.

Download important archives and store them somewhere separate from the M3Undle container and its configuration volume.

## Upload a backup

Under **Upload a Backup**, choose a `.m3undle-backup` archive, including one created on another host. After it appears in the backup list, use **Validate** and review **Report** before considering a restore.

Because credentials remain encrypted under the source host's key, preserve and account for the encryption key when moving a backup between hosts. The page does not claim that encrypted credentials can be recovered without the appropriate key.

## Restore

Select **Restore** only after validating the intended archive and confirming that it is the correct backup. Restore changes the active M3Undle data, so take a current backup first and avoid interrupting the operation.

A successful restore displays a confirmation message for the archive that was restored. Afterward, review:

- **Providers** and associated profiles
- **Channel Mapping** and the published lineup
- **EPG** source and mapping status
- **Settings → Security**
- dashboard endpoints and published counts

History and caches excluded from the archive may need time or a scheduled refresh to rebuild.
