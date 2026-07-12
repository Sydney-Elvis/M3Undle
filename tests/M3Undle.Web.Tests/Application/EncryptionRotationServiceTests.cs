using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Text;

namespace M3Undle.Web.Tests.Application;

// Mutates process-wide M3UNDLE_ENCRYPTION_KEY(S) environment variables via EnvScope; the
// assembly parallelizes at method level (GlobalTestSettings.cs), so this must run
// sequentially relative to other [DoNotParallelize] tests (see SecretEncryptionServiceTests).
[TestClass]
[DoNotParallelize]
public sealed class EncryptionRotationServiceTests
{
    [TestMethod]
    public async Task GetStatusAsync_ReportsCountsByActiveKeyBeforeRotation()
    {
        var oldKey = RandomKey();
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1", EncryptLegacyFormat("pw1", oldKey)));
            setup.DownstreamIntegrations.Add(SimpleIntegration("d1", EncryptLegacyFormat("key1", oldKey)));
            await setup.SaveChangesAsync();
        }

        using var env = new EnvScope(keys: $"new:{RandomKey()},old:{oldKey}");
        var rotation = CreateRotationService(fixture, out _);

        var status = await rotation.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual("new", status.ActiveKeyId);
        Assert.AreEqual(0, status.ProvidersOnActiveKey);
        Assert.AreEqual(1, status.ProvidersOnOtherKey);
        Assert.AreEqual(0, status.DownstreamIntegrationsOnActiveKey);
        Assert.AreEqual(1, status.DownstreamIntegrationsOnOtherKey);
    }

    [TestMethod]
    public async Task RotateAsync_MigratesBothTables_AndCreatesBackup()
    {
        var oldKey = RandomKey();
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1", EncryptLegacyFormat("pw1", oldKey)));
            setup.DownstreamIntegrations.Add(SimpleIntegration("d1", EncryptLegacyFormat("key1", oldKey)));
            await setup.SaveChangesAsync();
        }

        using var env = new EnvScope(keys: $"new:{RandomKey()},old:{oldKey}");
        var rotation = CreateRotationService(fixture, out var tempDataDir);

        try
        {
            var result = await rotation.RotateAsync(CancellationToken.None);

            Assert.IsTrue(result.Success);
            Assert.AreEqual("new", result.ActiveKeyId);
            Assert.AreEqual(1, result.ProvidersMigrated);
            Assert.AreEqual(1, result.DownstreamIntegrationsMigrated);
            Assert.AreEqual(0, result.RowsAlreadyCurrent);
            Assert.IsNotNull(result.BackupFilePath);
            Assert.IsTrue(File.Exists(result.BackupFilePath), "Rotation must back up the database before mutating rows.");

            await using var verify = fixture.CreateDbContext();
            var provider = await verify.Providers.SingleAsync(p => p.ProviderId == "p1");
            var integration = await verify.DownstreamIntegrations.SingleAsync(d => d.DownstreamIntegrationId == "d1");

            var encryption = CreateService();
            Assert.AreEqual("new", encryption.Peek(provider.XtreamEncryptedPassword!).KeyId);
            Assert.AreEqual("new", encryption.Peek(integration.ApiKeyEncrypted!).KeyId);
            Assert.AreEqual("pw1", encryption.Decrypt(provider.XtreamEncryptedPassword!));
            Assert.AreEqual("key1", encryption.Decrypt(integration.ApiKeyEncrypted!));
        }
        finally
        {
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RotateAsync_CalledAgainAfterFullMigration_IsNoOpAndSkipsBackup()
    {
        var oldKey = RandomKey();
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1", EncryptLegacyFormat("pw1", oldKey)));
            await setup.SaveChangesAsync();
        }

        using var env = new EnvScope(keys: $"new:{RandomKey()},old:{oldKey}");
        var rotation = CreateRotationService(fixture, out var tempDataDir);

        try
        {
            var first = await rotation.RotateAsync(CancellationToken.None);
            Assert.IsTrue(first.Success);
            Assert.AreEqual(1, first.ProvidersMigrated);

            var second = await rotation.RotateAsync(CancellationToken.None);

            Assert.IsTrue(second.Success);
            Assert.AreEqual(0, second.ProvidersMigrated);
            Assert.AreEqual(0, second.DownstreamIntegrationsMigrated);
            Assert.AreEqual(1, second.RowsAlreadyCurrent);
            Assert.IsNull(second.BackupFilePath, "A no-op rotate (nothing to migrate) should skip taking a backup.");
        }
        finally
        {
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RotateAsync_RowUndecryptableByAnyRingKey_RollsBackEntireTransaction()
    {
        var oldKey = RandomKey();
        var lostKey = RandomKey(); // Never added to the ring below — simulates a genuinely lost/corrupt key.
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            // Seed the bad row FIRST so a naive per-row-commit implementation would have
            // already migrated the good row if it didn't process bad rows atomically.
            setup.Providers.Add(SimpleProvider("bad", EncryptLegacyFormat("unrecoverable", lostKey)));
            setup.Providers.Add(SimpleProvider("good", EncryptLegacyFormat("pw1", oldKey)));
            await setup.SaveChangesAsync();
        }

        using var env = new EnvScope(keys: $"new:{RandomKey()},old:{oldKey}");
        var rotation = CreateRotationService(fixture, out var tempDataDir);

        try
        {
            var result = await rotation.RotateAsync(CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(0, result.ProvidersMigrated);

            await using var verify = fixture.CreateDbContext();
            var good = await verify.Providers.SingleAsync(p => p.ProviderId == "good");
            var encryption = CreateService();
            Assert.IsTrue(encryption.Peek(good.XtreamEncryptedPassword!).IsLegacyFormat,
                "The good row must NOT have been migrated — rotation is all-or-nothing.");
        }
        finally
        {
            Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RotateAsync_NoEncryptionKeyConfigured_FailsWithoutTouchingRowsOrTakingBackup()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1", EncryptLegacyFormat("pw1", RandomKey())));
            await setup.SaveChangesAsync();
        }

        using var env = new EnvScope(keys: null, key: null);
        var rotation = CreateRotationService(fixture, out var tempDataDir);

        try
        {
            var result = await rotation.RotateAsync(CancellationToken.None);

            Assert.IsFalse(result.Success);
            Assert.IsNull(result.ActiveKeyId);
            Assert.IsNull(result.BackupFilePath);
            Assert.AreEqual(0, result.ProvidersMigrated);

            var backupDir = Path.Combine(tempDataDir, "backups");
            Assert.IsFalse(Directory.Exists(backupDir),
                "No encryption key configured means rotation must fail fast, before ever taking a backup.");
        }
        finally
        {
            if (Directory.Exists(tempDataDir))
                Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetStatusAsync_NoEncryptionKeyConfigured_ReportsUnavailable()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Providers.Add(SimpleProvider("p1", EncryptLegacyFormat("pw1", RandomKey())));
            await setup.SaveChangesAsync();
        }

        using var env = new EnvScope(keys: null, key: null);
        var rotation = CreateRotationService(fixture, out _);

        var status = await rotation.GetStatusAsync(CancellationToken.None);

        Assert.IsFalse(status.EncryptionAvailable);
        Assert.IsNull(status.ActiveKeyId);
        Assert.AreEqual(0, status.KnownKeyIds.Count);
        // Peek() still parses the legacy/other-key row without needing a configured key, so the
        // row shows up as "on other key" rather than throwing.
        Assert.AreEqual(1, status.ProvidersOnOtherKey);
    }

    private static EncryptionRotationService CreateRotationService(TestFixture fixture, out string tempDataDir)
    {
        tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-encryption-test-{Guid.NewGuid():N}");
        var runtimePaths = new RuntimePaths(
            DataDirectory: tempDataDir,
            DatabasePath: string.Empty,
            DatabaseConnectionString: string.Empty,
            LogDirectory: string.Empty,
            SnapshotDirectory: string.Empty);

        var db = fixture.CreateDbContext();
        var encryption = CreateService();
        var backup = new SqliteBackupService(db, runtimePaths, NullLogger<SqliteBackupService>.Instance);
        return new EncryptionRotationService(db, encryption, backup, NullLogger<EncryptionRotationService>.Instance);
    }

    private static SecretEncryptionService CreateService()
    {
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        return new SecretEncryptionService(envVars);
    }

    private static Provider SimpleProvider(string id, string encryptedPassword) => new()
    {
        ProviderId = id,
        Name = id,
        PlaylistUrl = string.Empty,
        XtreamEncryptedPassword = encryptedPassword,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static DownstreamIntegration SimpleIntegration(string id, string encryptedApiKey) => new()
    {
        DownstreamIntegrationId = id,
        Name = id,
        Kind = "jellyfin",
        BaseUrl = "http://localhost/",
        ApiKeyEncrypted = encryptedApiKey,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// Replicates today's pre-key-ring wire format (bare Base64 of nonce+tag+ciphertext, no
    /// prefix, no AAD) independent of SecretEncryptionService, to simulate data written before
    /// this feature existed — matches the helper in SecretEncryptionServiceTests.
    /// </summary>
    private static string EncryptLegacyFormat(string plaintext, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        ciphertext.CopyTo(payload, nonce.Length + tag.Length);
        return Convert.ToBase64String(payload);
    }

    private static string RandomKey()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    // A real temp-file SQLite database, not the shared-cache in-memory pattern used elsewhere
    // in this suite (e.g. DownstreamNotificationServiceTests.cs): VACUUM INTO — which
    // SqliteBackupService relies on — does not produce a file when run against SQLite's
    // named in-memory/shared-cache mode (confirmed empirically; ExecuteNonQuery returns 0,
    // no exception, no file). Production always runs against a real file (RuntimePaths
    // resolves a real DataSource path), so a temp file here is actually the more faithful test.
    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"m3undle-rotation-test-{Guid.NewGuid():N}.db");
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

    /// <summary>
    /// Saves/restores M3UNDLE_ENCRYPTION_KEY(S) around a test — same pattern as
    /// SecretEncryptionServiceTests.EnvScope (duplicated per the suite's existing convention
    /// of not sharing test fixtures across files).
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
}
