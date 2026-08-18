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
    IEventService eventService,
    TimeProvider timeProvider,
    RefreshActivityTracker activityTracker,
    HeavyWorkGate heavyWorkGate,
    ILogger<SnapshotRefreshService> logger)
    : BackgroundService, IRefreshTrigger
{
    private enum RefreshMode { FetchAndBuild, BuildOnly }

    // Semaphore guards the running refresh — at-most-one execution at a time
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    // Bounded channel collapses multiple triggers to at-most-one queued run
    private readonly Channel<RefreshMode> _triggerChannel = Channel.CreateBounded<RefreshMode>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    // Current run CTS — cancelled by CancelRefresh(); null when no run is active
    private volatile CancellationTokenSource? _currentRunCts;
    private volatile bool _cancelledByUser;
    private DateTime? _refreshStartedAt;

    // Schedule loop wait CTS — cancelled when the user updates the refresh schedule
    private volatile CancellationTokenSource? _scheduleWaitCts;

    // -------------------------------------------------------------------------
    // IRefreshTrigger
    // -------------------------------------------------------------------------

    public bool IsRefreshing => _executionGate.CurrentCount == 0;

    public DateTime? RefreshStartedAt => _refreshStartedAt;

    public string? CurrentActivity => activityTracker.CurrentActivity;

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
                    EffectiveRefreshScheduleSettings? effectiveSettings;
                    DateTime? lastSnapshotUtc;

                    await using (var scope = scopeFactory.CreateAsyncScope())
                    {
                        var scheduleService = scope.ServiceProvider.GetRequiredService<IRefreshScheduleService>();
                        effectiveSettings = await scheduleService.GetActiveProfileSettingsAsync(stoppingToken);

                        if (effectiveSettings is not null && !effectiveSettings.Settings.IsManual)
                        {
                            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            lastSnapshotUtc = await db.Snapshots
                                .AsNoTracking()
                                .Where(s => s.ProfileId == effectiveSettings.ProfileId && s.Status == "active")
                                .OrderByDescending(s => s.CreatedUtc)
                                .Select(s => (DateTime?)s.CreatedUtc)
                                .FirstOrDefaultAsync(stoppingToken);
                        }
                        else
                        {
                            lastSnapshotUtc = null;
                        }
                    }

                    if (effectiveSettings is null)
                    {
                        logger.LogDebug("Refresh schedule: no active profile — waiting for profile activation or schedule change.");
                        await WaitIndefinitelyAsync(waitCts.Token);
                        continue;
                    }

                    var settings = effectiveSettings.Settings;
                    if (settings.IsManual)
                    {
                        // Park until the schedule is changed or the service stops
                        logger.LogDebug("Refresh schedule: manual for active profile {ProfileId} — waiting for explicit trigger or schedule change.",
                            effectiveSettings.ProfileId);
                        await WaitIndefinitelyAsync(waitCts.Token);
                        continue;
                    }

                    var intervalHours = settings.IntervalHours!.Value;
                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    var baseline = lastSnapshotUtc ?? now;
                    var nextTrigger = baseline.AddHours(intervalHours);
                    var delay = nextTrigger - now;

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
            else if (evt.Kind is AppEventKind.ProviderActivated or AppEventKind.ProviderChanged)
            {
                logger.LogInformation("Profile state changed — waking schedule loop.");
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
            var effectiveSettings = await scheduleService.GetActiveProfileSettingsAsync(stoppingToken);
            if (effectiveSettings is null)
            {
                logger.LogInformation("Startup recovery: no active profile — skipping startup refresh.");
                return;
            }

            var settings = effectiveSettings.Settings;

            if (!settings.StartupCatchup)
            {
                logger.LogInformation("Startup recovery: disabled — skipping startup refresh.");
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var lastSnapshotUtc = await db.Snapshots
                .AsNoTracking()
                .Where(s => s.ProfileId == effectiveSettings.ProfileId && s.Status == "active")
                .OrderByDescending(s => s.CreatedUtc)
                .Select(s => (DateTime?)s.CreatedUtc)
                .FirstOrDefaultAsync(stoppingToken);

            // For manual schedule, treat 24h as the staleness threshold for startup recovery
            var thresholdHours = settings.IntervalHours ?? 24;
            var isStale = IsStartupCatchupNeeded(lastSnapshotUtc, thresholdHours, timeProvider.GetUtcNow());

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
    // Startup catch-up predicate (internal for unit testing)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when a startup catch-up refresh should be triggered.
    /// A catch-up is needed when there is no prior snapshot, or when the most-recent
    /// snapshot is at least <paramref name="thresholdHours"/> old.
    /// </summary>
    internal static bool IsStartupCatchupNeeded(
        DateTime? lastSnapshotUtc,
        int thresholdHours,
        DateTimeOffset utcNow)
        => lastSnapshotUtc is null
           || (utcNow.UtcDateTime - lastSnapshotUtc.Value).TotalHours >= thresholdHours;

    // -------------------------------------------------------------------------
    // Refresh execution
    // -------------------------------------------------------------------------

    private async Task RunRefreshAsync(CancellationToken stoppingToken)
    {
        _cancelledByUser = false;
        var timeoutMinutes = Math.Max(1, refreshOptions.Value.TimeoutMinutes);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _currentRunCts = runCts;
        runCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        try { await eventService.CleanupOldEventsAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Event cleanup failed."); }

        logger.LogInformation("Snapshot refresh started.");
        _refreshStartedAt = timeProvider.GetUtcNow().UtcDateTime;
        eventBus.Publish(AppEventKind.RefreshStarted);
        bool succeeded = false;
        string? errorSummary = null;
        string? changeClass = null;
        IReadOnlySet<string> affectedProfileIds = new HashSet<string>();
        try
        {
            // Hold the heavy-work gate for the whole refresh so background series expansion
            // can't hammer the same provider we're fetching from. Acquired inside the try so a
            // timeout/shutdown while waiting is handled by the existing cancellation catches.
            if (heavyWorkGate.IsHeld)
                logger.LogDebug("Refresh waiting for in-flight background work to yield the heavy-work gate.");
            using var heavyWork = await heavyWorkGate.AcquireAsync(runCts.Token);

            await using var scope = scopeFactory.CreateAsyncScope();
            var builder = scope.ServiceProvider.GetRequiredService<SnapshotBuilder>();
            var (s, e, fetchedProviderIds, cc, profileIds) = await builder.RunAsync(runCts.Token);
            (succeeded, errorSummary, changeClass, affectedProfileIds) = (s, e, cc, profileIds);
            if (fetchedProviderIds.Count > 0)
                await PurgeStaleProviderDataAsync(fetchedProviderIds, stoppingToken);
            logger.LogInformation("Snapshot refresh completed (published={Succeeded}, change={ChangeClass}).", succeeded, changeClass ?? "none");
            if (cc == ChangeClasses.Breaking)
            {
                try
                {
                    await eventService.PublishAsync(SystemEventSeverity.Warning, SystemEventTypes.BreakingLineupChange,
                        "Large lineup change detected",
                        "More than 20% of channels changed — connected clients may need a lineup refresh or rescan.");
                }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to publish BreakingLineupChange event."); }
            }
        }
        catch (OperationCanceledException) when (_cancelledByUser && !stoppingToken.IsCancellationRequested)
        {
            errorSummary = "Cancelled by user.";
            logger.LogInformation("Snapshot refresh cancelled by user.");
        }
        catch (OperationCanceledException) when (!_cancelledByUser && !stoppingToken.IsCancellationRequested && runCts.IsCancellationRequested)
        {
            errorSummary = $"Timed out after {timeoutMinutes} minute(s).";
            logger.LogWarning("Snapshot refresh timed out after {TimeoutMinutes} minute(s).", timeoutMinutes);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Snapshot refresh cancelled due to service shutdown.");
        }
        finally
        {
            activityTracker.Clear();
            _refreshStartedAt = null;
            _currentRunCts = null;
            eventBus.Publish(AppEventKind.RefreshCompleted, succeeded, errorSummary, changeClass,
                affectedProfileIds.Count > 0 ? affectedProfileIds : null);
        }
    }

    // -------------------------------------------------------------------------
    // Data retention — keep last 2 fetch generations per provider
    // -------------------------------------------------------------------------

    private async Task PurgeStaleProviderDataAsync(IEnumerable<string> fetchedProviderIds, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var totalChannels = 0;
            var totalRuns = 0;

            foreach (var providerId in fetchedProviderIds)
            {
                var (channels, runs) = await PurgeProviderGenerationsAsync(db, providerId, ct);
                totalChannels += channels;
                totalRuns += runs;
            }

            if (totalChannels > 0 || totalRuns > 0)
                logger.LogInformation(
                    "Data retention: purged {ChannelCount} stale channel(s) and {RunCount} old fetch run(s).",
                    totalChannels, totalRuns);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Provider data retention purge failed — will retry on next refresh.");
        }
    }

    private static async Task<(int Channels, int Runs)> PurgeProviderGenerationsAsync(
        ApplicationDbContext db, string providerId, CancellationToken ct)
    {
        // Identify the 2 most recent fetch runs — these are the "live" generations to keep.
        var recentRunIds = await db.FetchRuns
            .AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .OrderByDescending(x => x.StartedUtc)
            .Take(2)
            .Select(x => x.FetchRunId)
            .ToListAsync(ct);

        // Fewer than 2 runs means there is no older generation to purge.
        if (recentRunIds.Count < 2)
            return (0, 0);

        // Use a subquery instead of loading IDs into memory — avoids SQLite's 999-parameter
        // limit when a provider has a large channel list with multiple old fetch runs.
        var staleChannelQuery = db.ProviderChannels
            .Where(x => x.ProviderId == providerId && !recentRunIds.Contains(x.LastFetchRunId))
            .Select(x => x.ProviderChannelId);

        var staleCount = await staleChannelQuery.CountAsync(ct);
        if (staleCount > 0)
        {
            // Delete child rows explicitly — SQLite FK cascade requires PRAGMA foreign_keys = ON
            // which is not enabled; follow the same explicit-delete pattern as DeleteProfileAsync.
            await db.ProfileGroupChannelFilters
                .Where(x => staleChannelQuery.Contains(x.ProviderChannelId))
                .ExecuteDeleteAsync(ct);

            await db.ChannelSources
                .Where(x => staleChannelQuery.Contains(x.ProviderChannelId))
                .ExecuteDeleteAsync(ct);

            await db.ProfileCustomGroupChannels
                .Where(x => staleChannelQuery.Contains(x.ProviderChannelId))
                .ExecuteDeleteAsync(ct);

            await db.ProviderChannels
                .Where(x => x.ProviderId == providerId && !recentRunIds.Contains(x.LastFetchRunId))
                .ExecuteDeleteAsync(ct);
        }

        // FetchRun → ProviderChannel FK is Restrict, so delete runs only after channels are gone.
        var deletedRuns = await db.FetchRuns
            .Where(x => x.ProviderId == providerId && !recentRunIds.Contains(x.FetchRunId))
            .ExecuteDeleteAsync(ct);

        return (staleCount, deletedRuns);
    }

    private async Task RunBuildOnlyAsync(CancellationToken stoppingToken)
    {
        _cancelledByUser = false;
        var timeoutMinutes = Math.Max(1, refreshOptions.Value.TimeoutMinutes);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _currentRunCts = runCts;
        runCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

        logger.LogInformation("Snapshot build-only started.");
        _refreshStartedAt = timeProvider.GetUtcNow().UtcDateTime;
        eventBus.Publish(AppEventKind.RefreshStarted);
        bool succeeded = false;
        string? errorSummary = null;
        string? changeClass = null;
        IReadOnlySet<string> affectedProfileIds = new HashSet<string>();
        try
        {
            // Build-only doesn't fetch from providers, but it is still heavy DB work over the
            // full channel set — gate it too so it doesn't contend with expansion's writes.
            using var heavyWork = await heavyWorkGate.AcquireAsync(runCts.Token);

            await using var scope = scopeFactory.CreateAsyncScope();
            var builder = scope.ServiceProvider.GetRequiredService<SnapshotBuilder>();
            var (s, e, cc, profileIds) = await builder.BuildOnlyAsync(runCts.Token);
            (succeeded, errorSummary, changeClass, affectedProfileIds) = (s, e, cc, profileIds);
            logger.LogInformation("Snapshot build-only completed (published={Succeeded}, change={ChangeClass}).", succeeded, changeClass ?? "none");
        }
        catch (OperationCanceledException) when (_cancelledByUser && !stoppingToken.IsCancellationRequested)
        {
            errorSummary = "Cancelled by user.";
            logger.LogInformation("Snapshot build-only cancelled by user.");
        }
        catch (OperationCanceledException) when (!_cancelledByUser && !stoppingToken.IsCancellationRequested && runCts.IsCancellationRequested)
        {
            errorSummary = $"Timed out after {timeoutMinutes} minute(s).";
            logger.LogWarning("Snapshot build-only timed out after {TimeoutMinutes} minute(s).", timeoutMinutes);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Snapshot build-only cancelled due to service shutdown.");
        }
        finally
        {
            activityTracker.Clear();
            _refreshStartedAt = null;
            _currentRunCts = null;
            eventBus.Publish(AppEventKind.RefreshCompleted, succeeded, errorSummary, changeClass,
                affectedProfileIds.Count > 0 ? affectedProfileIds : null);
        }
    }
}
