using M3Undle.Core.M3u;
using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace M3Undle.Web.Application;

/// <summary>
/// Singleton background service that runs scheduled and on-demand snapshot refreshes.
/// Also implements <see cref="IRefreshTrigger"/> for manual triggering from API endpoints.
/// The refresh schedule is read dynamically from DB — changes take effect without restart.
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

    // Channels from the last full refresh, keyed by providerId — reused by build-only so VOD/series are included without re-fetching
    private IReadOnlyDictionary<string, IReadOnlyList<ParsedProviderChannel>> _cachedChannels =
        new Dictionary<string, IReadOnlyList<ParsedProviderChannel>>();

    // Current run CTS — cancelled by CancelRefresh(); null when no run is active
    private volatile CancellationTokenSource? _currentRunCts;
    private volatile bool _cancelledByUser;

    // Schedule loop wait CTS — cancelled when the user updates the refresh schedule
    private volatile CancellationTokenSource? _scheduleWaitCts;

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

        // Startup delay before evaluating initial state
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(refreshOptions.Value.StartupDelaySeconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Startup recovery: conditionally trigger based on last snapshot staleness
        await HandleStartupRecoveryAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("SnapshotRefreshService stopped.");
            return;
        }

        // Start the dynamic schedule loop in background
        _ = ScheduleLoopAsync(stoppingToken);

        // Process triggers
        try
        {
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
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        logger.LogInformation("SnapshotRefreshService stopped.");
    }

    // -------------------------------------------------------------------------
    // Schedule loop
    // -------------------------------------------------------------------------

    private async Task ScheduleLoopAsync(CancellationToken stoppingToken)
    {
        var events = eventBus.Subscribe(out var unsubscriber);
        using (unsubscriber)
        {
            _ = MonitorScheduleChangesAsync(events, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _scheduleWaitCts = waitCts;

                try
                {
                    RefreshScheduleSettings settings;
                    DateTime? lastSnapshotUtc;

                    await using (var scope = scopeFactory.CreateAsyncScope())
                    {
                        var scheduleService = scope.ServiceProvider.GetRequiredService<IRefreshScheduleService>();
                        settings = await scheduleService.GetSettingsAsync(stoppingToken);

                        if (!settings.IsManual)
                        {
                            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            lastSnapshotUtc = await db.Snapshots
                                .AsNoTracking()
                                .Where(s => s.Status == "active")
                                .OrderByDescending(s => s.CreatedUtc)
                                .Select(s => (DateTime?)s.CreatedUtc)
                                .FirstOrDefaultAsync(stoppingToken);
                        }
                        else
                        {
                            lastSnapshotUtc = null;
                        }
                    }

                    if (settings.IsManual)
                    {
                        // Park until the schedule is changed or the service stops
                        logger.LogDebug("Refresh schedule: manual — waiting for explicit trigger or schedule change.");
                        await WaitIndefinitelyAsync(waitCts.Token);
                        continue;
                    }

                    var intervalHours = settings.IntervalHours!.Value;
                    var baseline = lastSnapshotUtc ?? DateTime.UtcNow;
                    var nextTrigger = baseline.AddHours(intervalHours);
                    var delay = nextTrigger - DateTime.UtcNow;

                    if (delay > TimeSpan.Zero)
                    {
                        logger.LogDebug("Next scheduled refresh at {NextTrigger:u} (in {DelayMinutes:F0} min).",
                            nextTrigger, delay.TotalMinutes);
                        var reached = await WaitAsync(delay, waitCts.Token);
                        if (!reached) continue; // cancelled — schedule changed or stopping
                    }
                    else
                    {
                        logger.LogDebug("Scheduled refresh interval elapsed — triggering now.");
                    }

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        if (_executionGate.CurrentCount > 0)
                            _triggerChannel.Writer.TryWrite(RefreshMode.FetchAndBuild);
                        else
                            logger.LogDebug("Scheduled refresh trigger skipped — a refresh is already in progress.");
                    }

                    // Wait the full interval before re-evaluating so we don't spin tight
                    // when the last snapshot is stale. A schedule-change event will cancel this.
                    await WaitAsync(TimeSpan.FromHours(intervalHours), waitCts.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                finally
                {
                    _scheduleWaitCts = null;
                }
            }
        }
    }

    private async Task MonitorScheduleChangesAsync(ChannelReader<AppEvent> events, CancellationToken stoppingToken)
    {
        await foreach (var evt in events.ReadAllAsync(stoppingToken))
        {
            if (evt.Kind == AppEventKind.RefreshScheduleChanged)
            {
                logger.LogInformation("Refresh schedule changed — waking schedule loop.");
                _scheduleWaitCts?.Cancel();
            }
        }
    }

    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task WaitIndefinitelyAsync(CancellationToken ct)
    {
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
    }

    // -------------------------------------------------------------------------
    // Startup recovery
    // -------------------------------------------------------------------------

    private async Task HandleStartupRecoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scheduleService = scope.ServiceProvider.GetRequiredService<IRefreshScheduleService>();
            var settings = await scheduleService.GetSettingsAsync(stoppingToken);

            if (!settings.StartupCatchup)
            {
                logger.LogInformation("Startup recovery: disabled — skipping startup refresh.");
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var lastSnapshotUtc = await db.Snapshots
                .AsNoTracking()
                .Where(s => s.Status == "active")
                .OrderByDescending(s => s.CreatedUtc)
                .Select(s => (DateTime?)s.CreatedUtc)
                .FirstOrDefaultAsync(stoppingToken);

            // For manual schedule, treat 24h as the staleness threshold for startup recovery
            var thresholdHours = settings.IntervalHours ?? 24;
            var isStale = lastSnapshotUtc is null
                || (DateTime.UtcNow - lastSnapshotUtc.Value).TotalHours >= thresholdHours;

            if (isStale)
            {
                logger.LogInformation(
                    "Startup recovery: last snapshot {LastSnapshot} is stale (threshold: {Threshold}h) — triggering refresh.",
                    lastSnapshotUtc?.ToString("u") ?? "none", thresholdHours);
                _triggerChannel.Writer.TryWrite(RefreshMode.FetchAndBuild);
            }
            else
            {
                logger.LogInformation(
                    "Startup recovery: last snapshot {LastSnapshot} is current — no startup refresh needed.",
                    lastSnapshotUtc!.Value.ToString("u"));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown during startup recovery check
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup recovery check failed — proceeding without startup refresh.");
        }
    }

    // -------------------------------------------------------------------------
    // Refresh execution
    // -------------------------------------------------------------------------

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
        string? changeClass = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var builder = scope.ServiceProvider.GetRequiredService<SnapshotBuilder>();
            var (s, e, channelsByProvider, cc) = await builder.RunAsync(runCts.Token);
            (succeeded, errorSummary, changeClass) = (s, e, cc);
            if (channelsByProvider.Count > 0)
                _cachedChannels = channelsByProvider;
            logger.LogInformation("Snapshot refresh completed (published={Succeeded}, change={ChangeClass}).", succeeded, changeClass ?? "none");
        }
        catch (OperationCanceledException) when (_cancelledByUser && !stoppingToken.IsCancellationRequested)
        {
            errorSummary = "Cancelled by user.";
            logger.LogInformation("Snapshot refresh cancelled by user.");
        }
        finally
        {
            _currentRunCts = null;
            eventBus.Publish(AppEventKind.RefreshCompleted, succeeded, errorSummary, changeClass);
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
        string? changeClass = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var builder = scope.ServiceProvider.GetRequiredService<SnapshotBuilder>();
            (succeeded, errorSummary, changeClass) = await builder.BuildOnlyAsync(_cachedChannels, runCts.Token);
            logger.LogInformation("Snapshot build-only completed (published={Succeeded}, change={ChangeClass}).", succeeded, changeClass ?? "none");
        }
        catch (OperationCanceledException) when (_cancelledByUser && !stoppingToken.IsCancellationRequested)
        {
            errorSummary = "Cancelled by user.";
            logger.LogInformation("Snapshot build-only cancelled by user.");
        }
        finally
        {
            _currentRunCts = null;
            eventBus.Publish(AppEventKind.RefreshCompleted, succeeded, errorSummary, changeClass);
        }
    }
}
