using System.Threading.Channels;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Streaming.Observability;

public sealed class StreamChannelHealthEventRecorder(
    IServiceScopeFactory scopeFactory,
    ILogger<StreamChannelHealthEventRecorder> logger) : BackgroundService, IStreamChannelHealthEventRecorder
{
    private readonly Channel<StreamDiagnosticEvent> _queue = Channel.CreateBounded<StreamDiagnosticEvent>(
        new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private int _pendingCount;

    public void Record(StreamDiagnosticEvent diagnosticEvent)
    {
        if (!ShouldPersist(diagnosticEvent))
            return;

        Interlocked.Increment(ref _pendingCount);
        if (!_queue.Writer.TryWrite(diagnosticEvent))
        {
            Interlocked.Decrement(ref _pendingCount);
            logger.LogWarning(
                "Stream channel health event queue is full; dropping {EventKind} for {ProviderId}/{ProviderChannelId}.",
                diagnosticEvent.Kind,
                diagnosticEvent.ProviderId,
                diagnosticEvent.ProviderChannelId);
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (Volatile.Read(ref _pendingCount) > 0)
            await Task.Delay(10, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var diagnosticEvent in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await PersistAsync(diagnosticEvent, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to persist stream channel health event {EventKind} for {ProviderId}/{ProviderChannelId}.",
                    diagnosticEvent.Kind,
                    diagnosticEvent.ProviderId,
                    diagnosticEvent.ProviderChannelId);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }
    }

    private async Task PersistAsync(StreamDiagnosticEvent diagnosticEvent, CancellationToken ct)
    {
        var healthEvent = Map(diagnosticEvent);
        if (healthEvent is null)
            return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.StreamChannelHealthEvents.Add(healthEvent);
        await db.SaveChangesAsync(ct);
    }

    private static StreamChannelHealthEvent? Map(StreamDiagnosticEvent diagnosticEvent)
    {
        if (string.IsNullOrWhiteSpace(diagnosticEvent.ProviderId)
            || string.IsNullOrWhiteSpace(diagnosticEvent.ProviderChannelId)
            || string.IsNullOrWhiteSpace(diagnosticEvent.DisplayName))
            return null;

        var isRecoveryFailure = diagnosticEvent.Kind is StreamDiagnosticEventKind.RecoveryHoldLimitExceeded
            or StreamDiagnosticEventKind.RecoveryFailedUnsafe;
        var isForcedRetune = diagnosticEvent.Kind == StreamDiagnosticEventKind.RecoveryForcedRetune || isRecoveryFailure;
        var isAbortAfterRecovery = diagnosticEvent.Kind == StreamDiagnosticEventKind.ClientAbortAfterRecovery;
        var isTsSyncLoss = diagnosticEvent.Kind == StreamDiagnosticEventKind.MpegTsSyncLost;

        return new StreamChannelHealthEvent
        {
            StreamChannelHealthEventId = diagnosticEvent.EventId,
            ProviderId = diagnosticEvent.ProviderId,
            ProviderChannelId = diagnosticEvent.ProviderChannelId,
            DisplayName = diagnosticEvent.DisplayName,
            EventKind = diagnosticEvent.Kind.ToString(),
            EventUtc = diagnosticEvent.TimestampUtc.UtcDateTime,
            SessionId = diagnosticEvent.SessionId,
            RelayMode = diagnosticEvent.RelayMode,
            RouteClassification = diagnosticEvent.RouteClassification,
            UpstreamFailureKind = diagnosticEvent.UpstreamFailureKind?.ToString(),
            ReconnectAttempt = diagnosticEvent.ReconnectAttempt,
            RecoveryDurationMs = diagnosticEvent.RecoveryDurationMs,
            SafeStartWaitMs = diagnosticEvent.OutputHeldMs,
            OutputHeldMs = diagnosticEvent.OutputHeldMs,
            SafeStartKind = diagnosticEvent.SafeStartKind,
            ClientDisconnectReason = diagnosticEvent.DisconnectReason?.ToString(),
            ClientAbortAfterRecovery = isAbortAfterRecovery,
            ClientAbortAfterRecoveryDelayMs = diagnosticEvent.ClientAbortAfterRecoveryDelayMs,
            ForcedRetune = isForcedRetune,
            TsSyncLoss = isTsSyncLoss,
            BytesSuppressed = diagnosticEvent.BytesSuppressed,
        };
    }

    private static bool ShouldPersist(StreamDiagnosticEvent diagnosticEvent)
        => diagnosticEvent.Kind is StreamDiagnosticEventKind.UpstreamFailure
            or StreamDiagnosticEventKind.RecoveryOutputResumed
            or StreamDiagnosticEventKind.RecoveryHoldLimitExceeded
            or StreamDiagnosticEventKind.RecoveryFailedUnsafe
            or StreamDiagnosticEventKind.RecoveryForcedRetune
            or StreamDiagnosticEventKind.ClientAbortAfterRecovery
            or StreamDiagnosticEventKind.MpegTsSyncLost;
}
