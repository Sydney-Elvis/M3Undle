using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Downstream;

/// <summary>
/// Background service that watches for successful RefreshCompleted events and notifies
/// all enabled downstream integrations. Notification failures are recorded on the row
/// but do not block the publish pipeline.
///
/// Slice 2 will add change classification so we can selectively fire lineup-only or
/// guide-only triggers. For now we fire based on each integration's TriggerOn* settings
/// whenever a full refresh succeeds.
/// </summary>
public sealed class DownstreamNotificationService(
    AppEventBus eventBus,
    IServiceScopeFactory scopeFactory,
    IEnumerable<IDownstreamAdapter> adapters,
    SecretEncryptionService encryption,
    IEventService eventService,
    ILogger<DownstreamNotificationService> logger) : BackgroundService
{
    private readonly Dictionary<string, IDownstreamAdapter> _adapters =
        adapters.ToDictionary(a => a.Kind, StringComparer.OrdinalIgnoreCase);

    // SQLite only allows one writer at a time. Notifications run concurrently, but
    // the result write-back must be serialized to avoid SQLITE_BUSY errors.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = eventBus.Subscribe(out var unsubscriber);
        await using (unsubscriber as IAsyncDisposable ?? new AsyncDisposableWrapper(unsubscriber))
        {
            await foreach (var evt in reader.ReadAllAsync(stoppingToken))
            {
                if (evt.Kind == AppEventKind.RefreshCompleted && evt.Succeeded)
                    await NotifyAllAsync(evt.ChangeClass, evt.AffectedProfileIds, stoppingToken);
            }
        }
    }

    private async Task NotifyAllAsync(string? changeClass, IReadOnlySet<string>? affectedProfileIds, CancellationToken ct)
    {
        // No meaningful change — skip all downstream notifications
        if (changeClass == ChangeClasses.None)
        {
            logger.LogDebug("Downstream notifications skipped — no meaningful change detected.");
            return;
        }

        // Determine which trigger type this change maps to
        var trigger = changeClass == ChangeClasses.GuideOnly
            ? DownstreamTrigger.GuideUpdate
            : DownstreamTrigger.LineupUpdate;

        List<DownstreamIntegration> integrations;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            integrations = await db.DownstreamIntegrations
                .AsNoTracking()
                .Where(x => x.Enabled &&
                    (changeClass == null
                        ? (x.TriggerOnLineupUpdate || x.TriggerOnGuideUpdate)
                        : (trigger == DownstreamTrigger.LineupUpdate ? x.TriggerOnLineupUpdate : x.TriggerOnGuideUpdate)))
                .ToListAsync(ct);

            // Filter to only integrations scoped to an affected profile (null ProfileId = global = always fires)
            if (affectedProfileIds is { Count: > 0 })
                integrations = integrations
                    .Where(x => x.ProfileId is null || affectedProfileIds.Contains(x.ProfileId))
                    .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load downstream integrations for notification.");
            return;
        }

        if (integrations.Count == 0)
            return;

        logger.LogInformation("Downstream: change={ChangeClass}, trigger={Trigger}, notifying {Count} integration(s).",
            changeClass ?? "first_run", trigger, integrations.Count);

        var tasks = integrations.Select(i => NotifyOneAsync(i, trigger, ct));
        await Task.WhenAll(tasks);
    }

    private async Task NotifyOneAsync(DownstreamIntegration integration, DownstreamTrigger trigger, CancellationToken ct)
    {
        if (!_adapters.TryGetValue(integration.Kind, out var adapter))
        {
            logger.LogWarning("No adapter found for downstream integration kind '{Kind}' (id={Id}).",
                integration.Kind, integration.DownstreamIntegrationId);
            return;
        }

        string? apiKey = null;
        if (integration.ApiKeyEncrypted is not null)
        {
            try
            {
                apiKey = encryption.Decrypt(integration.ApiKeyEncrypted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to decrypt API key for integration {Id}.", integration.DownstreamIntegrationId);
                var decryptError = "Failed to decrypt API key: " + ex.Message;
                try
                {
                    await eventService.PublishAsync(
                        SystemEventSeverity.Warning,
                        SystemEventTypes.DownstreamNotificationFailed,
                        $"Downstream notification failed: {integration.Name}",
                        decryptError,
                        integrationId: integration.DownstreamIntegrationId);
                }
                catch (Exception publishEx) when (publishEx is not OperationCanceledException)
                {
                    logger.LogWarning(publishEx, "Failed to publish DownstreamNotificationFailed event.");
                }

                await WriteResultAsync(integration.DownstreamIntegrationId, null, decryptError, ct);
                return;
            }
        }

        var error = await adapter.NotifyAsync(
            integration.BaseUrl, apiKey, integration.WebhookHeadersJson, trigger, ct);

        if (error is not null)
        {
            logger.LogWarning("Downstream notification failed for {Name} ({Id}): {Error}",
                integration.Name, integration.DownstreamIntegrationId, error);
            try
            {
                await eventService.PublishAsync(SystemEventSeverity.Warning, SystemEventTypes.DownstreamNotificationFailed,
                    $"Downstream notification failed: {integration.Name}", error,
                    integrationId: integration.DownstreamIntegrationId);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to publish DownstreamNotificationFailed event."); }
        }
        else
        {
            logger.LogInformation("Downstream notification sent to {Name} ({Id}).",
                integration.Name, integration.DownstreamIntegrationId);
        }

        await WriteResultAsync(integration.DownstreamIntegrationId, error is null ? DateTime.UtcNow : null, error, ct);
    }

    private async Task WriteResultAsync(string id, DateTime? notifiedUtc, string? error, CancellationToken ct)
    {
        await _writeLock.WaitAsync(CancellationToken.None);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var row = await db.DownstreamIntegrations
                .FirstOrDefaultAsync(x => x.DownstreamIntegrationId == id, CancellationToken.None);
            if (row is null) return;

            if (notifiedUtc.HasValue)
                row.LastNotifiedUtc = notifiedUtc;
            row.LastNotifyError = error;
            row.UpdatedUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write notification result for integration {Id}.", id);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private sealed class AsyncDisposableWrapper(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
