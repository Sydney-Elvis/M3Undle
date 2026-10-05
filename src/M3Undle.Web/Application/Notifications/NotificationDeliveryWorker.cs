namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// One independent loop per registered provider kind, so a slow or failing transport only delays its own queue. Each pass
/// opens a fresh scope, and a transport outage never reaches refresh, publication or streaming.
/// </summary>
public sealed class NotificationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    NotificationProviderRegistry registry,
    TimeProvider timeProvider,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int MaxPerPass = 50;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(registry.Kinds.Select(kind => RunKindAsync(kind, stoppingToken)));

    private async Task RunKindAsync(string kind, CancellationToken stoppingToken)
    {
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{kind}:{Guid.NewGuid():N}";
        try
        {
            await Task.Delay(PollInterval, timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>();
                    await processor.RecoverExpiredClaimsAsync(stoppingToken);
                    for (var i = 0; i < MaxPerPass && await processor.ProcessNextAsync(kind, owner, stoppingToken); i++)
                    {
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Notification delivery pass for {Kind} failed; it will be retried.", kind);
                }

                await Task.Delay(PollInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
