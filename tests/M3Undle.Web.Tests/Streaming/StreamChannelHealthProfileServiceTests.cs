using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class StreamChannelHealthProfileServiceTests
{
    [TestMethod]
    public async Task GetRecoveryPolicyAsync_RepeatedAbortAfterRecovery_DerivesUnstablePolicy()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true));

        var policy = await fixture.Service.GetRecoveryPolicyAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions
            {
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(3),
                RecoverySafeStartSearchLimitBytes = 512 * 1024,
                AllowPacketBoundaryRecoveryFallback = true,
            });

        Assert.AreEqual(StreamChannelHealthProfile.Unstable, policy.Profile);
        Assert.IsFalse(policy.AllowPacketBoundaryRecoveryFallback);
        Assert.IsTrue(policy.RecoveryOutputHoldLimit >= TimeSpan.FromSeconds(5));
        Assert.IsTrue(policy.RecoverySafeStartSearchLimitBytes >= 2 * 1024 * 1024);
    }

    [TestMethod]
    public async Task GetRecoveryPolicyAsync_NoHealthEvents_UsesConfiguredFallbackPolicy()
    {
        await using var fixture = await ProfileFixture.CreateAsync();

        var policy = await fixture.Service.GetRecoveryPolicyAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions
            {
                RecoveryOutputHoldLimit = TimeSpan.FromSeconds(3),
                RecoverySafeStartSearchLimitBytes = 512 * 1024,
                AllowPacketBoundaryRecoveryFallback = true,
            });

        Assert.AreEqual(StreamChannelHealthProfile.Fast, policy.Profile);
        Assert.IsTrue(policy.AllowPacketBoundaryRecoveryFallback);
        Assert.AreEqual(TimeSpan.FromSeconds(3), policy.RecoveryOutputHoldLimit);
        Assert.AreEqual(512 * 1024, policy.RecoverySafeStartSearchLimitBytes);
    }

    private static StreamChannelHealthEvent CreateHealthEvent(
        string eventKind,
        bool clientAbortAfterRecovery = false,
        string? safeStartKind = null)
        => new()
        {
            StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
            ProviderId = "provider-1",
            ProviderChannelId = "channel-1",
            DisplayName = "Test Channel",
            EventKind = eventKind,
            EventUtc = DateTime.UtcNow.AddMinutes(-5),
            ClientAbortAfterRecovery = clientAbortAfterRecovery,
            SafeStartKind = safeStartKind,
        };

    private sealed class ProfileFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        private ProfileFixture(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            StreamChannelHealthProfileService service)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
            Service = service;
        }

        public StreamChannelHealthProfileService Service { get; }

        public static async Task<ProfileFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(_ => _.UseSqlite(connection));
            var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var service = new StreamChannelHealthProfileService(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<StreamChannelHealthProfileService>.Instance);

            return new ProfileFixture(connection, serviceProvider, service);
        }

        public async Task SeedAsync(params StreamChannelHealthEvent[] events)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.StreamChannelHealthEvents.AddRange(events);
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
