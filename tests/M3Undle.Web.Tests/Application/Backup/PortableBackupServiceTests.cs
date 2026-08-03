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

[TestClass]
public sealed class PortableBackupServiceTests
{
    [TestMethod]
    public async Task CreateAsync_ExcludesRegeneratedTables_ButKeepsDurableConfiguration()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1"));
            setup.Profiles.Add(SimpleProfile("pr1"));
            setup.EpgSources.Add(SimpleEpgSource("epg1"));
            setup.FetchRuns.Add(SimpleFetchRun("f1", "p1"));
            setup.EpgFetchRuns.Add(SimpleEpgFetchRun("ef1", "epg1"));
            setup.SystemEvents.Add(SimpleSystemEvent("se1"));
            setup.StreamChannelHealthEvents.Add(SimpleHealthEvent("he1", "p1"));
            setup.XtreamSeriesCache.Add(SimpleSeriesCache("p1"));
            setup.ProviderGroups.Add(SimpleProviderGroup("pg1", "p1"));
            setup.CatalogItems.Add(SimpleCatalogItem("ci1", "p1", "pg1"));
            setup.Snapshots.Add(SimpleSnapshot("sn1", "pr1"));
            await setup.SaveChangesAsync();
        }

        var service = CreateService(fixture, out var tempDataDir, out var db);
        try
        {
            var result = await service.CreateAsync(CancellationToken.None);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            using var extracted = ExtractDatabase(result.FilePath!);

            foreach (var table in PortableBackupExcludedTables.TableNames)
                Assert.AreEqual(0, await CountRowsAsync(extracted.DatabasePath, table), $"Excluded table '{table}' must be empty after backup.");

            Assert.AreEqual(1, await CountRowsAsync(extracted.DatabasePath, "providers"), "Providers is not on the exclude list and must survive.");
            Assert.AreEqual(1, await CountRowsAsync(extracted.DatabasePath, "profiles"), "Profiles is not on the exclude list and must survive.");
            Assert.AreEqual(1, await CountRowsAsync(extracted.DatabasePath, "epg_sources"), "EpgSources is not on the exclude list and must survive.");

            var report = ReadReport(extracted);
            foreach (var table in PortableBackupExcludedTables.TableNames)
                Assert.AreEqual(1, report.RowsRemovedByTable[table], $"Report should record exactly the one seeded row removed from '{table}'.");
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAsync_FetchRunStillReferencedByProviderChannel_IsKeptNotDeleted()
    {
        // Reproduces the toontown-int-srv1 failure: provider_channels.last_fetch_run_id is a
        // required, restrict-on-delete FK into fetch_runs. Wiping the whole table throws a
        // FOREIGN KEY constraint violation for any channel whose most recent fetch is still
        // current — only rows nothing references may actually be dropped.
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1"));
            setup.FetchRuns.Add(SimpleFetchRun("f-old", "p1"));
            setup.FetchRuns.Add(SimpleFetchRun("f-current", "p1"));
            setup.ProviderChannels.Add(SimpleProviderChannel("pc1", "p1", "f-current"));
            await setup.SaveChangesAsync();
        }

        var service = CreateService(fixture, out var tempDataDir, out var db);
        try
        {
            var result = await service.CreateAsync(CancellationToken.None);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            using var extracted = ExtractDatabase(result.FilePath!);

            Assert.AreEqual(1, await CountRowsAsync(extracted.DatabasePath, "fetch_runs"),
                "The fetch_run still referenced by a provider_channel must survive.");
            Assert.AreEqual(1, await CountRowsAsync(extracted.DatabasePath, "provider_channels"),
                "provider_channels is not on the exclude list and must survive.");

            var report = ReadReport(extracted);
            Assert.AreEqual(1, report.RowsRemovedByTable["fetch_runs"],
                "Only the unreferenced fetch_run should be reported as removed.");
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAsync_ManifestChecksum_MatchesArchivedDatabase()
    {
        await using var fixture = await CreateFixtureAsync();
        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1"));
            await setup.SaveChangesAsync();
        }

        var service = CreateService(fixture, out var tempDataDir, out var db);
        try
        {
            var result = await service.CreateAsync(CancellationToken.None);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            using var extracted = ExtractDatabase(result.FilePath!);

            var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(extracted.DatabasePath))).ToLowerInvariant();

            Assert.AreEqual(result.Manifest!.DatabaseSha256, actualHash);
            Assert.AreEqual("m3undle-backup", result.Manifest.FormatIdentifier);
            Assert.AreEqual("1", result.Manifest.FormatVersion);
            CollectionAssert.AreEquivalent(PortableBackupExcludedTables.TableNames.ToArray(), result.Manifest.ExcludedTables.ToArray());
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAsync_AnotherDestructiveOperationHoldsTheLock_FailsWithoutCreatingAnArchive()
    {
        await using var fixture = await CreateFixtureAsync();
        var sharedLock = new DestructiveOperationLock();
        var service = CreateService(fixture, out var tempDataDir, out var db, sharedLock);

        try
        {
            Assert.IsTrue(sharedLock.TryAcquire("encryption-key-rotation", out var heldByOther));

            var result = await service.CreateAsync(CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.ErrorMessage!.Contains("encryption-key-rotation"));
            Assert.AreEqual(0, service.List().Count);

            heldByOther!.Dispose();
        }
        finally
        {
            await db.DisposeAsync();
            if (Directory.Exists(tempDataDir))
                Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAsync_MoreThanRetainedCount_RemovesOnlyTheOldest()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture, out var tempDataDir, out var db);

        try
        {
            var created = new List<string>();
            for (var i = 0; i < 6; i++)
            {
                var result = await service.CreateAsync(CancellationToken.None);
                Assert.IsTrue(result.Success, result.ErrorMessage);
                created.Add(result.FilePath!);
                await Task.Delay(50); // ensure distinct file-write timestamps for retention ordering
            }

            var remaining = service.List();

            Assert.AreEqual(5, remaining.Count, "Retention must keep only the 5 most recent backups.");
            Assert.IsFalse(File.Exists(created[0]), "The oldest backup must be removed by retention.");
            Assert.IsTrue(File.Exists(created[^1]), "The newest backup must survive retention.");
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateAsync_RestoreIsStaged_FailsWithoutCreatingAnArchive()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture, out var tempDataDir, out var db, out var stateStore);

        try
        {
            stateStore.Write(new RestoreStateMarker
            {
                State = RestoreState.Requested,
                BackupId = "staged-backup",
                ArchiveFileName = "some-backup.m3undle-backup",
                UpdatedUtc = DateTime.UtcNow,
            });

            var result = await service.CreateAsync(CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.ErrorMessage!.Contains("staged", StringComparison.OrdinalIgnoreCase), result.ErrorMessage);
            Assert.AreEqual(0, service.List().Count);

            stateStore.Clear();
            var afterCancel = await service.CreateAsync(CancellationToken.None);
            Assert.IsTrue(afterCancel.Success, "Backups must work again once the staged restore is cleared.");
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Retention_UploadedArchives_DoNotEvictCreatedBackups()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture, out var tempDataDir, out var db);

        try
        {
            var backups = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                var result = await service.CreateAsync(CancellationToken.None);
                Assert.IsTrue(result.Success, result.ErrorMessage);
                backups.Add(Path.GetFileName(result.FilePath!));
                await Task.Delay(30);
            }

            var uploads = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                using var content = new MemoryStream([1, 2, 3]);
                uploads.Add(await service.SaveUploadedArchiveAsync(content, CancellationToken.None));
                await Task.Delay(30);
            }

            var remaining = service.List().Select(s => s.FileName).ToHashSet(StringComparer.Ordinal);

            foreach (var backup in backups)
                Assert.IsTrue(remaining.Contains(backup), $"Created backup '{backup}' must not be evicted by uploads.");

            Assert.IsFalse(remaining.Contains(uploads[0]), "The oldest upload beyond the upload budget must be removed.");
            foreach (var upload in uploads.Skip(1))
                Assert.IsTrue(remaining.Contains(upload), $"Upload '{upload}' is within the upload retention budget and must survive.");
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Retention_NeverDeletesTheArchiveAStagedRestoreReferences()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture, out var tempDataDir, out var db, out var stateStore);

        try
        {
            string stagedUpload;
            using (var content = new MemoryStream([1, 2, 3]))
                stagedUpload = await service.SaveUploadedArchiveAsync(content, CancellationToken.None);

            stateStore.Write(new RestoreStateMarker
            {
                State = RestoreState.Requested,
                BackupId = "staged",
                ArchiveFileName = stagedUpload,
                UpdatedUtc = DateTime.UtcNow,
            });

            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(30);
                using var content = new MemoryStream([1, 2, 3]);
                await service.SaveUploadedArchiveAsync(content, CancellationToken.None);
            }

            Assert.IsTrue(
                service.List().Any(s => s.FileName == stagedUpload),
                "The archive a staged restore references must survive retention even when it falls outside the upload budget.");
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolvePath_RejectsPathTraversalAndNonExistentNames()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture, out var tempDataDir, out var db);

        try
        {
            var result = await service.CreateAsync(CancellationToken.None);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            var fileName = Path.GetFileName(result.FilePath!);

            Assert.IsNotNull(service.ResolvePath(fileName));
            Assert.IsNull(service.ResolvePath("../" + fileName));
            Assert.IsNull(service.ResolvePath(Path.Combine("subdir", fileName)));
            Assert.IsNull(service.ResolvePath("does-not-exist.m3undle-backup"));
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Delete_RemovesAnExistingBackup_AndReturnsFalseForUnknownNames()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture, out var tempDataDir, out var db);

        try
        {
            var result = await service.CreateAsync(CancellationToken.None);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            var fileName = Path.GetFileName(result.FilePath!);

            Assert.IsFalse(service.Delete("does-not-exist.m3undle-backup"));
            Assert.IsTrue(service.Delete(fileName));
            Assert.IsFalse(File.Exists(result.FilePath));
            Assert.AreEqual(0, service.List().Count);
        }
        finally
        {
            await db.DisposeAsync();
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    private static PortableBackupService CreateService(
        TestFixture fixture, out string tempDataDir, out ApplicationDbContext db,
        out RestoreStateStore restoreStateStore, DestructiveOperationLock? destructiveOperationLock = null)
    {
        tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-portable-backup-test-{Guid.NewGuid():N}");
        var runtimePaths = new RuntimePaths(
            DataDirectory: tempDataDir,
            DatabasePath: string.Empty,
            DatabaseConnectionString: string.Empty,
            LogDirectory: string.Empty,
            SnapshotDirectory: string.Empty);

        db = fixture.CreateDbContext();
        var sqliteBackup = new SqliteBackupService(db, runtimePaths, NullLogger<SqliteBackupService>.Instance);
        var encryption = new SecretEncryptionService(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));
        var buildInfo = new AppBuildInfo("1.2.3-test", null, null, null);
        restoreStateStore = new RestoreStateStore(runtimePaths);

        return new PortableBackupService(
            db,
            sqliteBackup,
            runtimePaths,
            encryption,
            buildInfo,
            destructiveOperationLock ?? new DestructiveOperationLock(),
            restoreStateStore,
            NullLogger<PortableBackupService>.Instance);
    }

    private static PortableBackupService CreateService(
        TestFixture fixture, out string tempDataDir, out ApplicationDbContext db, DestructiveOperationLock? destructiveOperationLock = null)
        => CreateService(fixture, out tempDataDir, out db, out _, destructiveOperationLock);

    private static async Task<int> CountRowsAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static ExtractedArchive ExtractDatabase(string archivePath)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), $"m3undle-portable-backup-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(archivePath, extractDir);
        return new ExtractedArchive(extractDir);
    }

    private static PortableBackupReport ReadReport(ExtractedArchive extracted)
    {
        var json = File.ReadAllText(Path.Combine(extracted.Directory, "backup-report.json"));
        return JsonSerializer.Deserialize<PortableBackupReport>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static Provider SimpleProvider(string id) => new()
    {
        ProviderId = id,
        Name = id,
        PlaylistUrl = "http://example.invalid/playlist.m3u",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static Profile SimpleProfile(string id) => new()
    {
        ProfileId = id,
        Name = id,
        Enabled = true,
        IsActive = true,
        OutputName = id,
        MergeMode = "union",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static EpgSource SimpleEpgSource(string id) => new()
    {
        EpgSourceId = id,
        Name = id,
        Kind = "xmltv_url",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static FetchRun SimpleFetchRun(string id, string providerId) => new()
    {
        FetchRunId = id,
        ProviderId = providerId,
        StartedUtc = DateTime.UtcNow,
        Status = "ok",
        Type = "snapshot",
    };

    private static ProviderChannel SimpleProviderChannel(string id, string providerId, string lastFetchRunId) => new()
    {
        ProviderChannelId = id,
        ProviderId = providerId,
        DisplayName = id,
        StreamUrl = "http://example.invalid/stream",
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
        Active = true,
        LastFetchRunId = lastFetchRunId,
    };

    private static EpgFetchRun SimpleEpgFetchRun(string id, string epgSourceId) => new()
    {
        EpgFetchRunId = id,
        EpgSourceId = epgSourceId,
        StartedUtc = DateTime.UtcNow,
        Status = "ok",
    };

    private static SystemEvent SimpleSystemEvent(string id) => new()
    {
        SystemEventId = id,
        EventType = "test",
        Severity = "info",
        Title = "test event",
        OccurredAt = DateTime.UtcNow,
    };

    private static StreamChannelHealthEvent SimpleHealthEvent(string id, string providerId) => new()
    {
        StreamChannelHealthEventId = id,
        ProviderId = providerId,
        ProviderChannelId = "pc1",
        DisplayName = "Test Channel",
        EventKind = "stall",
        EventUtc = DateTime.UtcNow,
    };

    private static XtreamSeriesCache SimpleSeriesCache(string providerId) => new()
    {
        ProviderId = providerId,
        SeriesId = 1,
        LastModifiedEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        EpisodesJson = "[]",
    };

    private static ProviderGroup SimpleProviderGroup(string id, string providerId) => new()
    {
        ProviderGroupId = id,
        ProviderId = providerId,
        RawName = "Movies",
        ContentType = "vod",
        Active = true,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
    };

    private static CatalogItem SimpleCatalogItem(string id, string providerId, string providerGroupId) => new()
    {
        CatalogItemId = id,
        ProviderId = providerId,
        ProviderGroupId = providerGroupId,
        ProviderItemKey = "id:1",
        ContentType = "vod",
        Title = "Movie",
        Active = true,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
    };

    private static Snapshot SimpleSnapshot(string id, string profileId) => new()
    {
        SnapshotId = id,
        ProfileId = profileId,
        CreatedUtc = DateTime.UtcNow,
        Status = "ok",
        PlaylistPath = "playlist.m3u",
        XmltvPath = "guide.xml",
        ChannelIndexPath = "index.json",
        StatusJsonPath = "status.json",
    };

    private sealed class ExtractedArchive(string directory) : IDisposable
    {
        public string Directory { get; } = directory;
        public string DatabasePath => Path.Combine(Directory, "configuration.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    // Real temp-file SQLite database — VACUUM INTO does not produce a file against SQLite's
    // named in-memory/shared-cache mode (see the same note in EncryptionRotationServiceTests).
    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"m3undle-portable-backup-src-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        await using (var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestFixture(dbPath, connectionString);
    }

    private sealed class TestFixture(string dbPath, string connectionString) : IAsyncDisposable
    {
        public ApplicationDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(dbPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup — a lingering handle shouldn't fail the test.
            }

            return ValueTask.CompletedTask;
        }
    }
}
