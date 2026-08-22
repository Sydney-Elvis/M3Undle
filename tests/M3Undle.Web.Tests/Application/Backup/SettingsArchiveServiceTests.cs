using M3Undle.Web.Application;
using M3Undle.Web.Application.Backup;
using M3Undle.Core;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application.Backup;

[TestClass]
[DoNotParallelize]
public sealed class SettingsArchiveServiceTests
{
    private const string Passphrase = "settings-archive-test-passphrase";

    [TestMethod]
    public async Task ExportThenImportAsync_RestoresSettingsGraphWithNewTargetIds()
    {
        using var keyScope = new EncryptionKeyScope(key: RandomKey());
        await using var source = await TestDatabase.CreateAsync();
        await using var target = await TestDatabase.CreateAsync();
        var sourceEncryption = CreateEncryption();

        await using (var sourceDb = source.CreateContext())
        {
            var settings = await sourceDb.SiteSettings.SingleAsync(x => x.Id == 1);
            settings.StreamingEnabled = false;
            settings.HdhrFriendlyName = "Lab HDHR";
            settings.AuthenticationEnabled = true;
            settings.EndpointSecurityEnabled = true;
            settings.StreamingSettingsRestartRequired = true;
            settings.BackupScheduleEnabled = true;

            sourceDb.Providers.Add(new Provider
            {
                ProviderId = "source-provider", Name = "Source Provider", Enabled = true,
                PlaylistUrl = "http://provider.invalid/playlist", HeadersJson = "{\"Authorization\":\"Bearer lab\"}",
                TimeoutSeconds = 42, IncludeVod = true, IncludeSeries = true, ForceMpegTs = true, CleanRelayMode = "always",
                XtreamBaseUrl = "http://provider.invalid", XtreamUsername = "lab", XtreamEncryptedPassword = sourceEncryption.Encrypt("provider-password"),
                XtreamIncludeXmltv = true, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            sourceDb.Profiles.Add(new Profile
            {
                ProfileId = "source-profile", Name = "Source Profile", Enabled = true, IsActive = true,
                OutputName = "m3undle", MergeMode = "union", RefreshScheduleKindOverride = "12h",
                RefreshStartupCatchupOverride = false, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            sourceDb.ProfileProviders.Add(new ProfileProvider { ProfileId = "source-profile", ProviderId = "source-provider", Priority = 4, Enabled = true });
            sourceDb.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "source-integration", ProfileId = "source-profile", Name = "Source Webhook", Kind = "webhook",
                BaseUrl = "http://receiver.invalid/notify", ApiKeyEncrypted = sourceEncryption.Encrypt("api-key"),
                WebhookHeadersJson = "{\"X-Lab-Token\":\"synthetic\"}", TriggerOnLineupUpdate = true, TriggerOnGuideUpdate = false,
                Enabled = true, LastNotifiedUtc = DateTime.UtcNow, LastNotifyError = "old delivery failure", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            await sourceDb.SaveChangesAsync();
        }

        await using var sourceDbForService = source.CreateContext();
        var sourceService = CreateService(sourceDbForService, source.DataDirectory, sourceEncryption);
        var exported = await sourceService.CreateAsync(Passphrase, CancellationToken.None);

        Assert.IsTrue(exported.Success, exported.ErrorMessage);
        Assert.AreEqual("settings", exported.Manifest!.Scope);
        Assert.AreEqual("legacy", exported.Manifest.EncryptionKeyId);

        await using var targetDbForService = target.CreateContext();
        var targetEncryption = CreateEncryption();
        var targetService = CreateService(targetDbForService, target.DataDirectory, targetEncryption);
        var preflight = await targetService.PreflightAsync(exported.FilePath!, Passphrase, CancellationToken.None);
        Assert.IsTrue(preflight.Success, string.Join(" ", preflight.Errors));

        await using (var targetSetup = target.CreateContext())
        {
            var targetSettings = await targetSetup.SiteSettings.SingleAsync(x => x.Id == 1);
            targetSettings.AuthenticationEnabled = false;
            targetSettings.EndpointSecurityEnabled = false;
            targetSettings.StreamingSettingsRestartRequired = false;
            targetSettings.BackupScheduleEnabled = false;
            await targetSetup.SaveChangesAsync();
        }

        var imported = await targetService.ApplyAsync(exported.FilePath!, Passphrase, CancellationToken.None);
        Assert.IsTrue(imported.Success, string.Join(" ", imported.Errors));
        Assert.AreEqual(1, imported.AppliedCounts["Providers"]);
        Assert.AreEqual(1, imported.AppliedCounts["DownstreamIntegrations"]);

        await using var verification = target.CreateContext();
        var provider = await verification.Providers.SingleAsync();
        var profile = await verification.Profiles.SingleAsync();
        var link = await verification.ProfileProviders.SingleAsync();
        var integration = await verification.DownstreamIntegrations.SingleAsync();
        var targetSettingsAfter = await verification.SiteSettings.SingleAsync(x => x.Id == 1);

        Assert.AreNotEqual("source-provider", provider.ProviderId);
        Assert.AreNotEqual("source-profile", profile.ProfileId);
        Assert.AreEqual(provider.ProviderId, link.ProviderId);
        Assert.AreEqual(profile.ProfileId, link.ProfileId);
        Assert.AreEqual(profile.ProfileId, integration.ProfileId);
        Assert.AreEqual("provider-password", targetEncryption.Decrypt(provider.XtreamEncryptedPassword!));
        Assert.AreEqual("api-key", targetEncryption.Decrypt(integration.ApiKeyEncrypted!));
        Assert.AreEqual("{\"X-Lab-Token\":\"synthetic\"}", integration.WebhookHeadersJson);
        Assert.IsNull(integration.LastNotifiedUtc);
        Assert.IsNull(integration.LastNotifyError);
        Assert.IsFalse(targetSettingsAfter.StreamingEnabled);
        Assert.AreEqual("Lab HDHR", targetSettingsAfter.HdhrFriendlyName);
        Assert.IsFalse(targetSettingsAfter.AuthenticationEnabled);
        Assert.IsFalse(targetSettingsAfter.EndpointSecurityEnabled);
        Assert.IsFalse(targetSettingsAfter.StreamingSettingsRestartRequired);
        Assert.IsFalse(targetSettingsAfter.BackupScheduleEnabled);
    }

    [TestMethod]
    public async Task ApplyAsync_TargetWithGlobalIntegration_IsRejectedWithoutChangingSiteSettings()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using var target = await TestDatabase.CreateAsync();
        await using (var sourceDb = source.CreateContext())
        {
            sourceDb.Providers.Add(SimpleProvider("source"));
            await sourceDb.SaveChangesAsync();
        }

        await using var sourceDbForService = source.CreateContext();
        var sourceService = CreateService(sourceDbForService, source.DataDirectory, CreateEncryption());
        var exported = await sourceService.CreateAsync(Passphrase, CancellationToken.None);
        Assert.IsTrue(exported.Success, exported.ErrorMessage);

        await using (var targetSetup = target.CreateContext())
        {
            targetSetup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "existing", Name = "Existing", Kind = "webhook", BaseUrl = "http://existing.invalid",
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
            await targetSetup.SaveChangesAsync();
        }

        await using var targetDbForService = target.CreateContext();
        var targetService = CreateService(targetDbForService, target.DataDirectory, CreateEncryption());
        var imported = await targetService.ApplyAsync(exported.FilePath!, Passphrase, CancellationToken.None);

        Assert.IsFalse(imported.Success);
        Assert.IsTrue(imported.Errors.Single().Contains("downstream integrations", StringComparison.OrdinalIgnoreCase));
        await using var verification = target.CreateContext();
        Assert.AreEqual(0, await verification.Providers.CountAsync());
        Assert.AreEqual(1, await verification.DownstreamIntegrations.CountAsync());
    }

    [TestMethod]
    public async Task ExportAsync_RewrapsOldCiphertextUnderTheActiveKey()
    {
        var oldKey = RandomKey();
        var activeKey = RandomKey();
        await using var source = await TestDatabase.CreateAsync();
        using (var oldScope = new EncryptionKeyScope(keys: $"old:{oldKey}"))
        {
            var oldEncryption = CreateEncryption();
            await using var setup = source.CreateContext();
            var provider = SimpleProvider("source");
            provider.XtreamEncryptedPassword = oldEncryption.Encrypt("old-secret");
            setup.Providers.Add(provider);
            await setup.SaveChangesAsync();
        }

        using var activeScope = new EncryptionKeyScope(keys: $"active:{activeKey},old:{oldKey}");
        await using var sourceDbForService = source.CreateContext();
        var sourceService = CreateService(sourceDbForService, source.DataDirectory, CreateEncryption());
        var exported = await sourceService.CreateAsync(Passphrase, CancellationToken.None);
        Assert.IsTrue(exported.Success, exported.ErrorMessage);
        Assert.AreEqual("active", exported.Manifest!.EncryptionKeyId);

        await using var target = await TestDatabase.CreateAsync();
        using var targetScope = new EncryptionKeyScope(keys: $"active:{activeKey}");
        await using var targetDbForService = target.CreateContext();
        var targetEncryption = CreateEncryption();
        var targetService = CreateService(targetDbForService, target.DataDirectory, targetEncryption);
        var imported = await targetService.ApplyAsync(exported.FilePath!, Passphrase, CancellationToken.None);

        Assert.IsTrue(imported.Success, string.Join(" ", imported.Errors));
        await using var verification = target.CreateContext();
        Assert.AreEqual("old-secret", targetEncryption.Decrypt((await verification.Providers.SingleAsync()).XtreamEncryptedPassword!));
    }

    [TestMethod]
    public async Task PreflightAsync_WrongPassphraseAndTamperingFailWithTheSameMessage()
    {
        await using var source = await TestDatabase.CreateAsync();
        await using (var setup = source.CreateContext())
        {
            var provider = SimpleProvider("source");
            provider.HeadersJson = "{\"Authorization\":\"Bearer synthetic-secret\"}";
            setup.Providers.Add(provider);
            await setup.SaveChangesAsync();
        }

        await using var sourceDb = source.CreateContext();
        var service = CreateService(sourceDb, source.DataDirectory, CreateEncryption());
        var exported = await service.CreateAsync(Passphrase, CancellationToken.None);
        Assert.IsTrue(exported.Success, exported.ErrorMessage);
        var archiveText = await File.ReadAllTextAsync(exported.FilePath!);
        Assert.IsFalse(archiveText.Contains("synthetic-secret", StringComparison.Ordinal));
        Assert.IsFalse(archiveText.Contains("SettingsEntities", StringComparison.Ordinal));

        var wrongPassphrase = await service.PreflightAsync(exported.FilePath!, "different-settings-archive-passphrase", CancellationToken.None);
        Assert.IsFalse(wrongPassphrase.Success);
        Assert.AreEqual("Unable to decrypt settings archive.", wrongPassphrase.Errors.Single());

        var archive = System.Text.Json.JsonSerializer.Deserialize<EncryptedSettingsArchive>(archiveText)!;
        var tamperedCiphertext = archive.Ciphertext[0] == 'A'
            ? "B" + archive.Ciphertext[1..]
            : "A" + archive.Ciphertext[1..];
        await File.WriteAllTextAsync(exported.FilePath!, System.Text.Json.JsonSerializer.Serialize(archive with { Ciphertext = tamperedCiphertext }));

        var tampered = await service.PreflightAsync(exported.FilePath!, Passphrase, CancellationToken.None);
        Assert.IsFalse(tampered.Success);
        Assert.AreEqual(wrongPassphrase.Errors.Single(), tampered.Errors.Single());
    }

    private static SettingsArchiveService CreateService(ApplicationDbContext db, string dataDirectory, SecretEncryptionService encryption)
        => new(db, new RuntimePaths(dataDirectory, string.Empty, string.Empty, string.Empty, string.Empty), encryption,
            new AppBuildInfo("1.2.3-test", null, null, null), NullLogger<SettingsArchiveService>.Instance);

    private static SecretEncryptionService CreateEncryption()
        => new(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance));

    private static Provider SimpleProvider(string id) => new()
    {
        ProviderId = id, Name = id, PlaylistUrl = "http://provider.invalid/playlist", CleanRelayMode = "auto",
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };

    private static string RandomKey()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private sealed class TestDatabase(string databasePath, string connectionString, string dataDirectory) : IAsyncDisposable
    {
        public string DataDirectory { get; } = dataDirectory;

        public static async Task<TestDatabase> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"m3undle-settings-archive-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={databasePath}";
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"m3undle-settings-archive-data-{Guid.NewGuid():N}");
            await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(databasePath, connectionString, dataDirectory);
        }

        public ApplicationDbContext CreateContext()
            => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EncryptionKeyScope : IDisposable
    {
        private readonly string? _previousKey = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY");
        private readonly string? _previousKeys = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS");

        public EncryptionKeyScope(string? keys = null, string? key = null)
        {
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
