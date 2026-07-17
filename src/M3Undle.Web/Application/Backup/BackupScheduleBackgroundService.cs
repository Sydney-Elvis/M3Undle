namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Polls hourly for a due weekly backup. Deliberately simple compared to SnapshotRefreshService's
/// event-driven wait/cancel loop — a weekly cadence doesn't need instant reactivity to a settings
/// change, so an hourly poll is a reasonable trade against the added complexity of wiring another
/// AppEventBus subscription for this.
/// </summary>
public sealed class BackupScheduleBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackupScheduleBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled backup check failed; will retry on the next poll.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scheduleService = scope.ServiceProvider.GetRequiredService<IBackupScheduleService>();

        var nextDue = await scheduleService.GetNextScheduledBackupUtcAsync(cancellationToken);
        if (nextDue is null || nextDue > DateTime.UtcNow)
            return;

        var backupService = scope.ServiceProvider.GetRequiredService<PortableBackupService>();
        var result = await backupService.CreateAsync(cancellationToken);

        if (result.Success)
        {
            await scheduleService.RecordRunAsync(DateTime.UtcNow, cancellationToken);
            logger.LogInformation("Scheduled portable backup created at {Path}.", result.FilePath);
        }
        else
        {
            logger.LogWarning("Scheduled portable backup failed: {Error}", result.ErrorMessage);
        }
    }
}
