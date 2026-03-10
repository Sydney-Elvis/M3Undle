using M3Undle.Core.M3u;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace M3Undle.Web.Application;

/// <summary>
/// Singleton background service that runs scheduled and on-demand snapshot refreshes.
/// Also implements <see cref="IRefreshTrigger"/> for manual triggering from API endpoints.
/// </summary>
public sealed class SnapshotRefreshService(
    IServiceScopeFactory scopeFactory,
    IOptions<RefreshOptions> refreshOptions,
    AppEventBus eventBus,
    ILogger<SnapshotRefreshService> logger)
    : BackgroundService, IRefreshTrigger
{
    private enum RefreshMode { FetchAndBuild, BuildOnly }

    // Semaphore guards the running refresh — at-most-one execution at a time
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    // Bounded channel collapses multiple triggers to at-most-one queued run
    private readonly Channel<RefreshMode> _triggerChannel = Channel.CreateBounded<RefreshMode>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    // Channels from the last full refresh — reused by build-only so VOD/series are included without re-fetching
    private IReadOnlyList<ParsedProviderChannel> _cachedChannels = [];

    // Current run CTS — cancelled by CancelRefresh(); null when no run is active
    private volatile CancellationTokenSource? _currentRunCts;
    private volatile bool _cancelledByUser;

    // -------------------------------------------------------------------------
    // IRefreshTrigger
    // -------------------------------------------------------------------------

    public bool IsRefreshing => _executionGate.CurrentCount == 0;

    public bool TriggerRefresh()
    {
        if (_executionGate.CurrentCount == 0)
            return false; // Already running — caller returns 409

        _triggerChannel.Writer.TryWrite(RefreshMode.FetchAndBuild);
        return true;
    }

    public bool TriggerBuildOnly()
    {
        if (_executionGate.CurrentCount == 0)
            return false; // Already running — caller returns 409

        _triggerChannel.Writer.TryWrite(RefreshMode.BuildOnly);
        return true;
    }

    public void CancelRefresh()
    {
        _cancelledByUser = true;
        _currentRunCts?.Cancel();
    }

    // -------------------------------------------------------------------------
    // BackgroundService
    // -------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var systemScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "System" });
        logger.LogInformation("SnapshotRefreshService started.");

        // Startup delay before the first scheduled run
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(refreshOptions.Value.StartupDelaySeconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Queue the first run immediately after startup delay
        _triggerChannel.Writer.TryWrite(RefreshMode.FetchAndBuild);

        // Start the schedule loop in background
        _ = ScheduleLoopAsync(stoppingToken);

        // Process triggers
        await foreach (var mode in _triggerChannel.Reader.ReadAllAsync(stoppingToken))
        {
            // Non-blocking acquire: if something is already running, drop the trigger
            if (!await _executionGate.WaitAsync(0, stoppingToken))
            {
                logger.LogDebug("Scheduled refresh skipped — a refresh is already in progress.");
                continue;
            }

            try
            {
                using var refreshScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });
                if (mode == RefreshMode.BuildOnly)
                    await RunBuildOnlyAsync(stoppingToken);
                else
                    await RunRefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                using var refreshScope = logger.BeginScope(new Dictionary<string, object> { ["EventType"] = "Refresh" });
                logger.LogError(ex, "Snapshot refresh failed unexpectedly.");
            }
            finally
            {
                _executionGate.Release();
            }
        }

        logger.LogInformation("SnapshotRefreshService stopped.");
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task ScheduleLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(refreshOptions.Value.IntervalHours), stoppingToken);

                if (_executionGate.CurrentCount > 0)
                {
                    _triggerChannel.Writer.TryWrite(RefreshMode.FetchAndBuild);
                }
                else
                {
                    logger.LogDebug("Scheduled refresh trigger skipped — a refresh is already in progress.");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunRefreshAsync(CancellationToken stoppingToken)
    {
        _cancelledByUser = false;
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _currentRunCts = runCts;
        runCts.CancelAfter(TimeSpan.FromMinutes(refreshOptions.Value.TimeoutMinutes));

        logger.LogInformation("Snapshot refresh started.");
        eventBus.Publish(AppEventKind.RefreshStarted);
        bool succeeded = false;
        string? errorSummary = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var builder = scope.ServiceProvider.GetRequiredService<SnapshotBuilder>();
            var (s, e, channels) = await builder.RunAsync(runCts.Token);
            (succeeded, errorSummary) = (s, e);
            if (channels.Count > 0)
                _cachedChannels = channels;
            logger.LogInformation("Snapshot refresh completed (published={Succeeded}).", succeeded);
        }
        catch (OperationCanceledException) when (_cancelledByUser && !stoppingToken.IsCancellationRequested)
        {
            errorSummary = "Cancelled by user.";
            logger.LogInformation("Snapshot refresh cancelled by user.");
        }
        finally
        {
            _currentRunCts = null;
            eventBus.Publish(AppEventKind.RefreshCompleted, succeeded, errorSummary);
        }
    }

    private async Task RunBuildOnlyAsync(CancellationToken stoppingToken)
    {
        _cancelledByUser = false;
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _currentRunCts = runCts;
        runCts.CancelAfter(TimeSpan.FromMinutes(refreshOptions.Value.TimeoutMinutes));

        logger.LogInformation("Snapshot build-only started.");
        eventBus.Publish(AppEventKind.RefreshStarted);
        bool succeeded = false;
        string? errorSummary = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var builder = scope.ServiceProvider.GetRequiredService<SnapshotBuilder>();
            (succeeded, errorSummary) = await builder.BuildOnlyAsync(_cachedChannels, runCts.Token);
            logger.LogInformation("Snapshot build-only completed (published={Succeeded}).", succeeded);
        }
        catch (OperationCanceledException) when (_cancelledByUser && !stoppingToken.IsCancellationRequested)
        {
            errorSummary = "Cancelled by user.";
            logger.LogInformation("Snapshot build-only cancelled by user.");
        }
        finally
        {
            _currentRunCts = null;
            eventBus.Publish(AppEventKind.RefreshCompleted, succeeded, errorSummary);
        }
    }
}
