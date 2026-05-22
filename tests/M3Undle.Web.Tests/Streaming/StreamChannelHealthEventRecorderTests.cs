using M3Undle.Web.Data;
using M3Undle.Web.Streaming.Models;
using M3Undle.Web.Streaming.Observability;
using M3Undle.Web.Streaming.Subscribers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class StreamChannelHealthEventRecorderTests
{
    [TestMethod]
    public async Task Record_PersistsRecoveryOutputResumedHealthEvent()
    {
        await using var fixture = await RecorderFixture.CreateAsync();

        await fixture.Recorder.StartAsync(CancellationToken.None);
        fixture.Recorder.Record(CreateEvent(
            StreamDiagnosticEventKind.RecoveryOutputResumed,
            safeStartKind: "H264Idr",
            outputHeldMs: 11.5,
            recoveryDurationMs: 1250,
            bytesSuppressed: 8084));
        await fixture.Recorder.FlushAsync();
        await fixture.Recorder.StopAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var healthEvent = await db.StreamChannelHealthEvents.SingleAsync();
        Assert.AreEqual("RecoveryOutputResumed", healthEvent.EventKind);
        Assert.AreEqual("provider-1", healthEvent.ProviderId);
        Assert.AreEqual("channel-1", healthEvent.ProviderChannelId);
        Assert.AreEqual("FfmpegCleanRemux", healthEvent.RelayMode);
        Assert.AreEqual("H264Idr", healthEvent.SafeStartKind);
        Assert.AreEqual(11.5, healthEvent.OutputHeldMs);
        Assert.AreEqual(1250, healthEvent.RecoveryDurationMs);
        Assert.AreEqual(8084, healthEvent.BytesSuppressed);
        Assert.IsFalse(healthEvent.ForcedRetune);
    }

    [TestMethod]
    public async Task Record_PersistsClientAbortAfterRecoveryCorrelation()
    {
        await using var fixture = await RecorderFixture.CreateAsync();

        await fixture.Recorder.StartAsync(CancellationToken.None);
        fixture.Recorder.Record(CreateEvent(
            StreamDiagnosticEventKind.ClientAbortAfterRecovery,
            disconnectReason: SubscriberDisconnectReason.ClientAborted,
            clientAbortAfterRecoveryDelayMs: 244_566));
        await fixture.Recorder.FlushAsync();
        await fixture.Recorder.StopAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var healthEvent = await db.StreamChannelHealthEvents.SingleAsync();
        Assert.AreEqual("ClientAbortAfterRecovery", healthEvent.EventKind);
        Assert.IsTrue(healthEvent.ClientAbortAfterRecovery);
        Assert.AreEqual("ClientAborted", healthEvent.ClientDisconnectReason);
        Assert.AreEqual(244_566, healthEvent.ClientAbortAfterRecoveryDelayMs);
    }

    [TestMethod]
    public async Task Record_PersistsCleanWatchDuration()
    {
        await using var fixture = await RecorderFixture.CreateAsync();

        await fixture.Recorder.StartAsync(CancellationToken.None);
        fixture.Recorder.Record(CreateEvent(
            StreamDiagnosticEventKind.CleanWatchCompleted,
            cleanWatchDurationMs: 45_000));
        await fixture.Recorder.FlushAsync();
        await fixture.Recorder.StopAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var healthEvent = await db.StreamChannelHealthEvents.SingleAsync();
        Assert.AreEqual("CleanWatchCompleted", healthEvent.EventKind);
        Assert.AreEqual(45_000, healthEvent.CleanWatchDurationMs);
    }

    [TestMethod]
    public async Task Record_IgnoresNonHealthDiagnosticEvent()
    {
        await using var fixture = await RecorderFixture.CreateAsync();

        await fixture.Recorder.StartAsync(CancellationToken.None);
        fixture.Recorder.Record(CreateEvent(StreamDiagnosticEventKind.SubscriberAttached));
        await fixture.Recorder.FlushAsync();
        await fixture.Recorder.StopAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        Assert.AreEqual(0, await db.StreamChannelHealthEvents.CountAsync());
    }

    private static StreamDiagnosticEvent CreateEvent(
        StreamDiagnosticEventKind kind,
        string? safeStartKind = null,
        double? outputHeldMs = null,
        double? recoveryDurationMs = null,
        long? bytesSuppressed = null,
        SubscriberDisconnectReason? disconnectReason = null,
        double? clientAbortAfterRecoveryDelayMs = null,
        double? cleanWatchDurationMs = null)
        => new(
            EventId: Guid.NewGuid().ToString("N"),
            TimestampUtc: DateTimeOffset.UtcNow,
            Kind: kind,
            SessionId: "session-1",
            ProviderId: "provider-1",
            ProviderChannelId: "channel-1",
            DisplayName: "ABC",
            RequestedRoute: "/live/key/1.ts",
            RouteClassification: "shared_hls",
            UpstreamFailureKind: kind == StreamDiagnosticEventKind.UpstreamFailure ? UpstreamFailureKind.TimeoutOrStall : null,
            ReconnectAttempt: 1,
            OutputHeldMs: outputHeldMs,
            RecoveryDurationMs: recoveryDurationMs,
            SafeStartKind: safeStartKind,
            BytesSuppressed: bytesSuppressed,
            ClientAbortAfterRecoveryDelayMs: clientAbortAfterRecoveryDelayMs,
            CleanWatchDurationMs: cleanWatchDurationMs,
            DisconnectReason: disconnectReason,
            RelayMode: "FfmpegCleanRemux");

    private sealed class RecorderFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;
        private readonly DbContextOptions<ApplicationDbContext> _options;

        private RecorderFixture(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            DbContextOptions<ApplicationDbContext> options,
            StreamChannelHealthEventRecorder recorder)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
            _options = options;
            Recorder = recorder;
        }

        public StreamChannelHealthEventRecorder Recorder { get; }

        public static async Task<RecorderFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(_ => _.UseSqlite(connection));
            var serviceProvider = services.BuildServiceProvider();

            await using (var db = new ApplicationDbContext(options))
                await db.Database.EnsureCreatedAsync();

            var recorder = new StreamChannelHealthEventRecorder(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<StreamChannelHealthEventRecorder>.Instance);

            return new RecorderFixture(connection, serviceProvider, options, recorder);
        }

        public ApplicationDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await Recorder.StopAsync(CancellationToken.None);
            Recorder.Dispose();
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
