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
    public async Task GetRelayPolicyDecision_AutoCautious_SelectsCleanRemux()
    {
        // Issue #96: escalate at first real instability. Cautious channels now select
        // clean remux under Auto (previously only Unstable did), so a misbehaving
        // channel is protected without first failing the user.
        await using var fixture = await ProfileFixture.CreateAsync();
        var policy = new StreamChannelRecoveryPolicy(
            StreamChannelHealthProfile.Cautious,
            TimeSpan.FromSeconds(3),
            512 * 1024,
            AllowPacketBoundaryRecoveryFallback: true,
            RequireDownstreamRetune: false,
            DownstreamRetuneReason: null,
            Reason: "cautious test profile");

        var decision = fixture.Service.GetRelayPolicyDecision("auto", policy);

        Assert.AreEqual(UpstreamRelayModes.FfmpegCleanRemux, decision.SelectedRelayMode);
        StringAssert.Contains(decision.Reason, "Cautious");
    }

    [TestMethod]
    public async Task GetRelayPolicyDecision_AutoStable_SelectsDirect()
    {
        // Stable channels stay direct and near-live; only non-Stable profiles remux.
        await using var fixture = await ProfileFixture.CreateAsync();
        var policy = new StreamChannelRecoveryPolicy(
            StreamChannelHealthProfile.Stable,
            TimeSpan.FromSeconds(3),
            512 * 1024,
            AllowPacketBoundaryRecoveryFallback: true,
            RequireDownstreamRetune: false,
            DownstreamRetuneReason: null,
            Reason: "stable test profile");

        var decision = fixture.Service.GetRelayPolicyDecision("auto", policy);

        Assert.AreEqual(UpstreamRelayModes.Direct, decision.SelectedRelayMode);
    }

    [TestMethod]
    public async Task GetRecoveryPolicyAsync_TwoAbortsAfterRecovery_DoesNotRequireDownstreamRetune()
    {
        // Issue #96: the forced downstream retune is now a last resort requiring >= 3
        // genuine post-recovery aborts. Two aborts classify Unstable (clean remux) but
        // must not force a downstream close.
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("RecoveryOutputResumed", safeStartKind: "H264Idr"),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true));

        var policy = await fixture.Service.GetRecoveryPolicyAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthProfile.Unstable, policy.Profile);
        Assert.IsFalse(policy.RequireDownstreamRetune);
    }

    [TestMethod]
    public async Task GetRecoveryPolicyAsync_ThreeAbortsAfterIdrRecovery_RequiresDownstreamRetune()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("RecoveryOutputResumed", safeStartKind: "H264Idr"),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true),
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true));

        var policy = await fixture.Service.GetRecoveryPolicyAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthProfile.Unstable, policy.Profile);
        Assert.IsTrue(policy.RequireDownstreamRetune);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_WithCleanWatchAfterAdverseEvent_ExposesLastCleanWatchUtc()
    {
        var cleanWatchTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true, age: TimeSpan.FromHours(2)),
            CreateHealthEvent("CleanWatchCompleted", cleanWatchDurationMs: TimeSpan.FromMinutes(35).TotalMilliseconds, age: TimeSpan.FromMinutes(10)));

        var evidence = await fixture.Service.GetEvidenceAsync(
            "provider-1",
            "channel-1",
            new ReconnectOptions());

        Assert.IsNotNull(evidence.LastCleanWatchUtc);
        Assert.IsNotNull(evidence.LastAdverseEventUtc);
        Assert.IsTrue(evidence.LastCleanWatchUtc > evidence.LastAdverseEventUtc,
            "LastCleanWatchUtc should be more recent than LastAdverseEventUtc");
        Assert.AreEqual(1, evidence.CleanWatchEvents);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_NoCleanWatchAfterAdverseEvent_LastCleanWatchUtcIsNull()
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

        Assert.IsNull(evidence.LastCleanWatchUtc,
            "Clean watch before the adverse event should not count — LastCleanWatchUtc must be null");
        Assert.AreEqual(0, evidence.CleanWatchEvents);
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

    [TestMethod]
    public async Task GetEvidenceAsync_NoEvents_TrendIsUnknown()
    {
        await using var fixture = await ProfileFixture.CreateAsync();

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Unknown, evidence.Trend.Trend);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_OnlyCleanWatchInRecentWindow_TrendIsStable()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("CleanWatchCompleted", cleanWatchDurationMs: TimeSpan.FromMinutes(20).TotalMilliseconds, age: TimeSpan.FromMinutes(30)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Stable, evidence.Trend.Trend);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_ForcedRetuneInRecentWindow_TrendIsWorsening()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("ForcedRetune", forcedRetune: true, age: TimeSpan.FromMinutes(20)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Worsening, evidence.Trend.Trend);
        StringAssert.Contains(evidence.Trend.Reason, "forced retune");
    }

    [TestMethod]
    public async Task GetEvidenceAsync_ClientAbortInRecentWindow_TrendIsWorsening()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("ClientAbortAfterRecovery", clientAbortAfterRecovery: true, age: TimeSpan.FromMinutes(10)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Worsening, evidence.Trend.Trend);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_AdverseOnlyInComparisonWindow_TrendIsImproving()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(90)),
            CreateHealthEvent("CleanWatchCompleted", cleanWatchDurationMs: TimeSpan.FromMinutes(35).TotalMilliseconds, age: TimeSpan.FromMinutes(20)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Improving, evidence.Trend.Trend);
        Assert.AreEqual(0, evidence.Trend.RecentAdverseCount);
        Assert.AreEqual(1, evidence.Trend.ComparisonAdverseCount);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_MoreAdverseInRecentThanComparison_TrendIsWorsening()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(90)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(30)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(25)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(20)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Worsening, evidence.Trend.Trend);
        Assert.AreEqual(3, evidence.Trend.RecentAdverseCount);
        Assert.AreEqual(1, evidence.Trend.ComparisonAdverseCount);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_FewerAdverseInRecentWithCleanWatch_TrendIsImproving()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(90)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(85)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(80)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(30)),
            CreateHealthEvent("CleanWatchCompleted", cleanWatchDurationMs: TimeSpan.FromMinutes(20).TotalMilliseconds, age: TimeSpan.FromMinutes(10)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Improving, evidence.Trend.Trend);
        Assert.AreEqual(1, evidence.Trend.RecentAdverseCount);
        Assert.AreEqual(3, evidence.Trend.ComparisonAdverseCount);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_EqualAdverseInBothWindows_TrendIsStable()
    {
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(90)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(85)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(30)),
            CreateHealthEvent("UpstreamFailure", age: TimeSpan.FromMinutes(25)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Stable, evidence.Trend.Trend);
        Assert.AreEqual(2, evidence.Trend.RecentAdverseCount);
        Assert.AreEqual(2, evidence.Trend.ComparisonAdverseCount);
    }

    [TestMethod]
    public async Task GetEvidenceAsync_CleanWatchBeforeForcedRetune_TrendIsWorsening()
    {
        // Clean watch before the adverse event should not flip the trend to Improving
        await using var fixture = await ProfileFixture.CreateAsync();
        await fixture.SeedAsync(
            CreateHealthEvent("CleanWatchCompleted", cleanWatchDurationMs: TimeSpan.FromMinutes(20).TotalMilliseconds, age: TimeSpan.FromMinutes(40)),
            CreateHealthEvent("ForcedRetune", forcedRetune: true, age: TimeSpan.FromMinutes(15)));

        var evidence = await fixture.Service.GetEvidenceAsync("provider-1", "channel-1", new ReconnectOptions());

        Assert.AreEqual(StreamChannelHealthTrend.Worsening, evidence.Trend.Trend);
        Assert.AreEqual(TimeSpan.Zero, evidence.Trend.CleanWatchSinceLastAdverse);
    }

    private static StreamChannelHealthEvent CreateHealthEvent(
        string eventKind,
        bool clientAbortAfterRecovery = false,
        bool forcedRetune = false,
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
            ForcedRetune = forcedRetune,
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
