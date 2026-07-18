using System.IO.Compression;
using System.Text.Json;
using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static M3Undle.Web.Application.Backup.PortableBackupHashing;

namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Validates and applies portable backup restores. See
/// .ai_docs/M3Undle_Backup_Restore_Implementation_Plan.md §7 for the staged workflow this
/// implements: preflight (no mutation) → stage (writes the Requested marker) → apply (creates a
/// rollback checkpoint, swaps the live database, migrates/validates, rolls back automatically on
/// any failure after the checkpoint exists).
/// </summary>
public sealed class PortableRestoreService(
    ApplicationDbContext db,
    RuntimePaths runtimePaths,
    SecretEncryptionService encryption,
    SqliteBackupService sqliteBackupService,
    PortableBackupService backupService,
    DestructiveOperationLock destructiveOperationLock,
    RestoreStateStore stateStore,
    IRefreshTrigger refreshTrigger,
    EnvironmentVariableService environmentVariableService,
    ILogger<PortableRestoreService> logger)
{
    private const string RestoreFileEnvironmentVariable = "M3UNDLE_RESTORE_FILE";

    private const int RetainedCheckpointCount = 2;

    /// <summary>
    /// How long a staged (Requested) restore stays valid. Staging is supposed to be followed
    /// immediately by a confirm/restart; without an expiry, an abandoned staged restore would
    /// silently replace the database on whatever restart happens next — a host reboot or an
    /// image upgrade weeks later.
    /// </summary>
    internal static readonly TimeSpan StagedRestoreValidity = TimeSpan.FromMinutes(15);

    /// <summary>Free space to demand beyond the calculated staging + checkpoint requirement.</summary>
    private const long DiskSpaceSlackBytes = 64L * 1024 * 1024;

    /// <summary>Test-only fault injection hook — see the call site in <see cref="ApplyAsync"/>.</summary>
    internal Action? SimulateFailureAfterCheckpointForTests { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Validates an archive without mutating anything — safe to call any number of times.
    /// Includes the "no conflicting operation" check, appropriate when nothing else has already
    /// claimed the lock on this call's behalf. <see cref="ApplyAsync"/> validates the same way
    /// internally via <see cref="ValidateArchiveAsync"/> instead, since by that point it already
    /// holds the lock itself and would otherwise see its own lock as a conflict.
    /// </summary>
    public async Task<PortableRestorePreflightResult> PreflightAsync(string archivePath, CancellationToken cancellationToken)
    {
        var result = await ValidateArchiveAsync(archivePath, cancellationToken);
        if (!result.Success)
            return result;

        if (destructiveOperationLock.CurrentOperation is not null)
        {
            return PortableRestorePreflightResult.Failed(
                [$"Another destructive operation ({destructiveOperationLock.CurrentOperation}) is in progress."], result.Manifest);
        }

        return result;
    }

    private async Task<PortableRestorePreflightResult> ValidateArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
            return PortableRestorePreflightResult.Failed(["Archive file not found."]);

        if (new FileInfo(archivePath).Length > PortableBackupFormat.MaxArchiveSizeBytes)
            return PortableRestorePreflightResult.Failed(
                [$"Archive exceeds the {PortableBackupFormat.MaxArchiveSizeBytes / (1024 * 1024)} MB size limit."]);

        var workDir = Path.Combine(runtimePaths.DataDirectory, "restore-work", $"preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(archivePath);
            }
            catch (InvalidDataException)
            {
                return PortableRestorePreflightResult.Failed(["File is not a valid archive."]);
            }

            using (archive)
            {
                if (archive.Entries.Count > PortableBackupFormat.MaxEntryCount)
                    return PortableRestorePreflightResult.Failed([$"Archive contains too many entries ({archive.Entries.Count})."]);

                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(entry.FullName))
                        return PortableRestorePreflightResult.Failed([$"Archive entry '{entry.FullName}' is unsafe."]);
                }

                var manifestEntry = archive.GetEntry("manifest.json");
                var databaseEntry = archive.GetEntry("configuration.db");
                var reportEntry = archive.GetEntry("backup-report.json");
                if (manifestEntry is null || databaseEntry is null || reportEntry is null)
                    return PortableRestorePreflightResult.Failed(["Archive is missing a required entry."]);

                PortableBackupManifest? manifest;
                try
                {
                    // Read through a hard byte cap rather than trusting the entry's declared
                    // length — a crafted central directory can understate it.
                    using var manifestBuffer = new MemoryStream();
                    await using (var manifestStream = manifestEntry.Open())
                        await CopyWithLimitAsync(manifestStream, manifestBuffer, PortableBackupFormat.MaxMetadataEntrySizeBytes, "manifest.json", cancellationToken);
                    manifestBuffer.Position = 0;
                    manifest = await JsonSerializer.DeserializeAsync<PortableBackupManifest>(manifestBuffer, JsonOptions, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    return PortableRestorePreflightResult.Failed([ex.Message]);
                }
                catch (JsonException)
                {
                    manifest = null;
                }

                if (manifest is null)
                    return PortableRestorePreflightResult.Failed(["manifest.json could not be parsed."]);

                var errors = new List<string>();

                if (!string.Equals(manifest.FormatIdentifier, PortableBackupFormat.Identifier, StringComparison.Ordinal))
                    errors.Add("Archive is not an M3Undle backup.");
                if (!string.Equals(manifest.FormatVersion, PortableBackupFormat.CurrentVersion, StringComparison.Ordinal))
                    errors.Add($"Unsupported backup format version '{manifest.FormatVersion}'.");

                if (errors.Count > 0)
                    return PortableRestorePreflightResult.Failed(errors, manifest);

                var extractedDbPath = Path.Combine(workDir, "configuration.db");
                try
                {
                    await ExtractEntryWithLimitAsync(databaseEntry, extractedDbPath, PortableBackupFormat.MaxDatabaseSizeBytes, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    return PortableRestorePreflightResult.Failed([ex.Message], manifest);
                }

                var diskSpaceError = CheckDiskSpace(new FileInfo(extractedDbPath).Length);
                if (diskSpaceError is not null)
                    return PortableRestorePreflightResult.Failed([diskSpaceError], manifest);

                var actualHash = await ComputeSha256Async(extractedDbPath, cancellationToken);
                if (!string.Equals(actualHash, manifest.DatabaseSha256, StringComparison.Ordinal))
                    errors.Add("Database checksum does not match the manifest; archive may be corrupt or tampered with.");

                if (!IsSqliteHeaderValid(extractedDbPath))
                {
                    errors.Add("Packaged database does not have a valid SQLite header.");
                }
                else
                {
                    var quickCheckResult = await RunQuickCheckAsync(extractedDbPath, cancellationToken);
                    if (quickCheckResult is not null)
                        errors.Add($"Packaged database failed integrity check: {quickCheckResult}");
                }

                var knownMigrations = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                if (manifest.SchemaVersion is not null && !knownMigrations.Contains(manifest.SchemaVersion))
                    errors.Add($"Backup schema version '{manifest.SchemaVersion}' is not recognized by this application — it may be from a newer, unsupported release.");

                if (manifest.EncryptionKeyId is not null)
                {
                    var currentFingerprint = encryption.GetKeyFingerprint(manifest.EncryptionKeyId);
                    if (currentFingerprint is null)
                        errors.Add($"Required encryption key '{manifest.EncryptionKeyId}' is not present in the current key ring.");
                    else if (!string.Equals(currentFingerprint, manifest.EncryptionKeyFingerprint, StringComparison.Ordinal))
                        errors.Add($"Encryption key '{manifest.EncryptionKeyId}' is present but its key material does not match this backup.");
                }

                return errors.Count == 0
                    ? PortableRestorePreflightResult.Succeeded(manifest)
                    : PortableRestorePreflightResult.Failed(errors, manifest);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // Best-effort: a delete failure here (e.g. a lingering file handle) must not throw
            // out of this finally block and mask the preflight result the try block produced.
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to clean up preflight work directory {WorkDir}.", workDir);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Failed to clean up preflight work directory {WorkDir}.", workDir);
            }
        }
    }

    /// <summary>The current or last restore's durable status — survives the restart between staging and applying.</summary>
    public RestoreStateMarker? GetCurrentStatus() => stateStore.Read();

    /// <summary>Clears a completed/failed/rolled-back status once the operator has seen it.</summary>
    public void ClearStatus() => stateStore.Clear();

    /// <summary>
    /// Cancels a staged (Requested) restore so it can no longer fire on a future restart.
    /// Returns false if nothing was staged.
    /// </summary>
    public bool CancelStagedRestore()
    {
        var marker = stateStore.Read();
        if (marker is null || marker.State != RestoreState.Requested)
            return false;

        stateStore.Clear();
        logger.LogInformation("Staged restore of backup {BackupId} was cancelled.", marker.BackupId);
        return true;
    }

    /// <summary>
    /// Runs preflight against an existing server-side backup and, if it passes, records a
    /// Requested marker for a future confirm step to act on. Does not restart the process or
    /// touch the live database — see plan §7.1-§7.3.
    /// </summary>
    public async Task<PortableRestoreStageResult> StageAsync(string backupFileName, CancellationToken cancellationToken)
    {
        var archivePath = backupService.ResolvePath(backupFileName);
        if (archivePath is null)
            return PortableRestoreStageResult.Failed(["Backup file not found."]);

        var preflight = await PreflightAsync(archivePath, cancellationToken);
        if (!preflight.Success)
            return PortableRestoreStageResult.Failed(preflight.Errors);

        stateStore.Write(new RestoreStateMarker
        {
            State = RestoreState.Requested,
            BackupId = preflight.Manifest!.BackupId,
            ArchiveFileName = backupFileName,
            UpdatedUtc = DateTime.UtcNow,
        });

        return PortableRestoreStageResult.Succeeded(preflight.Manifest);
    }

    /// <summary>
    /// Checks M3UNDLE_RESTORE_FILE and applies it directly if present — setting the variable and
    /// restarting the container is itself the operator's confirmation, so this skips the
    /// stage/confirm split used by the UI path. One-shot by the variable's own value: once an
    /// attempt has been recorded for a given path (success, failure, or missing file), a normal
    /// container restart that leaves the same value set will not retry it. To retry, an operator
    /// changes the env var value or clears restore-state.json — never requires editing the
    /// SQLite database. Must run before <see cref="ApplyPendingRestoreIfAnyAsync"/> at startup.
    /// Returns true if an attempt was made (any outcome) so the caller can log it.
    /// </summary>
    public async Task<bool> ApplyEnvironmentRestoreIfRequestedAsync(CancellationToken cancellationToken)
    {
        var requestedPath = environmentVariableService.GetValue(RestoreFileEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedPath))
            return false;

        var marker = stateStore.Read();
        if (marker is not null && string.Equals(marker.SourceEnvironmentPath, requestedPath, StringComparison.Ordinal))
            return false;

        if (!File.Exists(requestedPath))
        {
            stateStore.Write(new RestoreStateMarker
            {
                State = RestoreState.Failed,
                BackupId = string.Empty,
                ArchiveFileName = Path.GetFileName(requestedPath),
                UpdatedUtc = DateTime.UtcNow,
                ErrorMessage = $"{RestoreFileEnvironmentVariable} is set to '{requestedPath}', but no file exists there.",
                SourceEnvironmentPath = requestedPath,
            });

            logger.LogError("{Variable} is set to '{Path}', but no file exists there. Restore not attempted.", RestoreFileEnvironmentVariable, requestedPath);
            return true;
        }

        var result = await ApplyAsync(requestedPath, cancellationToken);

        // ApplyAsync already wrote its own outcome marker — stamp the source path onto it so the
        // one-shot check above recognizes this exact value as handled on the next restart.
        var updated = stateStore.Read();
        if (updated is not null)
            stateStore.Write(updated with { SourceEnvironmentPath = requestedPath });

        if (result.Success)
            refreshTrigger.TriggerRefresh();

        return true;
    }

    /// <summary>
    /// Called at the same early startup point db.Database.Migrate() already runs from
    /// (before EF migrations touch the live database and before hosted services start — see
    /// plan §7.5, Program.cs:407-426). No-ops if no restore is staged. Returns true if a restore
    /// was applied (successfully or not) so the caller can log it.
    /// </summary>
    public async Task<bool> ApplyPendingRestoreIfAnyAsync(CancellationToken cancellationToken)
    {
        var marker = stateStore.Read();
        if (marker is null || marker.State != RestoreState.Requested)
            return false;

        // A staged restore is a destructive commitment; if the confirm/restart never happened
        // (abandoned browser session, failed restart), it must not fire on some unrelated
        // restart days later.
        if (DateTime.UtcNow - marker.UpdatedUtc > StagedRestoreValidity)
        {
            stateStore.Write(marker with
            {
                State = RestoreState.Failed,
                ErrorMessage = $"Staged restore expired: it was staged at {marker.UpdatedUtc:u} but not confirmed within {StagedRestoreValidity.TotalMinutes:0} minutes. Stage it again to restore.",
                UpdatedUtc = DateTime.UtcNow,
            });
            logger.LogWarning("Staged restore of backup {BackupId} expired without confirmation and was not applied.", marker.BackupId);
            return true;
        }

        var archivePath = backupService.ResolvePath(marker.ArchiveFileName);
        if (archivePath is null)
        {
            stateStore.Write(marker with { State = RestoreState.Failed, ErrorMessage = "Staged archive is no longer present.", UpdatedUtc = DateTime.UtcNow });
            return true;
        }

        var result = await ApplyAsync(archivePath, cancellationToken);
        if (result.Success)
            refreshTrigger.TriggerRefresh();

        return true;
    }

    /// <summary>
    /// Revalidates the archive, checkpoints the live database, swaps it in, migrates and
    /// validates the result, and automatically rolls back to the checkpoint on any failure from
    /// that point on. Never partially completes: either the new database is live and verified,
    /// or the original database is restored and verified.
    /// </summary>
    public async Task<PortableRestoreResult> ApplyAsync(string archivePath, CancellationToken cancellationToken)
    {
        if (!destructiveOperationLock.TryAcquire("restore", out var lockHandle))
            return PortableRestoreResult.Failed(
                $"Another destructive operation ({destructiveOperationLock.CurrentOperation}) is already in progress.", rolledBack: false);

        using var heldLock = lockHandle;

        var preflight = await ValidateArchiveAsync(archivePath, cancellationToken);
        if (!preflight.Success)
            return PortableRestoreResult.Failed("Preflight failed: " + string.Join(" ", preflight.Errors), rolledBack: false);

        var manifest = preflight.Manifest!;

        // Same filesystem as the live database (both under DataDirectory) so the eventual swap
        // is a true atomic rename rather than a cross-device copy. Named with a freshly generated
        // id rather than manifest.BackupId — the manifest comes from untrusted archive content,
        // and splicing it into a path unvalidated would allow a crafted BackupId to escape
        // restore-work (e.g. "..") when combined with the directory below.
        var workDir = Path.Combine(runtimePaths.DataDirectory, "restore-work", $"apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string? checkpointPath = null;

        try
        {
            var extractedDbPath = Path.Combine(workDir, "configuration.db");
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                await ExtractEntryWithLimitAsync(
                    archive.GetEntry("configuration.db")!, extractedDbPath, PortableBackupFormat.MaxDatabaseSizeBytes, cancellationToken);
            }

            // Bring the extracted database fully up to date with this app's migrations before it
            // ever becomes the live database — the shared migration call in Program.cs that runs
            // right after this will then find nothing pending.
            var extractedConnectionString = new SqliteConnectionStringBuilder { DataSource = extractedDbPath }.ToString();
            await using (var extractedContext = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlite(extractedConnectionString)
                    // Matches Program.cs's DbContext registration: the installed EF Core package
                    // produces cosmetically different, semantically identical model codegen on
                    // every snapshot regeneration, which otherwise makes Migrate() throw here too.
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                    .Options))
            {
                await extractedContext.Database.MigrateAsync(cancellationToken);
            }
            ClearPoolFor(extractedConnectionString);

            var quickCheckError = await RunQuickCheckAsync(extractedDbPath, cancellationToken);
            if (quickCheckError is not null)
                throw new InvalidOperationException($"Migrated backup database failed integrity check: {quickCheckError}");

            var fkViolations = await RunForeignKeyCheckAsync(extractedDbPath, cancellationToken);
            if (fkViolations.Count > 0)
                throw new InvalidOperationException($"Migrated backup database has foreign key violations: {string.Join("; ", fkViolations)}");

            // The migration-name check in preflight can't detect a backup whose schema silently
            // diverged from what its recorded migration produces (the failure mode of editing an
            // already-shipped migration in place). Compare the migrated database structurally
            // against a pristine database built from this binary's own migrations, and refuse to
            // swap in anything that doesn't match — before the checkpoint, so nothing is touched.
            var pristineDbPath = Path.Combine(workDir, "pristine.db");
            var pristineConnectionString = new SqliteConnectionStringBuilder { DataSource = pristineDbPath }.ToString();
            await using (var pristineContext = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlite(pristineConnectionString)
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                    .Options))
            {
                await pristineContext.Database.MigrateAsync(cancellationToken);
            }
            ClearPoolFor(pristineConnectionString);

            var schemaDifferences = await SqliteSchemaComparer.CompareAsync(pristineDbPath, extractedDbPath, cancellationToken);
            if (schemaDifferences.Count > 0)
                throw new InvalidOperationException(
                    $"Migrated backup database does not match the schema this version produces: {string.Join(" ", schemaDifferences)}");

            // Rotate every user's security stamp in the database that is about to go live, so
            // cookies issued on the pre-restore timeline (or on whatever host created the
            // backup) stop validating and everyone signs in again — see plan §7.8. Requires the
            // security-stamp validation interval configured in Program.cs to take effect
            // immediately rather than at the default 30-minute revalidation.
            await RotateSecurityStampsAsync(extractedDbPath, cancellationToken);

            // --- Point of no return: everything past here touches the live database file. ---
            checkpointPath = await CreateRollbackCheckpointAsync(cancellationToken);
            stateStore.Write(new RestoreStateMarker
            {
                State = RestoreState.CheckpointCreated,
                BackupId = manifest.BackupId,
                ArchiveFileName = Path.GetFileName(archivePath),
                UpdatedUtc = DateTime.UtcNow,
                RollbackCheckpointFileName = Path.GetFileName(checkpointPath),
            });

            // Test-only fault injection: lets tests exercise the automatic-rollback path
            // deterministically. There is no organic way to make only the primary swap fail
            // without also blocking the rollback swap below, since both target the same live
            // database path.
            SimulateFailureAfterCheckpointForTests?.Invoke();

            SwapInto(runtimePaths.DatabasePath, extractedDbPath);

            var postSwapCheck = await RunQuickCheckAsync(runtimePaths.DatabasePath, cancellationToken);
            if (postSwapCheck is not null)
                throw new InvalidOperationException($"Live database failed integrity check immediately after restore: {postSwapCheck}");

            // The restored database's snapshots table is empty (excluded from backup), so any
            // artifact files on disk belong to the discarded timeline and nothing references
            // them. Best-effort cleanup; the post-restore refresh regenerates everything.
            ClearStaleWorkDirectories();

            stateStore.Write(new RestoreStateMarker
            {
                State = RestoreState.Completed,
                BackupId = manifest.BackupId,
                ArchiveFileName = Path.GetFileName(archivePath),
                UpdatedUtc = DateTime.UtcNow,
                RollbackCheckpointFileName = Path.GetFileName(checkpointPath),
            });

            logger.LogInformation("Restore of backup {BackupId} completed.", manifest.BackupId);
            return PortableRestoreResult.Succeeded(manifest.BackupId);
        }
        catch (Exception ex) when (checkpointPath is not null)
        {
            logger.LogError(ex, "Restore of backup {BackupId} failed after the live database may have changed — rolling back to the pre-restore checkpoint.", manifest.BackupId);

            try
            {
                SwapInto(runtimePaths.DatabasePath, checkpointPath);

                stateStore.Write(new RestoreStateMarker
                {
                    State = RestoreState.RolledBack,
                    BackupId = manifest.BackupId,
                    ArchiveFileName = Path.GetFileName(archivePath),
                    UpdatedUtc = DateTime.UtcNow,
                    ErrorMessage = ex.Message,
                    RollbackCheckpointFileName = Path.GetFileName(checkpointPath),
                });

                return PortableRestoreResult.Failed($"Restore failed and was automatically rolled back: {ex.Message}", rolledBack: true);
            }
            catch (Exception rollbackEx)
            {
                logger.LogCritical(
                    rollbackEx,
                    "Automatic rollback of backup {BackupId} FAILED after the live database was already changed. Manual recovery required from checkpoint {CheckpointPath}.",
                    manifest.BackupId, checkpointPath);

                stateStore.Write(new RestoreStateMarker
                {
                    State = RestoreState.Failed,
                    BackupId = manifest.BackupId,
                    ArchiveFileName = Path.GetFileName(archivePath),
                    UpdatedUtc = DateTime.UtcNow,
                    ErrorMessage = $"Restore failed AND automatic rollback failed: {rollbackEx.Message}. Manual recovery required from checkpoint file '{Path.GetFileName(checkpointPath)}'.",
                    RollbackCheckpointFileName = Path.GetFileName(checkpointPath),
                });

                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore of backup {BackupId} failed before the live database was touched — no rollback needed.", manifest.BackupId);
            stateStore.Write(new RestoreStateMarker
            {
                State = RestoreState.Failed,
                BackupId = manifest.BackupId,
                ArchiveFileName = Path.GetFileName(archivePath),
                UpdatedUtc = DateTime.UtcNow,
                ErrorMessage = ex.Message,
            });

            return PortableRestoreResult.Failed(ex.Message, rolledBack: false);
        }
        finally
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    private static async Task RotateSecurityStampsAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // randomblob is evaluated per row, so every user gets a distinct new stamp.
            command.CommandText = "UPDATE \"AspNetUsers\" SET \"SecurityStamp\" = upper(hex(randomblob(16)))";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await connection.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }
    }

    private void ClearStaleWorkDirectories()
    {
        // hls-work is the default GeneratedHlsOptions directory under the data dir; a custom
        // configured path isn't cleaned here (best-effort — stale HLS segments are harmless,
        // they're only reachable through sessions that died with the restart).
        foreach (var dir in new[] { runtimePaths.SnapshotDirectory, Path.Combine(runtimePaths.DataDirectory, "hls-work") })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;

            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(dir))
                {
                    if (Directory.Exists(child))
                        Directory.Delete(child, recursive: true);
                    else
                        File.Delete(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not fully clear stale work directory {Directory} after restore; the next refresh will overwrite it.", dir);
            }
        }
    }

    private async Task<string> CreateRollbackCheckpointAsync(CancellationToken cancellationToken)
    {
        var checkpointsDir = Path.Combine(runtimePaths.DataDirectory, "backups", "checkpoints");
        Directory.CreateDirectory(checkpointsDir);
        var checkpointPath = Path.Combine(
            checkpointsDir, $"rollback-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}.db");

        await sqliteBackupService.VacuumIntoAsync(checkpointPath, cancellationToken);

        var quickCheckError = await RunQuickCheckAsync(checkpointPath, cancellationToken);
        if (quickCheckError is not null)
        {
            File.Delete(checkpointPath);
            throw new InvalidOperationException(
                $"Rollback checkpoint failed its integrity check ({quickCheckError}); aborting restore before touching the live database.");
        }

        EnforceCheckpointRetention(checkpointsDir);
        return checkpointPath;
    }

    private void EnforceCheckpointRetention(string checkpointsDir)
    {
        var stale = Directory.EnumerateFiles(checkpointsDir, "rollback-*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(RetainedCheckpointCount)
            .ToList();

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to remove rollback checkpoint {FileName} during retention cleanup.", file.Name);
            }
        }
    }

    /// <summary>
    /// Atomically replaces destinationPath's content with sourcePath's, on the same filesystem,
    /// and removes any stale -wal/-shm siblings so a leftover WAL from the previous file's
    /// content can never be replayed against the new one.
    /// </summary>
    private void SwapInto(string destinationPath, string sourcePath)
    {
        ClearPoolFor(runtimePaths.DatabaseConnectionString);
        DeleteStaleWalFiles(destinationPath);

        var tempDestination = destinationPath + ".swap-tmp";
        if (File.Exists(tempDestination))
            File.Delete(tempDestination);

        File.Copy(sourcePath, tempDestination);
        File.Move(tempDestination, destinationPath, overwrite: true);

        DeleteStaleWalFiles(destinationPath);
    }

    private static void DeleteStaleWalFiles(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var siblingPath = databasePath + suffix;
            if (File.Exists(siblingPath))
                File.Delete(siblingPath);
        }
    }

    private static void ClearPoolFor(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return;

        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// Enough free space for the extracted database during staging plus a rollback checkpoint of
    /// the live database, with slack. Best-effort: if free space can't be determined (unusual
    /// mounts), the restore proceeds rather than being blocked on a probe failure.
    /// </summary>
    private string? CheckDiskSpace(long extractedDatabaseBytes)
    {
        long liveDatabaseBytes = 0;
        if (!string.IsNullOrEmpty(runtimePaths.DatabasePath) && File.Exists(runtimePaths.DatabasePath))
            liveDatabaseBytes = new FileInfo(runtimePaths.DatabasePath).Length;

        var required = extractedDatabaseBytes + liveDatabaseBytes + DiskSpaceSlackBytes;
        long availableBytes;
        try
        {
            var probePath = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Path.GetFullPath(runtimePaths.DataDirectory))!
                : runtimePaths.DataDirectory;
            availableBytes = new DriveInfo(probePath).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return availableBytes >= required
            ? null
            : $"Not enough free disk space for the restore: {required / (1024 * 1024)} MB required (staging plus rollback checkpoint), {availableBytes / (1024 * 1024)} MB available.";
    }

    private static async Task ExtractEntryWithLimitAsync(
        ZipArchiveEntry entry, string destinationPath, long maxBytes, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write);
        await using var source = entry.Open();
        await CopyWithLimitAsync(source, destination, maxBytes, entry.FullName, cancellationToken);
    }

    private static async Task CopyWithLimitAsync(
        Stream source, Stream destination, long maxBytes, string entryName, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"Archive entry '{entryName}' expands beyond the {maxBytes / (1024 * 1024)} MB limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsSqliteHeaderValid(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task<string?> RunQuickCheckAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            var result = (string?)await command.ExecuteScalarAsync(cancellationToken);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result;
        }
        finally
        {
            await connection.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }
    }

    private static async Task<IReadOnlyList<string>> RunForeignKeyCheckAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var connection = new SqliteConnection(connectionString);
        var violations = new List<string>();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                violations.Add(reader.GetString(0));
        }
        finally
        {
            await connection.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }

        return violations;
    }
}
