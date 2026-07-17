using System.IO.Compression;
using System.Text.Json;
using M3Undle.Core;
using M3Undle.Web.Application;
using M3Undle.Web.Application.Backup;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application.Backup;

// Encryption-key tests mutate process-wide M3UNDLE_ENCRYPTION_KEY(S) env vars (see EnvScope) —
// same convention as SecretEncryptionServiceTests/EncryptionRotationServiceTests.
[TestClass]
[DoNotParallelize]
public sealed class PortableRestoreServiceTests
{
    [TestMethod]
    public async Task PreflightAsync_ValidArchive_Succeeds()
    {
        await using var ctx = await CreateContextAsync();
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src")));

        var result = await ctx.Restore.PreflightAsync(archivePath, CancellationToken.None);

        Assert.IsTrue(result.Success, string.Join(" ", result.Errors));
        Assert.IsNotNull(result.Manifest);
    }

    [TestMethod]
    public async Task PreflightAsync_TamperedChecksum_Fails()
    {
        await using var ctx = await CreateContextAsync();
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src")));
        RewriteManifest(archivePath, m => m with { DatabaseSha256 = new string('0', 64) });

        var result = await ctx.Restore.PreflightAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("checksum", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task PreflightAsync_UnknownFormatVersion_Fails()
    {
        await using var ctx = await CreateContextAsync();
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src")));
        RewriteManifest(archivePath, m => m with { FormatVersion = "99" });

        var result = await ctx.Restore.PreflightAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("Unsupported backup format version", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PreflightAsync_MissingRequiredEncryptionKey_Fails()
    {
        await using var ctx = await CreateContextAsync();

        string archivePath;
        using (var backupEnv = new EnvScope(keys: $"k1:{RandomKey()}"))
        {
            archivePath = await ctx.CreateSourceBackupAsync(
                db => db.Providers.Add(SimpleProvider("src")),
                encryption: new SecretEncryptionService(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance)));
        }

        using var restoreEnv = new EnvScope(keys: null, key: null);
        var restoreSideEncryption = new SecretEncryptionService(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));
        var restoreServiceNoKey = ctx.CreateRestoreServiceWithEncryption(restoreSideEncryption);

        var result = await restoreServiceNoKey.PreflightAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("k1", StringComparison.Ordinal) && e.Contains("not present", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task PreflightAsync_WrongKeyMaterialSameId_Fails()
    {
        await using var ctx = await CreateContextAsync();

        string archivePath;
        using (var backupEnv = new EnvScope(keys: $"k1:{RandomKey()}"))
        {
            archivePath = await ctx.CreateSourceBackupAsync(
                db => db.Providers.Add(SimpleProvider("src")),
                encryption: new SecretEncryptionService(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance)));
        }

        using var restoreEnv = new EnvScope(keys: $"k1:{RandomKey()}"); // same id "k1", different random material
        var restoreSideEncryption = new SecretEncryptionService(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));
        var restoreServiceWrongKey = ctx.CreateRestoreServiceWithEncryption(restoreSideEncryption);

        var result = await restoreServiceWrongKey.PreflightAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("fingerprint", StringComparison.OrdinalIgnoreCase) || e.Contains("does not match", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ApplyAsync_HappyPath_SwapsLiveDatabaseAndCreatesValidatedCheckpoint()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));

        var result = await ctx.Restore.ApplyAsync(archivePath, CancellationToken.None);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsFalse(result.RolledBack);

        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'src-restored'"));
        Assert.AreEqual(0, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"));

        var checkpointsDir = Path.Combine(ctx.TempDataDir, "backups", "checkpoints");
        var checkpoint = Directory.EnumerateFiles(checkpointsDir, "rollback-*.db").Single();
        Assert.AreEqual(1, await CountRowsAsync(checkpoint, "providers", "provider_id = 'live-original'"), "The checkpoint must hold the pre-restore data.");

        var marker = ctx.StateStore.Read();
        Assert.AreEqual(RestoreState.Completed, marker!.State);
    }

    [TestMethod]
    public async Task ApplyAsync_FailureAfterCheckpoint_RollsBackAutomaticallyToOriginalData()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));

