using M3Undle.Web.Application;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class SqliteBackupServiceTests
{
    [TestMethod]
    public async Task BackupAsync_CalledTwiceInImmediateSuccession_ProducesDistinctFiles()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"m3undle-backup-test-{Guid.NewGuid():N}.db");
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-backup-test-data-{Guid.NewGuid():N}");
        var connectionString = $"Data Source={dbPath}";

        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options);
        await db.Database.EnsureCreatedAsync();

        var runtimePaths = new RuntimePaths(
            DataDirectory: tempDataDir,
            DatabasePath: dbPath,
            DatabaseConnectionString: connectionString,
            LogDirectory: string.Empty,
            SnapshotDirectory: string.Empty);
        var service = new SqliteBackupService(db, runtimePaths, NullLogger<SqliteBackupService>.Instance);

        try
        {
            // Two calls back-to-back can land within the same second (or even millisecond) —
            // the filename must still be unique so the second backup doesn't fail or overwrite
            // the first (see SqliteBackupService.BackupAsync's uniqueSuffix).
            var firstPath = await service.BackupAsync(CancellationToken.None);
            var secondPath = await service.BackupAsync(CancellationToken.None);

            Assert.AreNotEqual(firstPath, secondPath);
            Assert.IsTrue(File.Exists(firstPath));
            Assert.IsTrue(File.Exists(secondPath));
        }
        finally
        {
            await db.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDataDir))
                Directory.Delete(tempDataDir, recursive: true);
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }
}
