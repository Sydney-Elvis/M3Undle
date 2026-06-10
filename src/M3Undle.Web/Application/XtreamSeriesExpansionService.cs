using System.Collections.Concurrent;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed record XtreamSeriesStub(int SeriesId, long LastModifiedEpoch);

public sealed record XtreamSeriesExpanded(int SeriesId, long LastModifiedEpoch, string EpisodesJson);

public sealed record XtreamSeriesExpansionJob(
    string ProviderId,
    string ProviderName,
    string BaseUrl,
    string Username,
    string Password,
    int TimeoutSeconds,
    IReadOnlyList<XtreamSeriesStub> Series,
    int Priority = 1);

public sealed record XtreamSeriesExpansionStatus(
    string ProviderId,
    string ProviderName,
    int Total,
    int Completed,
    int Failed,
    DateTime StartedUtc);

public interface IXtreamSeriesExpansionQueue
{
    /// <summary>
    /// Queue a background series expansion job. Returns <c>false</c> when a job for the same
    /// provider is already queued or running — the remainder is re-derived on the next refresh,
    /// so dropped enqueues are never lost work.
    /// </summary>
    bool TryEnqueue(XtreamSeriesExpansionJob job);

    /// <summary>
    /// Expand as much of the job as fits inside <paramref name="budget"/>, persisting results
    /// and returning what was expanded so the caller can publish it immediately. The remainder
    /// is automatically queued for background expansion. Returns an empty list (and queues
    /// nothing extra) when a job for this provider is already queued or running.
    /// </summary>
    Task<IReadOnlyList<XtreamSeriesExpanded>> TryExpandInlineAsync(
        XtreamSeriesExpansionJob job, TimeSpan budget, CancellationToken cancellationToken);

    /// <summary>Statuses of currently running jobs (empty when idle).</summary>
    IReadOnlyList<XtreamSeriesExpansionStatus> ActiveJobs { get; }

    /// <summary>Number of jobs waiting for a slot.</summary>
    int WaitingJobs { get; }
}