        ctx.Restore.SimulateFailureAfterCheckpointForTests = () => throw new InvalidOperationException("simulated failure after checkpoint");

        var result = await ctx.Restore.ApplyAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.RolledBack);
        Assert.IsTrue(result.ErrorMessage!.Contains("simulated failure", StringComparison.Ordinal));

        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"), "Rollback must restore the original data.");
        Assert.AreEqual(0, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'src-restored'"));

        var marker = ctx.StateStore.Read();
        Assert.AreEqual(RestoreState.RolledBack, marker!.State);
    }

    [TestMethod]
    public async Task ApplyAsync_PreflightFailsBeforeCheckpoint_LeavesLiveDatabaseUntouchedAndCreatesNoCheckpoint()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));
        RewriteManifest(archivePath, m => m with { SchemaVersion = "99999999999999_NotARealMigration" });

        var result = await ctx.Restore.ApplyAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.RolledBack, "Nothing was touched, so there is nothing to roll back.");

        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"));

        var checkpointsDir = Path.Combine(ctx.TempDataDir, "backups", "checkpoints");
        Assert.IsFalse(Directory.Exists(checkpointsDir) && Directory.EnumerateFiles(checkpointsDir, "rollback-*.db").Any());
    }

    [TestMethod]
    public async Task ApplyAsync_BackupSchemaDivergesFromMigrations_BlocksBeforeCheckpoint()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));

        // Simulate the in-place-migration-edit failure mode: the backup's migration history
        // claims a known schema version, but its physical schema differs from what this
        // binary's migrations actually produce.
        AddRogueColumnToArchivedDatabase(archivePath, table: "providers", column: "rogue_col");

        var result = await ctx.Restore.ApplyAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.RolledBack, "Schema mismatch must be detected before the checkpoint, so nothing rolls back.");
        Assert.IsTrue(result.ErrorMessage!.Contains("rogue_col", StringComparison.Ordinal), result.ErrorMessage);

        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"));

        var checkpointsDir = Path.Combine(ctx.TempDataDir, "backups", "checkpoints");
        Assert.IsFalse(Directory.Exists(checkpointsDir) && Directory.EnumerateFiles(checkpointsDir, "rollback-*.db").Any());
    }

    [TestMethod]
    public async Task StageAsync_ThenApplyPendingRestoreIfAny_AppliesTheRestoreAndTriggersRefresh()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));

        // Simulate what a future admin API would do: copy the uploaded/selected backup under
        // the live backups directory so PortableBackupService.ResolvePath can find it by name.
        var liveBackupsDir = Path.Combine(ctx.TempDataDir, "backups");
        Directory.CreateDirectory(liveBackupsDir);
        var fileName = Path.GetFileName(archivePath);
        var livePath = Path.Combine(liveBackupsDir, fileName);
        File.Copy(archivePath, livePath);

        var stageResult = await ctx.Restore.StageAsync(fileName, CancellationToken.None);
        Assert.IsTrue(stageResult.Success, string.Join(" ", stageResult.Errors));
        Assert.AreEqual(RestoreState.Requested, ctx.StateStore.Read()!.State);

        var applied = await ctx.Restore.ApplyPendingRestoreIfAnyAsync(CancellationToken.None);

        Assert.IsTrue(applied);
        Assert.AreEqual(RestoreState.Completed, ctx.StateStore.Read()!.State);
        Assert.IsTrue(ctx.RefreshTrigger.TriggerRefreshCalled, "A completed restore must queue a post-restore refresh.");
        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'src-restored'"));
    }

    [TestMethod]
    public async Task ApplyPendingRestoreIfAnyAsync_NoStagedMarker_ReturnsFalseAndTouchesNothing()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));

        var applied = await ctx.Restore.ApplyPendingRestoreIfAnyAsync(CancellationToken.None);

        Assert.IsFalse(applied);
        Assert.IsFalse(ctx.RefreshTrigger.TriggerRefreshCalled);
        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"));
    }

    [TestMethod]
    public async Task CancelStagedRestore_ClearsTheMarker_SoNoRestoreAppliesOnRestart()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var fileName = await ctx.StageSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));

        Assert.AreEqual(RestoreState.Requested, ctx.StateStore.Read()!.State);
        Assert.IsTrue(ctx.Restore.CancelStagedRestore());
        Assert.IsNull(ctx.StateStore.Read());
        Assert.IsFalse(ctx.Restore.CancelStagedRestore(), "Cancelling twice must report nothing staged.");

        var applied = await ctx.Restore.ApplyPendingRestoreIfAnyAsync(CancellationToken.None);

        Assert.IsFalse(applied);
        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"));
    }

    [TestMethod]
    public async Task ApplyPendingRestoreIfAnyAsync_StagedMarkerOlderThanValidityWindow_ExpiresInsteadOfApplying()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        await ctx.StageSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));

        var marker = ctx.StateStore.Read()!;
        ctx.StateStore.Write(marker with
        {
            UpdatedUtc = DateTime.UtcNow - PortableRestoreService.StagedRestoreValidity - TimeSpan.FromMinutes(1),
        });

        var applied = await ctx.Restore.ApplyPendingRestoreIfAnyAsync(CancellationToken.None);

        Assert.IsTrue(applied, "An expired staged restore is still a recorded attempt.");
        var outcome = ctx.StateStore.Read()!;
        Assert.AreEqual(RestoreState.Failed, outcome.State);
        Assert.IsTrue(outcome.ErrorMessage!.Contains("expired", StringComparison.OrdinalIgnoreCase), outcome.ErrorMessage);
        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"), "The live database must be untouched.");
        Assert.AreEqual(0, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'src-restored'"));
    }

    [TestMethod]
    public async Task PreflightAsync_TooManyArchiveEntries_Fails()
    {
        await using var ctx = await CreateContextAsync();
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src")));

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            for (var i = 0; i < PortableBackupFormat.MaxEntryCount; i++)
            {
                var entry = archive.CreateEntry($"padding-{i}.txt");
                await using var stream = entry.Open();
                stream.WriteByte(0);
            }
        }

        var result = await ctx.Restore.PreflightAsync(archivePath, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("too many entries", StringComparison.OrdinalIgnoreCase)), string.Join(" ", result.Errors));
    }

    [TestMethod]
    public async Task ApplyAsync_RotatesSecurityStamps_SoPreRestoreSessionsStopValidating()
    {
        await using var ctx = await CreateContextAsync();
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            SecurityStamp = "ORIGINAL-STAMP",
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        }));

        var result = await ctx.Restore.ApplyAsync(archivePath, CancellationToken.None);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "AspNetUsers", "\"Id\" = 'user-1'"), "The user itself must survive the restore.");
        Assert.AreEqual(0, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "AspNetUsers", "\"SecurityStamp\" = 'ORIGINAL-STAMP'"),
            "Every security stamp must be rotated so cookies from before the restore stop validating.");
    }

    [TestMethod]
    public async Task ApplyAsync_ClearsStaleSnapshotAndHlsArtifacts()
    {
        await using var ctx = await CreateContextAsync();
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));

        var snapshotDir = Path.Combine(ctx.RuntimePaths.SnapshotDirectory, "m3undle", "stale-snapshot");
        Directory.CreateDirectory(snapshotDir);
        await File.WriteAllTextAsync(Path.Combine(snapshotDir, "playlist.m3u"), "#EXTM3U");
        var hlsWorkDir = Path.Combine(ctx.TempDataDir, "hls-work");
        Directory.CreateDirectory(hlsWorkDir);
        await File.WriteAllTextAsync(Path.Combine(hlsWorkDir, "segment-0.ts"), "stale");

        var result = await ctx.Restore.ApplyAsync(archivePath, CancellationToken.None);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(ctx.RuntimePaths.SnapshotDirectory).Count(), "Stale snapshot artifacts must be cleared.");
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(hlsWorkDir).Count(), "Stale HLS work files must be cleared.");
    }

    [TestMethod]
    public async Task ApplyEnvironmentRestoreIfRequestedAsync_NoVariableSet_ReturnsFalseAndTouchesNothing()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        using var env = new RestoreFileEnvScope(null);

        var applied = await ctx.Restore.ApplyEnvironmentRestoreIfRequestedAsync(CancellationToken.None);

        Assert.IsFalse(applied);
        Assert.IsNull(ctx.StateStore.Read());
        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'live-original'"));
    }

    [TestMethod]
    public async Task ApplyEnvironmentRestoreIfRequestedAsync_FileDoesNotExist_RecordsFailureWithoutRetryingSamePath()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var missingPath = Path.Combine(ctx.TempDataDir, "does-not-exist.m3undle-backup");
        using var env = new RestoreFileEnvScope(missingPath);

        var firstAttempt = await ctx.Restore.ApplyEnvironmentRestoreIfRequestedAsync(CancellationToken.None);
        Assert.IsTrue(firstAttempt, "A missing file at the requested path is still a recorded attempt.");
        var marker = ctx.StateStore.Read();
        Assert.AreEqual(RestoreState.Failed, marker!.State);
        Assert.AreEqual(missingPath, marker.SourceEnvironmentPath);

        var secondAttempt = await ctx.Restore.ApplyEnvironmentRestoreIfRequestedAsync(CancellationToken.None);
        Assert.IsFalse(secondAttempt, "The same env var value must not be retried on a normal restart.");
    }

    [TestMethod]
    public async Task ApplyEnvironmentRestoreIfRequestedAsync_ValidArchive_AppliesRestoreAndDoesNotRetriggerOnRestart()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.SeedLiveAsync(db => db.Providers.Add(SimpleProvider("live-original")));
        var archivePath = await ctx.CreateSourceBackupAsync(db => db.Providers.Add(SimpleProvider("src-restored")));
        using var env = new RestoreFileEnvScope(archivePath);

        var firstAttempt = await ctx.Restore.ApplyEnvironmentRestoreIfRequestedAsync(CancellationToken.None);

        Assert.IsTrue(firstAttempt);
        Assert.AreEqual(RestoreState.Completed, ctx.StateStore.Read()!.State);
        Assert.AreEqual(archivePath, ctx.StateStore.Read()!.SourceEnvironmentPath);
        Assert.IsTrue(ctx.RefreshTrigger.TriggerRefreshCalled);
        Assert.AreEqual(1, await CountRowsAsync(ctx.RuntimePaths.DatabasePath, "providers", "provider_id = 'src-restored'"));

        ctx.RefreshTrigger.Reset();
        var secondAttempt = await ctx.Restore.ApplyEnvironmentRestoreIfRequestedAsync(CancellationToken.None);

        Assert.IsFalse(secondAttempt, "A normal restart with the same M3UNDLE_RESTORE_FILE value must not reapply.");
        Assert.IsFalse(ctx.RefreshTrigger.TriggerRefreshCalled);
    }

    // --- test scaffolding ---

    // The installed EF Core package produces cosmetically different, semantically identical
    // model codegen on every snapshot regeneration (confirmed across three independent
    // round-trips — see Program.cs's matching ConfigureWarnings call), which otherwise makes
    // MigrateAsync() throw PendingModelChangesWarning here too.
    private static DbContextOptions<ApplicationDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

    private static async Task<TestContext> CreateContextAsync()
    {
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-restore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDir);
        var livePath = Path.Combine(tempDataDir, "m3undle.db");
        var liveConnectionString = $"Data Source={livePath}";

        await using (var seedDb = new ApplicationDbContext(CreateOptions(liveConnectionString)))
            await seedDb.Database.MigrateAsync();

        var runtimePaths = new RuntimePaths(
            DataDirectory: tempDataDir,
            DatabasePath: livePath,
            DatabaseConnectionString: liveConnectionString,
            LogDirectory: string.Empty,
            SnapshotDirectory: Path.Combine(tempDataDir, "snapshots"));

        var ctx = new TestContext(tempDataDir, runtimePaths, liveConnectionString);
        await ctx.InitializeAsync();
        return ctx;
    }

    private static Provider SimpleProvider(string id) => new()
    {
        ProviderId = id,
        Name = id,
        PlaylistUrl = "http://example.invalid/playlist.m3u",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static async Task<int> CountRowsAsync(string databasePath, string table, string? whereClause = null)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = whereClause is null
            ? $"SELECT COUNT(*) FROM \"{table}\""
            : $"SELECT COUNT(*) FROM \"{table}\" WHERE {whereClause}";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        SqliteConnection.ClearPool(connection);
        return count;
    }

    private static void RewriteManifest(string archivePath, Func<PortableBackupManifest, PortableBackupManifest> mutate)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry("manifest.json")!;

        PortableBackupManifest manifest;
        using (var readStream = entry.Open())
            manifest = JsonSerializer.Deserialize<PortableBackupManifest>(readStream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var mutated = mutate(manifest);
        entry.Delete();
        var newEntry = archive.CreateEntry("manifest.json");
        using var writeStream = newEntry.Open();
        JsonSerializer.Serialize(writeStream, mutated, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Alters the archived configuration.db (adding a column the app's migrations never
    /// created) and rewrites the manifest checksum so the tampering isn't what preflight
    /// catches — only the schema comparison can.
    /// </summary>
    private static void AddRogueColumnToArchivedDatabase(string archivePath, string table, string column)
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"m3undle-rogue-{Guid.NewGuid():N}.db");
        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            {
                archive.GetEntry("configuration.db")!.ExtractToFile(tempDbPath);

                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDbPath }.ToString()))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT";
                    command.ExecuteNonQuery();
                }
                SqliteConnection.ClearAllPools();

                archive.GetEntry("configuration.db")!.Delete();
                archive.CreateEntryFromFile(tempDbPath, "configuration.db");
            }

            var newHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tempDbPath))).ToLowerInvariant();
            RewriteManifest(archivePath, m => m with { DatabaseSha256 = newHash });
        }
        finally
        {
            if (File.Exists(tempDbPath))
                File.Delete(tempDbPath);
        }
    }

    private static string RandomKey() => Convert.ToBase64String(RandomBytes(32));

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private sealed class FakeRefreshTrigger : IRefreshTrigger
    {
        public bool IsRefreshing => false;
        public DateTime? RefreshStartedAt => null;
        public string? CurrentActivity => null;
        public bool TriggerRefreshCalled { get; private set; }

        public bool TriggerRefresh()
        {
            TriggerRefreshCalled = true;
            return true;
        }

        public void Reset() => TriggerRefreshCalled = false;

        public bool TriggerBuildOnly() => true;

        public void CancelRefresh()
        {
        }
    }

    private sealed class TestContext(string tempDataDir, RuntimePaths runtimePaths, string liveConnectionString) : IAsyncDisposable
    {
        public string TempDataDir { get; } = tempDataDir;
        public RuntimePaths RuntimePaths { get; } = runtimePaths;
        public RestoreStateStore StateStore { get; } = new(runtimePaths);
        public FakeRefreshTrigger RefreshTrigger { get; } = new();
        public PortableRestoreService Restore { get; private set; } = null!;

        private readonly List<string> _sourceTempDirs = [];
        private ApplicationDbContext? _liveDb;

        public async Task<PortableRestoreService> InitializeAsync()
        {
            var encryption = new SecretEncryptionService(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));
            Restore = CreateRestoreServiceWithEncryption(encryption);
            return Restore;
        }

        public PortableRestoreService CreateRestoreServiceWithEncryption(SecretEncryptionService encryption)
        {
            var liveDb = new ApplicationDbContext(CreateOptions(liveConnectionString));
            _liveDb = liveDb;
            var sqliteBackup = new SqliteBackupService(liveDb, RuntimePaths, NullLogger<SqliteBackupService>.Instance);
            var buildInfo = new AppBuildInfo("1.2.3-test", null, null, null);
            var backupService = new PortableBackupService(liveDb, sqliteBackup, RuntimePaths, encryption, buildInfo, new DestructiveOperationLock(), StateStore, NullLogger<PortableBackupService>.Instance);

            var environmentVariableService = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
            return new PortableRestoreService(
                liveDb, RuntimePaths, encryption, sqliteBackup, backupService, new DestructiveOperationLock(), StateStore, RefreshTrigger,
                environmentVariableService, NullLogger<PortableRestoreService>.Instance);
        }

        public async Task SeedLiveAsync(Action<ApplicationDbContext> seed)
        {
            await using var db = new ApplicationDbContext(CreateOptions(liveConnectionString));
            seed(db);
            await db.SaveChangesAsync();
        }

        /// <summary>Creates a source backup, copies it under the live backups directory, and stages it.</summary>
        public async Task<string> StageSourceBackupAsync(Action<ApplicationDbContext> seed)
        {
            var archivePath = await CreateSourceBackupAsync(seed);
            var liveBackupsDir = Path.Combine(TempDataDir, "backups");
            Directory.CreateDirectory(liveBackupsDir);
            var fileName = Path.GetFileName(archivePath);
            File.Copy(archivePath, Path.Combine(liveBackupsDir, fileName));

            var stageResult = await Restore.StageAsync(fileName, CancellationToken.None);
            if (!stageResult.Success)
                throw new InvalidOperationException("Failed to stage backup for test: " + string.Join(" ", stageResult.Errors));

            return fileName;
        }

        public async Task<string> CreateSourceBackupAsync(Action<ApplicationDbContext> seed, SecretEncryptionService? encryption = null)
        {
            var sourceDbPath = Path.Combine(Path.GetTempPath(), $"m3undle-restore-source-{Guid.NewGuid():N}.db");
            var sourceConnectionString = $"Data Source={sourceDbPath}";
            var sourceTempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-restore-source-data-{Guid.NewGuid():N}");
            _sourceTempDirs.Add(sourceTempDataDir);

            await using (var setupDb = new ApplicationDbContext(CreateOptions(sourceConnectionString)))
            {
                await setupDb.Database.MigrateAsync();
                seed(setupDb);
                await setupDb.SaveChangesAsync();
            }

            var sourceRuntimePaths = new RuntimePaths(sourceTempDataDir, string.Empty, string.Empty, string.Empty, string.Empty);
            await using var sourceDb = new ApplicationDbContext(CreateOptions(sourceConnectionString));
            var sourceSqliteBackup = new SqliteBackupService(sourceDb, sourceRuntimePaths, NullLogger<SqliteBackupService>.Instance);
            var sourceEncryption = encryption ?? new SecretEncryptionService(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));
            var buildInfo = new AppBuildInfo("1.2.3-test", null, null, null);
            var sourceBackupService = new PortableBackupService(
                sourceDb, sourceSqliteBackup, sourceRuntimePaths, sourceEncryption, buildInfo, new DestructiveOperationLock(),
                new RestoreStateStore(sourceRuntimePaths), NullLogger<PortableBackupService>.Instance);

            var result = await sourceBackupService.CreateAsync(CancellationToken.None);
            if (!result.Success)
                throw new InvalidOperationException($"Failed to create source backup for test: {result.ErrorMessage}");

            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(sourceDbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }

            return result.FilePath!;
        }

        public async ValueTask DisposeAsync()
        {
            if (_liveDb is not null)
                await _liveDb.DisposeAsync();

            SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(TempDataDir))
                    Directory.Delete(TempDataDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }

            foreach (var dir in _sourceTempDirs)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    /// <summary>
    /// Saves/restores M3UNDLE_ENCRYPTION_KEY(S) around a test — same pattern used throughout
    /// this suite (see SecretEncryptionServiceTests.EnvScope).
    /// </summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string? _previousKey;
        private readonly string? _previousKeys;

        public EnvScope(string? keys = null, string? key = null)
        {
            _previousKey = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY");
            _previousKeys = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS");

            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", key);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", keys);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", _previousKey);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", _previousKeys);
        }
    }

    /// <summary>Saves/restores M3UNDLE_RESTORE_FILE around a test.</summary>
    private sealed class RestoreFileEnvScope : IDisposable
    {
        private readonly string? _previous;

        public RestoreFileEnvScope(string? path)
        {
            _previous = Environment.GetEnvironmentVariable("M3UNDLE_RESTORE_FILE");
            Environment.SetEnvironmentVariable("M3UNDLE_RESTORE_FILE", path);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("M3UNDLE_RESTORE_FILE", _previous);
    }
}
