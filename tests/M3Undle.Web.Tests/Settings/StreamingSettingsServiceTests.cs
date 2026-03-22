using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Settings;

[TestClass]
public sealed class StreamingSettingsServiceTests
{
    [TestMethod]
    public async Task UpdateAsync_PersistsValues_AndMarksRestartRequired()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        var result = await service.UpdateAsync(new UpdateStreamingSettingsCommand(
            StreamingEnabled: false,
            MaxConcurrentSessions: 77,
            IdleGraceSeconds: 21,
            IdleGraceHardCapSeconds: 150,
            BufferMaxBytesPerSession: 8 * 1024 * 1024,
            BufferMaxBytesHardCap: 48 * 1024 * 1024,
            BufferReadChunkSizeBytes: 64 * 1024,
            ReconnectReadStallTimeoutSeconds: 45,
            ReconnectOutageWindowSeconds: 90,
            ReconnectConnectTimeoutSeconds: 20));

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.Changed);
        Assert.IsTrue(result.RestartRequired);

        var state = await service.GetSettingsAsync();
        Assert.IsFalse(state.Saved.StreamingEnabled);
        Assert.AreEqual(77, state.Saved.MaxConcurrentSessions);
        Assert.AreEqual(21, state.Saved.IdleGraceSeconds);
        Assert.AreEqual(150, state.Saved.IdleGraceHardCapSeconds);
        Assert.AreEqual(8 * 1024 * 1024, state.Saved.BufferMaxBytesPerSession);
        Assert.AreEqual(48 * 1024 * 1024, state.Saved.BufferMaxBytesHardCap);
        Assert.AreEqual(64 * 1024, state.Saved.BufferReadChunkSizeBytes);
        Assert.AreEqual(45, state.Saved.ReconnectReadStallTimeoutSeconds);
        Assert.AreEqual(90, state.Saved.ReconnectOutageWindowSeconds);
        Assert.AreEqual(20, state.Saved.ReconnectConnectTimeoutSeconds);
        Assert.IsTrue(state.RestartRequired);

        Assert.IsTrue(state.Applied.StreamingEnabled);
        Assert.AreEqual(50, state.Applied.MaxConcurrentSessions);
    }

    [TestMethod]
    public async Task UpdateAsync_InvalidValues_ReturnsValidationError_AndLeavesStateUnchanged()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        var result = await service.UpdateAsync(new UpdateStreamingSettingsCommand(
            StreamingEnabled: true,
            MaxConcurrentSessions: 0,
            IdleGraceSeconds: 30,
            IdleGraceHardCapSeconds: 10,
            BufferMaxBytesPerSession: 1024,
            BufferMaxBytesHardCap: 512,
            BufferReadChunkSizeBytes: 0,
            ReconnectReadStallTimeoutSeconds: 50,
            ReconnectOutageWindowSeconds: 10,
            ReconnectConnectTimeoutSeconds: 0));

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Changed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error));

        var state = await service.GetSettingsAsync();
        Assert.IsTrue(state.Saved.StreamingEnabled);
        Assert.AreEqual(50, state.Saved.MaxConcurrentSessions);
        Assert.IsFalse(state.RestartRequired);
    }

    [TestMethod]
    public async Task ClearRestartRequiredAsync_ClearsPendingFlagOnStartup()
    {
        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        await service.UpdateAsync(new UpdateStreamingSettingsCommand(
            StreamingEnabled: true,
            MaxConcurrentSessions: 60,
            IdleGraceSeconds: 15,
            IdleGraceHardCapSeconds: 120,
            BufferMaxBytesPerSession: 4 * 1024 * 1024,
            BufferMaxBytesHardCap: 32 * 1024 * 1024,
            BufferReadChunkSizeBytes: 32 * 1024,
            ReconnectReadStallTimeoutSeconds: 30,
            ReconnectOutageWindowSeconds: 75,
            ReconnectConnectTimeoutSeconds: 15));

        await service.ClearRestartRequiredAsync();

        var state = await service.GetSettingsAsync();
        Assert.IsFalse(state.RestartRequired);
        Assert.AreEqual(60, state.Saved.MaxConcurrentSessions);
    }

    [TestMethod]
    public async Task UpdateAsync_SavedSettings_AreAppliedOnNextServiceConstruction()
    {
        // Checklist: After restart, saved settings are applied to runtime behavior.
        var tempDir = Path.Combine(Path.GetTempPath(), $"m3undle-stream-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "settings.db");
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            await using (var setupDb = new ApplicationDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
            }

            await using (var firstDb = new ApplicationDbContext(options))
            {
                var firstService = CreateService(firstDb);
                var update = await firstService.UpdateAsync(new UpdateStreamingSettingsCommand(
                    StreamingEnabled: true,
                    MaxConcurrentSessions: 61,
                    IdleGraceSeconds: 27,
                    IdleGraceHardCapSeconds: 150,
                    BufferMaxBytesPerSession: 6 * 1024 * 1024,
                    BufferMaxBytesHardCap: 40 * 1024 * 1024,
                    BufferReadChunkSizeBytes: 48 * 1024,
                    ReconnectReadStallTimeoutSeconds: 41,
                    ReconnectOutageWindowSeconds: 95,
                    ReconnectConnectTimeoutSeconds: 18));

                Assert.IsTrue(update.Succeeded);
                Assert.IsTrue(update.Changed);
            }

            await using (var secondDb = new ApplicationDbContext(options))
            {
                var secondService = CreateService(secondDb);
                var state = await secondService.GetSettingsAsync();

                Assert.AreEqual(61, state.Saved.MaxConcurrentSessions);
                Assert.AreEqual(27, state.Saved.IdleGraceSeconds);
                Assert.AreEqual(48 * 1024, state.Saved.BufferReadChunkSizeBytes);
                Assert.AreEqual(41, state.Saved.ReconnectReadStallTimeoutSeconds);
                Assert.IsTrue(state.RestartRequired);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static StreamingSettingsService CreateService(ApplicationDbContext db)
        => new(
            db,
            Options.Create(new StreamProxyOptions()),
            Options.Create(new BufferOptions()),
            Options.Create(new ReconnectOptions()));

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var fixture = new TestFixture(connection, options);

        await using var db = fixture.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        return fixture;
    }

    private sealed class TestFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options) : IAsyncDisposable
    {
        public ApplicationDbContext CreateDbContext() => new(options);

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
