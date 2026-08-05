using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
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
    int Priority = 1,
    /// <summary>
    /// How many times this job's leftovers have already been automatically re-queued.
    /// Bounded by <c>MaxRetryGenerations</c> so series that can never succeed (e.g. a provider's
    /// permanently-missing catalog IDs, which 404 every attempt) stop being retried instead of
    /// looping forever. Once the cap is hit the leftovers simply wait for the next natural
    /// refresh, which re-derives them from the cache diff anyway.
    /// </summary>
    int RetryGeneration = 0,
    /// <summary>
    /// Series successfully expanded by earlier hops of this retry chain. Carried forward so the
    /// terminal hop still triggers a snapshot refresh covering work done by earlier hops, even
    /// when the terminal hop itself expands nothing (e.g. its leftovers were all permanent 404s).
    /// </summary>
    int ExpandedSoFar = 0);

/// <summary>Result of one background expansion job — what it expanded and what it left behind.</summary>
internal sealed record XtreamSeriesExpansionOutcome(List<XtreamSeriesStub> Remainder, int Expanded);

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

    /// <summary>
    /// Runs <paramref name="write"/> under the same gate that serializes every
    /// XtreamSeriesCache write made by this service's background jobs. SQLite only tolerates
    /// one writer at a time — any other code writing to XtreamSeriesCache (e.g.
    /// XtreamLineupClient's stale-row purge) must go through here too, or it can collide with a
    /// job's write and throw "database table is locked" (confirmed live).
    /// </summary>
    Task RunSeriesCacheWriteAsync(Func<Task> write, CancellationToken cancellationToken);
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
    HeavyWorkGate heavyWorkGate,
    ILogger<XtreamSeriesExpansionService> logger)
    : BackgroundService, IXtreamSeriesExpansionQueue
{
    // TEMP DIAGNOSTIC (remove once the Onyx/Delta/third-provider concurrency test is done):
    // starting point for the adaptive concurrency search in ExpandCoreAsync (see RoundBudget /
    // ConcurrencyStep there) — not a fixed value for the whole job anymore. Overridable via
    // M3UNDLE_SERIES_EXPAND_CONCURRENCY so each test run can pick a different starting point
    // without a rebuild. Falls back to 8 when unset or invalid.
    private static readonly int ExpandConcurrency =
        int.TryParse(Environment.GetEnvironmentVariable("M3UNDLE_SERIES_EXPAND_CONCURRENCY"), out var expandConcurrencyOverride)
        && expandConcurrencyOverride > 0
            ? expandConcurrencyOverride
            : 8;

    // TEMP DIAGNOSTIC (remove once the Onyx/Delta/third-provider concurrency test is done):
    // delay between starting each successive worker within a round, so a round's workers don't
    // all fire their first request in the same instant. Overridable via
    // M3UNDLE_SERIES_EXPAND_STAGGER_MS.
    private static readonly int WorkerStartStaggerMs =
        int.TryParse(Environment.GetEnvironmentVariable("M3UNDLE_SERIES_EXPAND_STAGGER_MS"), out var staggerOverride)
        && staggerOverride >= 0
            ? staggerOverride
            : 75;

    // TEMP DIAGNOSTIC (remove once the Onyx/Delta/third-provider concurrency test is done):
    // ceiling for the adaptive search in ExpandCoreAsync — Onyx showed diminishing returns above
    // 24 (32 was measurably worse) even without any 503s, which the 503-based search alone
    // wouldn't catch. Overridable via M3UNDLE_SERIES_EXPAND_CONCURRENCY_MAX.
    private static readonly int ExpandConcurrencyMax =
        int.TryParse(Environment.GetEnvironmentVariable("M3UNDLE_SERIES_EXPAND_CONCURRENCY_MAX"), out var expandConcurrencyMaxOverride)
        && expandConcurrencyMaxOverride > 0
            ? expandConcurrencyMaxOverride
            : 24;

    private const int SaveBatchSize = 100;
    // How many times a job's leftovers may be automatically re-queued before they're left for
    // the next natural refresh. Bounds the retry chain — see XtreamSeriesExpansionJob.RetryGeneration.
    private const int MaxRetryGenerations = 3;
    // Provider jobs running simultaneously (different panels — no shared rate limit).
    private const int MaxConcurrentJobs = 2;

    private sealed record QueuedJob(XtreamSeriesExpansionJob Job, DateTime EnqueuedUtc);

    private readonly Lock _lock = new();
    private readonly List<QueuedJob> _waiting = [];
    private readonly HashSet<string> _knownProviders = [];   // queued, running, or inline-expanding
    private readonly ConcurrentDictionary<string, XtreamSeriesExpansionStatus> _running = new();
    private readonly SemaphoreSlim _wake = new(0);
    private int _runningCount;

    // Serializes every XtreamSeriesCache write, from any source — this service's own background
    // jobs (across MaxConcurrentJobs providers) AND XtreamLineupClient's stale-row purge, which
    // runs on a totally separate refresh path and used to write to the same table completely
    // ungated. SQLite only tolerates one writer at a time; two of these colliding throws
    // "database table is locked" (SqliteErrorCode 6) — confirmed live twice: once was jobs
    // racing each other (fixed by making this gate instance-level instead of per-job-local),
    // and it still happened after that with a lineup refresh running concurrently with a
    // background job, which is what RunSeriesCacheWriteAsync below closes.
    private readonly SemaphoreSlim _persistGate = new(1);

    // TEMP DIAGNOSTIC (remove once the Onyx/Delta/third-provider concurrency test is done):
    // per-provider concurrency learned by the adaptive search in ExpandCoreAsync. Without this,
    // every new ExpandCoreAsync call (inline handoff to background, or a later background job
    // for the same provider) starts the search over from ExpandConcurrency instead of reusing
    // what was already learned — confirmed wasteful in practice (re-climbed 8→10→12 and re-hit
    // the same 503 ceiling a second time right after the inline pass had just locked at 10).
    private readonly ConcurrentDictionary<string, int> _learnedConcurrency = new();

    public IReadOnlyList<XtreamSeriesExpansionStatus> ActiveJobs => [.. _running.Values];

    public int WaitingJobs
    {
        get { lock (_lock) { return _waiting.Count; } }
    }

    public async Task RunSeriesCacheWriteAsync(Func<Task> write, CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken);
        try { await write(); }
        finally { _persistGate.Release(); }
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
            // gateRounds: false — inline expansion runs inside the snapshot refresh, which
            // already holds the heavy-work gate. Acquiring it here would self-deadlock.
            (expanded, remainder, _) = await ExpandCoreAsync(job, deadline, progress: null, gateRounds: false, cancellationToken);
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
                    XtreamSeriesExpansionOutcome? outcome = null;
                    try
                    {
                        outcome = await ExpandJobAsync(job, stoppingToken);
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

                    // Re-queue leftover failures/pending immediately instead of waiting for the
                    // next full refresh — the provider slot was just freed above, so this is a
                    // normal TryEnqueue, not a special-cased bypass. Bounded by
                    // MaxRetryGenerations: leftovers that keep failing (permanently-missing
                    // catalog IDs 404 on every attempt) would otherwise re-queue forever, and
                    // each hop also hammers a provider that may already be rate-limiting.
                    if (outcome is { Remainder.Count: > 0 })
                    {
                        if (!WillAutoRetry(job, outcome.Remainder.Count))
                        {
                            logger.LogInformation(
                                "Series expansion leaving {Remainder} unfinished for provider {ProviderId} after {Generations} retry generation(s) — deferring to the next refresh.",
                                outcome.Remainder.Count, job.ProviderId, job.RetryGeneration);
                        }
                        else
                        {
                            logger.LogInformation(
                                "Series expansion job leaving {Remainder} unfinished for provider {ProviderId} — re-queuing immediately (generation {Generation}).",
                                outcome.Remainder.Count, job.ProviderId, job.RetryGeneration + 1);
                            TryEnqueue(job with
                            {
                                Series = outcome.Remainder,
                                RetryGeneration = job.RetryGeneration + 1,
                                ExpandedSoFar = job.ExpandedSoFar + outcome.Expanded,
                            });
                        }
                    }
                }, stoppingToken);
            }
        }
    }

    internal async Task<XtreamSeriesExpansionOutcome> ExpandJobAsync(XtreamSeriesExpansionJob job, CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        logger.LogInformation(
            "Series expansion started for provider {ProviderId}: {Count} series queued, concurrency {Concurrency}, priority {Priority}.",
            job.ProviderId, job.Series.Count, ExpandConcurrency, job.Priority);

        _running[job.ProviderId] = new XtreamSeriesExpansionStatus(
            job.ProviderId, job.ProviderName, job.Series.Count, 0, 0, startedUtc);

        // Progress only updates the dashboard status here — it deliberately does not trigger a
        // snapshot refresh. Publishing newly expanded episodes requires a full FetchAndBuild
        // (episode channels are materialised at fetch time by XtreamLineupClient's
        // BuildSeriesChannels, so a cheap BuildOnly would republish the same set), and doing that
        // mid-job cost far more than it delivered: measured on a 28,988-series provider, an
        // intermediate refresh every 2,500 series stalled expansion ~4.7 min each time —
        // ~35 min of real work stretched to ~87 min — and each one re-fetched every provider's
        // full playlist, not just the one being expanded. The terminal-hop refresh in this method
        // publishes everything once the job settles; anything interrupted before then is still
        // persisted in XtreamSeriesCache and publishes on the next refresh's cache diff.
        void OnProgress(int completed, int failed)
            => _running[job.ProviderId] = new XtreamSeriesExpansionStatus(
                job.ProviderId, job.ProviderName, job.Series.Count, completed, failed, startedUtc);

        var (expanded, remainder, failed) = await ExpandCoreAsync(job, deadline: null, OnProgress, gateRounds: true, cancellationToken);

        var elapsed = DateTime.UtcNow - startedUtc;
        var rate = elapsed.TotalSeconds > 0 ? expanded.Count / elapsed.TotalSeconds : 0;
        logger.LogInformation(
            "Series expansion finished for provider {ProviderId}: {Completed} expanded, {Failed} failed in {Elapsed:hh\\:mm\\:ss} ({Rate:F1} series/s).",
            job.ProviderId, expanded.Count, failed, elapsed, rate);

        // Publish + refresh only on the terminal hop of a retry chain. Every hop used to trigger
        // a full snapshot refresh (which re-fetches the provider's entire playlist), so a job
        // that left leftovers produced a burst of back-to-back full refreshes against a provider
        // that was often already rate-limiting — confirmed live. ExpandedSoFar carries earlier
        // hops' work forward so the terminal hop still publishes it even if it expanded nothing.
        var totalExpanded = job.ExpandedSoFar + expanded.Count;
        if (totalExpanded > 0 && !WillAutoRetry(job, remainder.Count))
        {
            await PublishCompletionEventAsync(job, totalExpanded, failed);
            await TriggerRefreshWithRetryAsync(cancellationToken);
        }

        return new XtreamSeriesExpansionOutcome(remainder, expanded.Count);
    }

    /// <summary>
    /// Whether <paramref name="job"/>'s leftovers will be automatically re-queued. Single source
    /// of truth so ExpandJobAsync's "is this the terminal hop?" check and the worker loop's
    /// re-queue decision can never disagree.
    /// </summary>
    private static bool WillAutoRetry(XtreamSeriesExpansionJob job, int remainderCount)
        => remainderCount > 0 && job.RetryGeneration < MaxRetryGenerations;

    // -------------------------------------------------------------------------
    // Expansion core — worker pool shared by inline and background paths.
    // Workers pull from a shared queue so the pipeline stays full (no batch-tail
    // drain); persistence happens off the fetch path under its own gate.
    // -------------------------------------------------------------------------

    /// <param name="gateRounds">
    /// Whether each round should be run under <see cref="HeavyWorkGate"/>. True for background
    /// jobs. <b>Must be false for inline expansion</b> — inline runs inside the snapshot refresh,
    /// which already holds the gate, and the gate is not reentrant.
    /// </param>
    private async Task<(List<XtreamSeriesExpanded> Expanded, List<XtreamSeriesStub> Remainder, int Failed)> ExpandCoreAsync(
        XtreamSeriesExpansionJob job,
        DateTime? deadline,
        Action<int, int>? progress,
        bool gateRounds,
        CancellationToken cancellationToken)
    {
        var pending = new ConcurrentQueue<XtreamSeriesStub>(job.Series);
        var expanded = new List<XtreamSeriesExpanded>();
        var failedStubs = new ConcurrentBag<XtreamSeriesStub>();
        var saveBuffer = new List<XtreamSeriesExpanded>();
        var bufferLock = new Lock();
        int completed = 0, failed = 0;

        using var client = httpClientFactory.CreateClient();
        // TEMP DIAGNOSTIC (remove after the concurrency test): timestamps the checkpoint log below.
        var jobStopwatch = Stopwatch.StartNew();

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

            await _persistGate.WaitAsync(CancellationToken.None);
            try { await PersistBatchAsync(job.ProviderId, toSave); }
            finally { _persistGate.Release(); }
        }

        // TEMP DIAGNOSTIC (remove once the Onyx/Delta/third-provider concurrency test is done):
        // adaptive concurrency search, replacing the shared-backoff attempt (which made Delta
        // worse — see the commit history — because releasing a whole paused pool at once just
        // recreates the burst that triggered the pause). Runs the job in rounds of up to
        // RoundBudget dequeued items. A round with zero 503s means this level is fine: record it
        // as the last known-good level and step up by ConcurrencyStep for the next round. The
        // first round that sees any 503 locks concurrency at the last known-good level for the
        // rest of the job — no further probing, no oscillation. If even the starting level fails
        // before any round succeeds, step down instead, floored at 1.
        const int RoundBudget = 250;
        const int ConcurrencyStep = 2;

        var currentConcurrency = _learnedConcurrency.GetValueOrDefault(job.ProviderId, ExpandConcurrency);
        int? lastGoodConcurrency = null;
        var concurrencyLocked = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && (deadline is null || DateTime.UtcNow < deadline)
                   && !pending.IsEmpty)
            {
                var roundConcurrency = currentConcurrency;
                var roundBudgetRemaining = RoundBudget;
                var got503ThisRound = 0;

                async Task RoundWorkerAsync(int startDelayMs)
                {
                    if (startDelayMs > 0)
                        await Task.Delay(startDelayMs, cancellationToken);

                    while (!cancellationToken.IsCancellationRequested
                           && (deadline is null || DateTime.UtcNow < deadline)
                           && Interlocked.Decrement(ref roundBudgetRemaining) >= 0
                           && pending.TryDequeue(out var stub))
                    {
                        string json;
                        // TEMP DIAGNOSTIC (remove after the concurrency test): per-request timing so
                        // a failure log line can show how long the request ran before it failed.
                        var requestStopwatch = Stopwatch.StartNew();
                        try
                        {
                            var infoUrl =
                                $"{job.BaseUrl}/player_api.php?username={Uri.EscapeDataString(job.Username)}" +
                                $"&password={Uri.EscapeDataString(job.Password)}&action=get_series_info&series_id={stub.SeriesId}";
                            json = await HttpFetchHelper.FetchStringAsync(client, infoUrl, job.TimeoutSeconds, cancellationToken);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or ProviderFetchException)
                        {
                            // TEMP DIAGNOSTIC (remove after the concurrency test): status code +
                            // exception type + elapsed time, at Warning so it shows up without
                            // enabling Debug logging.
                            var statusCode = (ex as HttpRequestException)?.StatusCode;
                            logger.LogWarning(
                                "get_series_info FAILED provider={ProviderId} series={SeriesId} concurrency={Concurrency} elapsedMs={ElapsedMs} exceptionType={ExceptionType} status={StatusCode} message={Message}",
                                job.ProviderId, stub.SeriesId, roundConcurrency, requestStopwatch.ElapsedMilliseconds,
                                ex.GetType().Name, statusCode, ex.Message);

                            if (statusCode == HttpStatusCode.ServiceUnavailable)
                                Interlocked.Increment(ref got503ThisRound);

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
                        var completedCount = Interlocked.Increment(ref completed);
                        progress?.Invoke(completedCount, failed);

                        // TEMP DIAGNOSTIC (remove after the concurrency test): periodic checkpoint
                        // so throughput and failure clustering can be read straight off the log.
                        if (completedCount % 250 == 0)
                        {
                            logger.LogInformation(
                                "Series expansion checkpoint provider={ProviderId} concurrency={Concurrency} completed={Completed} failed={Failed} elapsedMs={ElapsedMs}",
                                job.ProviderId, roundConcurrency, completedCount, failed, jobStopwatch.ElapsedMilliseconds);
                        }

                        await PersistBufferedAsync(force: false);
                    }
                }

                // Gate per round rather than per job: a refresh waiting to start is delayed by at
                // most one round instead of a multi-minute job. Inline expansion passes
                // gateRounds: false because it already runs under the refresh's hold.
                var heavyWork = gateRounds ? await heavyWorkGate.AcquireAsync(cancellationToken) : null;
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, roundConcurrency).Select(i => RoundWorkerAsync(i * WorkerStartStaggerMs)));
                }
                finally
                {
                    heavyWork?.Dispose();
                }

                if (got503ThisRound > 0)
                {
                    if (concurrencyLocked)
                    {
                        // Already locked onto a known-good level — nothing to adjust.
                    }
                    else if (lastGoodConcurrency is int good)
                    {
                        currentConcurrency = good;
                        concurrencyLocked = true;
                        logger.LogInformation(
                            "Series expansion adaptive-concurrency LOCKED provider={ProviderId} concurrency={Concurrency} (round at {RoundConcurrency} saw {Got503}x 503)",
                            job.ProviderId, currentConcurrency, roundConcurrency, got503ThisRound);
                    }
                    else if (roundConcurrency <= 1)
                    {
                        currentConcurrency = 1;
                        concurrencyLocked = true;
                        logger.LogInformation(
                            "Series expansion adaptive-concurrency FLOORED provider={ProviderId} concurrency=1 (still saw {Got503}x 503)",
                            job.ProviderId, got503ThisRound);
                    }
                    else
                    {
                        currentConcurrency = Math.Max(1, roundConcurrency - ConcurrencyStep);
                        logger.LogInformation(
                            "Series expansion adaptive-concurrency STEP DOWN provider={ProviderId} concurrency={Concurrency} (from {RoundConcurrency}, {Got503}x 503)",
                            job.ProviderId, currentConcurrency, roundConcurrency, got503ThisRound);
                    }
                }
                else if (!concurrencyLocked)
                {
                    lastGoodConcurrency = roundConcurrency;
                    var next = Math.Min(roundConcurrency + ConcurrencyStep, ExpandConcurrencyMax);
                    if (next != roundConcurrency)
                    {
                        currentConcurrency = next;
                        logger.LogInformation(
                            "Series expansion adaptive-concurrency STEP UP provider={ProviderId} concurrency={Concurrency} (from {RoundConcurrency} clean)",
                            job.ProviderId, currentConcurrency, roundConcurrency);
                    }
                    // else: already at ExpandConcurrencyMax and still clean — stay there, nothing to log.
                }

                // TEMP DIAGNOSTIC (remove after the concurrency test): persist whatever the
                // search currently believes for this provider, so the next call to
                // ExpandCoreAsync — inline handoff to background, or a later background job —
                // starts from here instead of re-climbing from ExpandConcurrency every time.
                _learnedConcurrency[job.ProviderId] = currentConcurrency;
            }
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
