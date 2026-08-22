using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using M3Undle.Core;
using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static M3Undle.Web.Application.Backup.PortableBackupHashing;

namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Creates portable configuration backups: a pruned, checksummed, versioned
/// .m3undle-backup archive containing everything except the tables in
/// <see cref="PortableBackupExcludedTables"/>. Built on <see cref="SqliteBackupService"/>'s
/// VACUUM INTO primitive — the raw copy is pruned and re-vacuumed on a temp file, never the
/// live database. See .ai_docs/M3Undle_Backup_Restore_Implementation_Plan.md §3-§6.
/// </summary>
public sealed class PortableBackupService(
    ApplicationDbContext db,
    SqliteBackupService sqliteBackupService,
    RuntimePaths runtimePaths,
    SecretEncryptionService encryption,
    AppBuildInfo buildInfo,
    DestructiveOperationLock destructiveOperationLock,
    RestoreStateStore restoreStateStore,
    ILogger<PortableBackupService> logger)
{
    private const int RetainedBackupCount = 5;
    private const int RetainedUploadCount = 3;
    private const string UploadedFileNamePrefix = "uploaded-";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<PortableBackupResult> CreateAsync(CancellationToken cancellationToken)
    {
        // A staged restore references a specific backup state; creating (and retention-pruning)
        // backups while one is armed invites confusion at best — plan §6 requires the block.
        if (restoreStateStore.Read() is { State: RestoreState.Requested })
            return PortableBackupResult.Failed(
                "A restore is staged and waiting for a restart. Confirm or cancel it before creating a backup.");

        if (!destructiveOperationLock.TryAcquire("portable-backup", out var lockHandle))
            return PortableBackupResult.Failed(
                $"Another destructive operation ({destructiveOperationLock.CurrentOperation}) is already in progress. Try again once it completes.");

        using var heldLock = lockHandle;
        var stopwatch = Stopwatch.StartNew();
        var backupId = Guid.NewGuid().ToString("N");
        var backupsDir = Path.Combine(runtimePaths.DataDirectory, "backups");
        Directory.CreateDirectory(backupsDir);

        // Kept under backupsDir (same filesystem as the final published archive), not system
        // temp: /tmp and the data directory are commonly separate mounts in Docker, and the
        // final publish below relies on File.Move being a same-filesystem atomic rename.
        var workDir = Path.Combine(backupsDir, $".work-{backupId}");
        Directory.CreateDirectory(workDir);

        try
        {
            var databasePath = Path.Combine(workDir, "configuration.db");
            await sqliteBackupService.VacuumIntoAsync(databasePath, cancellationToken);

            var rowsRemovedByTable = await PruneExcludedTablesAsync(databasePath, cancellationToken);
            var databaseSha256 = await ComputeSha256Async(databasePath, cancellationToken);
            var databaseSizeBytes = new FileInfo(databasePath).Length;

            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(cancellationToken);
            var manifest = new PortableBackupManifest
            {
                FormatIdentifier = PortableBackupFormat.Identifier,
                FormatVersion = PortableBackupFormat.CurrentVersion,
                AppVersion = buildInfo.Version,
                SchemaVersion = appliedMigrations.LastOrDefault(),
                BackupId = backupId,
                CreatedUtc = DateTime.UtcNow,
                EncryptionKeyId = encryption.ActiveKeyId,
                EncryptionKeyFingerprint = encryption.ActiveKeyFingerprint,
                Scope = "full",
                SettingsEntities = [],
                ExcludedTables = PortableBackupExcludedTables.TableNames,
                DatabaseSha256 = databaseSha256,
            };

            var report = new PortableBackupReport
            {
                BackupId = backupId,
                CreatedUtc = manifest.CreatedUtc,
                RowsRemovedByTable = rowsRemovedByTable,
                DatabaseSizeBytes = databaseSizeBytes,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                Warnings = [],
            };

            var manifestPath = Path.Combine(workDir, "manifest.json");
            var reportPath = Path.Combine(workDir, "backup-report.json");
            await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken);
            await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions), cancellationToken);

            var fileName = $"m3undle-backup-{manifest.CreatedUtc:yyyyMMdd-HHmmss}-{backupId[..8]}{PortableBackupFormat.ArchiveExtension}";
            var finalPath = Path.Combine(backupsDir, fileName);
            var tempArchivePath = Path.Combine(workDir, fileName);

            using (var archive = ZipFile.Open(tempArchivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(manifestPath, "manifest.json", CompressionLevel.Optimal);
                archive.CreateEntryFromFile(databasePath, "configuration.db", CompressionLevel.Optimal);
                archive.CreateEntryFromFile(reportPath, "backup-report.json", CompressionLevel.Optimal);
            }

            // Re-read the database entry back out of the archive we just wrote and re-hash it —
            // catches a corrupt write before the archive is ever considered a valid backup,
            // rather than trusting that packaging succeeded just because it didn't throw.
            var archivedSha256 = await ComputeArchivedDatabaseSha256Async(tempArchivePath, cancellationToken);
            if (!string.Equals(archivedSha256, databaseSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Archived database checksum did not match the source after packaging; discarding this backup.");

            SetOwnerOnlyPermissions(tempArchivePath);
            File.Move(tempArchivePath, finalPath, overwrite: false);

            var removedByRetention = EnforceRetention(backupsDir);

            stopwatch.Stop();
            logger.LogInformation(
                "Portable backup {BackupId} created at {Path} ({SizeBytes} bytes, {Duration:0.0}s); retention removed {RemovedCount} older backup(s).",
                backupId, finalPath, databaseSizeBytes, stopwatch.Elapsed.TotalSeconds, removedByRetention);

            return PortableBackupResult.Succeeded(finalPath, manifest, report);
        }
        finally
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    public IReadOnlyList<PortableBackupSummary> List()
    {
        var backupsDir = Path.Combine(runtimePaths.DataDirectory, "backups");
        if (!Directory.Exists(backupsDir))
            return [];

        return Directory.EnumerateFiles(backupsDir, $"*{PortableBackupFormat.ArchiveExtension}")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new PortableBackupSummary(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    /// <summary>
    /// Resolves a server-generated backup file name to its path. Never accepts anything that
    /// isn't a bare file name directly under the backups directory — no path a caller could use
    /// to escape it, and no user-supplied path is ever trusted directly.
    /// </summary>
    public string? ResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            return null;

        var candidate = Path.Combine(runtimePaths.DataDirectory, "backups", fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    public bool Delete(string fileName)
    {
        var path = ResolvePath(fileName);
        if (path is null)
            return false;

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Saves an uploaded archive under a server-generated name — the client-supplied filename is
    /// never trusted as a path. Content isn't validated here; the caller runs preflight against
    /// the returned file name before treating it as a real backup.
    /// </summary>
    public async Task<string> SaveUploadedArchiveAsync(Stream content, CancellationToken cancellationToken)
    {
        var backupsDir = Path.Combine(runtimePaths.DataDirectory, "backups");
        Directory.CreateDirectory(backupsDir);

        var fileName = $"{UploadedFileNamePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}{PortableBackupFormat.ArchiveExtension}";
        var destinationPath = Path.Combine(backupsDir, fileName);

        try
        {
            await using (var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write))
            {
                // Transport-level limits exist too (endpoint body-size cap, InputFile max size),
                // but the service enforces its own ceiling so no caller can bypass it.
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > PortableBackupFormat.MaxArchiveSizeBytes)
                        throw new InvalidOperationException(
                            $"Uploaded archive exceeds the {PortableBackupFormat.MaxArchiveSizeBytes / (1024 * 1024)} MB size limit.");

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }
        catch
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            throw;
        }

        SetOwnerOnlyPermissions(destinationPath);
        EnforceRetention(backupsDir);
        return fileName;
    }

    /// <summary>
    /// Reads the manifest and report out of a stored archive for display. Returns nulls (never
    /// throws) for corrupt or non-archive files — display code treats that as "no data", while
    /// real validation stays preflight's job.
    /// </summary>
    public (PortableBackupManifest? Manifest, PortableBackupReport? Report) ReadArchiveInfo(string fileName)
    {
        var path = ResolvePath(fileName);
        if (path is null)
            return (null, null);

        try
        {
            using var archive = ZipFile.OpenRead(path);
            return (
                ReadJsonEntry<PortableBackupManifest>(archive, "manifest.json"),
                ReadJsonEntry<PortableBackupReport>(archive, "backup-report.json"));
        }
        catch (InvalidDataException)
        {
            return (null, null);
        }
    }

    private static T? ReadJsonEntry<T>(ZipArchive archive, string entryName) where T : class
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null || entry.Length > PortableBackupFormat.MaxMetadataEntrySizeBytes)
            return null;

        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, int>> PruneExcludedTablesAsync(string databasePath, CancellationToken cancellationToken)
    {
        var rowsRemovedByTable = new Dictionary<string, int>();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            foreach (var table in PortableBackupExcludedTables.TableNames)
            {
                await using var deleteCommand = connection.CreateCommand();
                // provider_channels.last_fetch_run_id is a required, restrict-on-delete FK into
                // fetch_runs — wiping the whole table violates it for any channel whose most
                // recent fetch is still current. Keep the referenced rows; everything else is
                // regenerable history and safe to drop.
                deleteCommand.CommandText = table == "fetch_runs"
                    ? "DELETE FROM \"fetch_runs\" WHERE fetch_run_id NOT IN (SELECT last_fetch_run_id FROM provider_channels)"
                    : $"DELETE FROM \"{table}\"";
                rowsRemovedByTable[table] = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var vacuumCommand = connection.CreateCommand())
            {
                vacuumCommand.CommandText = "VACUUM";
                await vacuumCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var quickCheckCommand = connection.CreateCommand())
            {
                quickCheckCommand.CommandText = "PRAGMA quick_check";
                var result = (string?)await quickCheckCommand.ExecuteScalarAsync(cancellationToken);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Pruned backup database failed integrity check: {result}");
            }
        }
        finally
        {
            await connection.DisposeAsync();
            // The pooled native connection can otherwise keep a file handle open past Dispose,
            // which would block the temp work directory cleanup that follows.
            SqliteConnection.ClearPool(connection);
        }

        return rowsRemovedByTable;
    }

    private int EnforceRetention(string backupsDir)
    {
        // Uploaded archives are retained separately: they share the directory and extension,
        // but counting them against the created-backup budget would let a handful of uploads
        // silently evict every real backup. Neither pool ever deletes the archive a staged
        // (Requested) restore is waiting to apply.
        var stagedFileName = restoreStateStore.Read() is { State: RestoreState.Requested } marker
            ? marker.ArchiveFileName
            : null;

        var allArchives = Directory.EnumerateFiles(backupsDir, $"*{PortableBackupFormat.ArchiveExtension}")
            .Select(path => new FileInfo(path))
            .Where(f => !string.Equals(f.Name, stagedFileName, StringComparison.Ordinal))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var stale = allArchives
            .Where(f => !f.Name.StartsWith(UploadedFileNamePrefix, StringComparison.Ordinal))
            .Skip(RetainedBackupCount)
            .Concat(allArchives
                .Where(f => f.Name.StartsWith(UploadedFileNamePrefix, StringComparison.Ordinal))
                .Skip(RetainedUploadCount));

        var removed = 0;
        foreach (var file in stale)
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to remove backup {FileName} during retention cleanup; will retry on the next backup.", file.Name);
            }
        }

        return removed;
    }

    private static async Task<string> ComputeArchivedDatabaseSha256Async(string archivePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("configuration.db")
            ?? throw new InvalidOperationException("Packaged archive is missing its configuration.db entry.");

        await using var entryStream = entry.Open();
        var hash = await SHA256.HashDataAsync(entryStream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