/// <summary>
/// Background worker that expands Xtream series into episodes via get_series_info.
/// The lineup fetch first expands inline within a small time budget (fast providers load
/// completely on first sync), then hands the remainder here. Jobs for different providers
/// run concurrently — a slow 29K-series panel never blocks a fast one — and providers
/// linked to the active profile are scheduled first. Progress is persisted in batches,
/// so a restart or shutdown resumes where it left off on the next refresh.
/// </summary>
public sealed class XtreamSeriesExpansionService(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IRefreshTrigger refreshTrigger,
    IEventService eventService,
    ILogger<XtreamSeriesExpansionService> logger)
    : BackgroundService, IXtreamSeriesExpansionQueue
{
    // Parallel get_series_info calls per provider job. Same code measures ~14.6 series/s on a
    // fast panel at 4 — if a slow panel's rate doesn't improve at 8, the panel is serializing
    // per-account and more concurrency won't help (the completion log prints the rate).
    private const int ExpandConcurrency = 8;
    private const int SaveBatchSize = 100;
    // Trigger an intermediate snapshot rebuild every N saved series so episodes
    // appear progressively on very large providers instead of all at the end.
    private const int ProgressRefreshEvery = 2500;
    // Provider jobs running simultaneously (different panels — no shared rate limit).
    private const int MaxConcurrentJobs = 2;

    private sealed record QueuedJob(XtreamSeriesExpansionJob Job, DateTime EnqueuedUtc);

    private readonly Lock _lock = new();
    private readonly List<QueuedJob> _waiting = [];
    private readonly HashSet<string> _knownProviders = [];   // queued, running, or inline-expanding
    private readonly ConcurrentDictionary<string, XtreamSeriesExpansionStatus> _running = new();
    private readonly SemaphoreSlim _wake = new(0);
    private int _runningCount;

    public IReadOnlyList<XtreamSeriesExpansionStatus> ActiveJobs => [.. _running.Values];

    public int WaitingJobs
    {
        get { lock (_lock) { return _waiting.Count; } }
    }

    public bool TryEnqueue(XtreamSeriesExpansionJob job)
    {
        lock (_lock)
        {
            if (!_knownProviders.Add(job.ProviderId))
                return false;
            _waiting.Add(new QueuedJob(job, DateTime.UtcNow));
        }
        _wake.Release();
        return true;
    }

    public async Task<IReadOnlyList<XtreamSeriesExpanded>> TryExpandInlineAsync(
        XtreamSeriesExpansionJob job, TimeSpan budget, CancellationToken cancellationToken)
    {
        // If this provider is already queued or mid-expansion, don't double-fetch the same
        // series — build from cache and let the existing job finish.
        lock (_lock)
        {
            if (!_knownProviders.Add(job.ProviderId))
                return [];
        }

        List<XtreamSeriesExpanded> expanded;
        List<XtreamSeriesStub> remainder;
        try
        {
            var deadline = DateTime.UtcNow + budget;
            (expanded, remainder, _) = await ExpandCoreAsync(job, deadline, progress: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Refresh was cancelled mid-inline — push the whole job to the background instead.
            EnqueueOwned(job);
            throw;
        }
        catch
        {
            lock (_lock) { _knownProviders.Remove(job.ProviderId); }
            throw;
        }

        if (remainder.Count > 0)
        {
            logger.LogInformation(
                "Inline series expansion hit its {Budget}s budget for provider {ProviderId}: {Done}/{Total} done — queuing {Remainder} for background.",
                (int)budget.TotalSeconds, job.ProviderId, expanded.Count, job.Series.Count, remainder.Count);
            EnqueueOwned(job with { Series = remainder });
        }
        else
        {
            logger.LogInformation(
                "Inline series expansion completed for provider {ProviderId}: {Done}/{Total} series within budget.",
                job.ProviderId, expanded.Count, job.Series.Count);
            lock (_lock) { _knownProviders.Remove(job.ProviderId); }
        }

        return expanded;
    }

    // Enqueue a job whose provider slot is already registered in _knownProviders (inline handoff).
    private void EnqueueOwned(XtreamSeriesExpansionJob job)
    {
        lock (_lock) { _waiting.Add(new QueuedJob(job, DateTime.UtcNow)); }
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (true)
            {
                XtreamSeriesExpansionJob? next;
                lock (_lock)
                {
                    if (_runningCount >= MaxConcurrentJobs || _waiting.Count == 0)
                        break;

                    // Active-profile providers first, then FIFO.
                    var pick = _waiting
                        .OrderBy(x => x.Job.Priority)
                        .ThenBy(x => x.EnqueuedUtc)
                        .First();
                    _waiting.Remove(pick);
                    next = pick.Job;
                    _runningCount++;
                }

                var job = next;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExpandJobAsync(job, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Shutdown — saved batches persist, remainder resumes next refresh.
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Series expansion job failed for provider {ProviderId}.", job.ProviderId);
                    }
                    finally
                    {
                        _running.TryRemove(job.ProviderId, out _);
                        lock (_lock)
                        {
                            _knownProviders.Remove(job.ProviderId);
                            _runningCount--;
                        }
                        _wake.Release();
                    }
                }, stoppingToken);
            }
        }
    }

    internal async Task ExpandJobAsync(XtreamSeriesExpansionJob job, CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        logger.LogInformation(
            "Series expansion started for provider {ProviderId}: {Count} series queued, concurrency {Concurrency}, priority {Priority}.",
            job.ProviderId, job.Series.Count, ExpandConcurrency, job.Priority);

        _running[job.ProviderId] = new XtreamSeriesExpansionStatus(
            job.ProviderId, job.ProviderName, job.Series.Count, 0, 0, startedUtc);

        var savedSinceRefresh = 0;

        void OnProgress(int completed, int failed)
        {
            _running[job.ProviderId] = new XtreamSeriesExpansionStatus(
                job.ProviderId, job.ProviderName, job.Series.Count, completed, failed, startedUtc);

            var sinceRefresh = Interlocked.Increment(ref savedSinceRefresh);
            if (sinceRefresh >= ProgressRefreshEvery)
            {
                Interlocked.Exchange(ref savedSinceRefresh, 0);
                refreshTrigger.TriggerRefresh();
            }
        }

        var (expanded, _, failed) = await ExpandCoreAsync(job, deadline: null, OnProgress, cancellationToken);

        var elapsed = DateTime.UtcNow - startedUtc;
        var rate = elapsed.TotalSeconds > 0 ? expanded.Count / elapsed.TotalSeconds : 0;
        logger.LogInformation(
            "Series expansion finished for provider {ProviderId}: {Completed} expanded, {Failed} failed in {Elapsed:hh\\:mm\\:ss} ({Rate:F1} series/s).",
            job.ProviderId, expanded.Count, failed, elapsed, rate);

        if (expanded.Count > 0)
        {
            await PublishCompletionEventAsync(job, expanded.Count, failed);
            await TriggerRefreshWithRetryAsync(cancellationToken);
        }
    }

    // -------------------------------------------------------------------------
    // Expansion core — worker pool shared by inline and background paths.
    // Workers pull from a shared queue so the pipeline stays full (no batch-tail
    // drain); persistence happens off the fetch path under its own gate.
    // -------------------------------------------------------------------------

    private async Task<(List<XtreamSeriesExpanded> Expanded, List<XtreamSeriesStub> Remainder, int Failed)> ExpandCoreAsync(
        XtreamSeriesExpansionJob job,
        DateTime? deadline,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        var pending = new ConcurrentQueue<XtreamSeriesStub>(job.Series);
        var expanded = new List<XtreamSeriesExpanded>();
        var failedStubs = new ConcurrentBag<XtreamSeriesStub>();
        var saveBuffer = new List<XtreamSeriesExpanded>();
        var bufferLock = new Lock();
        var persistGate = new SemaphoreSlim(1);
        int completed = 0, failed = 0;

        using var client = httpClientFactory.CreateClient();

        async Task PersistBufferedAsync(bool force)
        {
            List<XtreamSeriesExpanded>? toSave = null;
            lock (bufferLock)
            {
                if (saveBuffer.Count >= SaveBatchSize || (force && saveBuffer.Count > 0))
                {
                    toSave = [.. saveBuffer];
                    saveBuffer.Clear();
                }
            }
            if (toSave is null)
                return;

            await persistGate.WaitAsync(CancellationToken.None);
            try { await PersistBatchAsync(job.ProviderId, toSave); }
            finally { persistGate.Release(); }
        }

        async Task WorkerAsync()
        {
            while (!cancellationToken.IsCancellationRequested
                   && (deadline is null || DateTime.UtcNow < deadline)
                   && pending.TryDequeue(out var stub))
            {
                string json;
                try
                {
                    var infoUrl =
                        $"{job.BaseUrl}/player_api.php?username={Uri.EscapeDataString(job.Username)}" +
                        $"&password={Uri.EscapeDataString(job.Password)}&action=get_series_info&series_id={stub.SeriesId}";
                    json = await HttpFetchHelper.FetchStringAsync(client, infoUrl, job.TimeoutSeconds, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or ProviderFetchException)
                {
                    logger.LogDebug("get_series_info failed for series {SeriesId}: {Message}", stub.SeriesId, ex.Message);
                    failedStubs.Add(stub);
                    progress?.Invoke(completed, Interlocked.Increment(ref failed));
                    continue;
                }

                var item = new XtreamSeriesExpanded(stub.SeriesId, stub.LastModifiedEpoch, json);
                lock (bufferLock)
                {
                    expanded.Add(item);
                    saveBuffer.Add(item);
                }
                progress?.Invoke(Interlocked.Increment(ref completed), failed);
                await PersistBufferedAsync(force: false);
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, ExpandConcurrency).Select(_ => WorkerAsync()));
        }
        finally
        {
            // Always flush what we have — even on cancellation, completed work is kept.
            await PersistBufferedAsync(force: true);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Remainder = untouched series + failures (failures retry in the background/next sync).
        var remainder = pending.ToList();
        remainder.AddRange(failedStubs);
        return (expanded, remainder, failed);
    }

    private async Task PersistBatchAsync(string providerId, List<XtreamSeriesExpanded> items)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var item in items)
        {
            var existing = await db.XtreamSeriesCache.FindAsync([providerId, item.SeriesId], CancellationToken.None);
            if (existing is null)
            {
                db.XtreamSeriesCache.Add(new XtreamSeriesCache
                {
                    ProviderId = providerId,
                    SeriesId = item.SeriesId,
                    LastModifiedEpoch = item.LastModifiedEpoch,
                    EpisodesJson = item.EpisodesJson,
                });
            }
            else
            {
                existing.LastModifiedEpoch = item.LastModifiedEpoch;
                existing.EpisodesJson = item.EpisodesJson;
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task PublishCompletionEventAsync(XtreamSeriesExpansionJob job, int completed, int failed)
    {
        try
        {
            await eventService.PublishAsync(
                failed > 0 ? SystemEventSeverity.Warning : SystemEventSeverity.Info,
                SystemEventTypes.SeriesSyncCompleted,
                $"Series sync complete for '{job.ProviderName}'",
                failed > 0
                    ? $"{completed:N0} series expanded, {failed:N0} failed (will retry on next refresh)."
                    : $"{completed:N0} series expanded.",
                providerId: job.ProviderId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to publish SeriesSyncCompleted event for provider {ProviderId}.", job.ProviderId);
        }
    }

    private async Task TriggerRefreshWithRetryAsync(CancellationToken cancellationToken)
    {
        // A refresh may already be running — retry briefly so the new episodes get published.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (refreshTrigger.TriggerRefresh())
                return;
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
        logger.LogWarning("Could not queue a snapshot refresh after series expansion — episodes will publish on the next scheduled refresh.");
    }
}
