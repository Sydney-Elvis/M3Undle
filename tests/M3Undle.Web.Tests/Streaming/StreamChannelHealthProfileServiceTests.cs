using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Streaming.Configuration;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Upstream;
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

        Assert.AreEqual(StreamChannelHealthProfile.Stable, policy.Profile);
        Assert.IsTrue(policy.AllowPacketBoundaryRecoveryFallback);
        Assert.AreEqual(TimeSpan.FromSeconds(3), policy.RecoveryOutputHoldLimit);
        Assert.AreEqual(512 * 1024, policy.RecoverySafeStartSearchLimitBytes);
    }

    [TestMethod]
    public async Task GetRelayPolicyDecision_AutoUnstable_SelectsCleanRemux()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var policy = new StreamChannelRecoveryPolicy(
            StreamChannelHealthProfile.Unstable,
            TimeSpan.FromSeconds(5),
            2 * 1024 * 1024,
            AllowPacketBoundaryRecoveryFallback: false,
            RequireDownstreamRetune: true,
            DownstreamRetuneReason: "test",
            Reason: "unstable test profile");

        var decision = fixture.Service.GetRelayPolicyDecision("auto", policy);

        Assert.AreEqual("auto", decision.ProviderRelayPolicy);
        Assert.AreEqual(UpstreamRelayModes.FfmpegCleanRemux, decision.SelectedRelayMode);
        StringAssert.Contains(decision.Reason, "Unstable");
    }

    [TestMethod]
    public async Task GetRelayPolicyDecision_OffUnstable_ForcesDirect()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        var policy = new StreamChannelRecoveryPolicy(
            StreamChannelHealthProfile.Unstable,
            TimeSpan.FromSeconds(5),
            2 * 1024 * 1024,
            AllowPacketBoundaryRecoveryFallback: false,
            RequireDownstreamRetune: true,
            DownstreamRetuneReason: "test",
            Reason: "unstable test profile");

        var decision = fixture.Service.GetRelayPolicyDecision("off", policy);

        Assert.AreEqual("off", decision.ProviderRelayPolicy);
        Assert.AreEqual(UpstreamRelayModes.Direct, decision.SelectedRelayMode);
    }

    [TestMethod]
    public async Task GetRecoveryPolicyAsync_CleanWatchAfterAdverseEvent_DecaysUnstableToCautious()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true, age: TimeSpan.FromHours(2)),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true, age: TimeSpan.FromHours(2)),
            CreateHealthEvent("CleanWatchCompleted", cleanWatchDurationMs: TimeSpan.FromMinutes(30).TotalMilliseconds));

        var policy = await fixture.Service.GetRecoveryPolicyAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions());
        var evidence = await fixture.Service.GetEvidenceAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthProfile.Cautious, policy.Profile);
        Assert.AreEqual(1, evidence.CleanWatchEvents);
        Assert.AreEqual(TimeSpan.FromMinutes(30), evidence.CleanWatchDuration);
        Assert.AreEqual(StreamChannelHealthProfile.Cautious, evidence.RecoveryPolicy.Profile);
    }

    [TestMethod]
    public async Task GetRecoveryPolicyAsync_CleanWatchBeforeAdverseEvent_DoesNotDecayUnstable()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("CleanWatchCompleted", cleanWatchDurationMs: TimeSpan.FromMinutes(60).TotalMilliseconds, age: TimeSpan.FromHours(2)),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true));

        var evidence = await fixture.Service.GetEvidenceAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthProfile.Unstable, evidence.RecoveryPolicy.Profile);
        Assert.AreEqual(0, evidence.CleanWatchEvents);
        Assert.AreEqual(TimeSpan.Zero, evidence.CleanWatchDuration);
    }

    private static StreamChannelHealthEvent CreateHealthEvent(
        string eventKind,
        bool clientAbortAfterRecovery = false,
        string? safeStartKind = null,
        double? cleanWatchDurationMs = null,
        TimeSpan? age = null)
        => new()
        {
            StreamChannelHealthEventId = Guid.NewGuid().ToString("N"),
            ProviderId = "provider-1",
            ProviderChannelId = "channel-1",
            DisplayName = "Test Channel",
            EventKind = eventKind,
            EventUtc = DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(5)),
            ClientAbortAfterRecovery = clientAbortAfterRecovery,
            SafeStartKind = safeStartKind,
            CleanWatchDurationMs = cleanWatchDurationMs,
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
