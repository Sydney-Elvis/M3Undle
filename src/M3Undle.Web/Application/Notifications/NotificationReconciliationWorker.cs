namespace M3Undle.Web.Application.Notifications;

/// <summary>Wakes the reconciler early after a change; the periodic poll is what guarantees convergence.</summary>
public sealed class NotificationSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Wake()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);
}

public sealed class NotificationReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    NotificationSignal signal,
    TimeProvider timeProvider,
    ILogger<NotificationReconciliationWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PassDeadline = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup finish its own work (migrations, restore policy, startup events) before the first pass.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var reconciler = scope.ServiceProvider.GetRequiredService<NotificationReconciler>();
                using var deadline = new CancellationTokenSource(PassDeadline, timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
                await reconciler.RunOnceAsync(linked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification reconciliation pass failed; it will be retried.");
            }

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var woke = signal.WaitAsync(wait.Token);
                var tick = Task.Delay(Interval, timeProvider, wait.Token);
                await Task.WhenAny(woke, tick);
                await wait.CancelAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
