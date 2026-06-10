using System.Threading.Channels;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

public sealed record XtreamSeriesStub(int SeriesId, long LastModifiedEpoch);

public sealed record XtreamSeriesExpansionJob(
    string ProviderId,
    string ProviderName,
    string BaseUrl,
    string Username,
    string Password,
    int TimeoutSeconds,
    IReadOnlyList<XtreamSeriesStub> Series);

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

    /// <summary>Status of the currently running job, or <c>null</c> when idle.</summary>
    XtreamSeriesExpansionStatus? CurrentStatus { get; }
}

/// <summary>
/// Background worker that expands Xtream series into episodes via get_series_info.
/// Lineup fetch never blocks on this: it enqueues new/changed series here and builds
/// from whatever the cache already holds. Progress is persisted in batches, so a
/// restart or shutdown resumes where it left off on the next refresh.
/// </summary>
public sealed class XtreamSeriesExpansionService(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IRefreshTrigger refreshTrigger,
    IEventService eventService,
    ILogger<XtreamSeriesExpansionService> logger)
    : BackgroundService, IXtreamSeriesExpansionQueue
{
    // 4 parallel get_series_info calls — enough to cut hours to ~quarter without
    // looking like abuse to panels that IP-block aggressive clients.
    private const int ExpandConcurrency = 4;
    private const int SaveBatchSize = 100;
    // Trigger an intermediate snapshot rebuild every N saved series so episodes
    // appear progressively on very large providers instead of all at the end.
    private const int ProgressRefreshEvery = 2500;

    private readonly Channel<XtreamSeriesExpansionJob> _jobs = Channel.CreateUnbounded<XtreamSeriesExpansionJob>();
    private readonly HashSet<string> _pendingProviders = [];
    private readonly Lock _pendingLock = new();
    private volatile XtreamSeriesExpansionStatus? _currentStatus;

    public XtreamSeriesExpansionStatus? CurrentStatus => _currentStatus;

    public bool TryEnqueue(XtreamSeriesExpansionJob job)
    {
        lock (_pendingLock)
        {
            if (!_pendingProviders.Add(job.ProviderId))
                return false;
        }

        if (_jobs.Writer.TryWrite(job))
            return true;

        lock (_pendingLock) { _pendingProviders.Remove(job.ProviderId); }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ExpandJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown — saved batches persist, remainder resumes next refresh.
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Series expansion job failed for provider {ProviderId}.", job.ProviderId);
            }
            finally
            {
                _currentStatus = null;
                lock (_pendingLock) { _pendingProviders.Remove(job.ProviderId); }
            }
        }
    }

    internal async Task ExpandJobAsync(XtreamSeriesExpansionJob job, CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;
        logger.LogInformation(
            "Series expansion started for provider {ProviderId}: {Count} series queued, concurrency {Concurrency}.",
            job.ProviderId, job.Series.Count, ExpandConcurrency);

        using var client = httpClientFactory.CreateClient();
        var sem = new SemaphoreSlim(ExpandConcurrency);
        int completed = 0, failed = 0, savedSinceRefresh = 0;
        var anySaved = false;

        _currentStatus = new XtreamSeriesExpansionStatus(
            job.ProviderId, job.ProviderName, job.Series.Count, 0, 0, startedUtc);

        foreach (var batch in job.Series.Chunk(SaveBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var results = new (XtreamSeriesStub Stub, string? EpisodesJson)[batch.Length];

            await Task.WhenAll(batch.Select(async (stub, idx) =>
            {
                await sem.WaitAsync(cancellationToken);
                try
                {
                    var infoUrl =
                        $"{job.BaseUrl}/player_api.php?username={Uri.EscapeDataString(job.Username)}" +
                        $"&password={Uri.EscapeDataString(job.Password)}&action=get_series_info&series_id={stub.SeriesId}";
                    var json = await HttpFetchHelper.FetchStringAsync(client, infoUrl, job.TimeoutSeconds, cancellationToken);
                    results[idx] = (stub, json);
                }
                catch (Exception ex) when (ex is HttpRequestException or ProviderFetchException)
                {
                    logger.LogDebug("get_series_info failed for series {SeriesId}: {Message}", stub.SeriesId, ex.Message);
                    results[idx] = (stub, null);
                }
                finally { sem.Release(); }
            }));

            int batchSaved;
            try
            {
                batchSaved = await PersistBatchAsync(job.ProviderId, results, cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // Provider was most likely deleted mid-job — abort, nothing left to do.
                logger.LogWarning(ex, "Series cache persist failed for provider {ProviderId} — aborting expansion job.", job.ProviderId);
                return;
            }

            completed += batchSaved;
            failed += batch.Length - batchSaved;
            anySaved |= batchSaved > 0;
            savedSinceRefresh += batchSaved;

            _currentStatus = new XtreamSeriesExpansionStatus(
                job.ProviderId, job.ProviderName, job.Series.Count, completed, failed, startedUtc);

            if (savedSinceRefresh >= ProgressRefreshEvery)
            {
                savedSinceRefresh = 0;
                refreshTrigger.TriggerRefresh();
            }
        }

        var elapsed = DateTime.UtcNow - startedUtc;
        logger.LogInformation(
            "Series expansion finished for provider {ProviderId}: {Completed} expanded, {Failed} failed in {Elapsed:hh\\:mm\\:ss}.",
            job.ProviderId, completed, failed, elapsed);

        if (anySaved)
        {
            await PublishCompletionEventAsync(job, completed, failed);
            await TriggerRefreshWithRetryAsync(cancellationToken);
        }
    }

    private async Task<int> PersistBatchAsync(
        string providerId,
        (XtreamSeriesStub Stub, string? EpisodesJson)[] results,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var saved = 0;
        foreach (var (stub, episodesJson) in results)
        {
            // Failed fetches are not persisted — keep any prior entry so the series retries next sync.
            if (episodesJson is null)
                continue;

            var existing = await db.XtreamSeriesCache.FindAsync(
                [providerId, stub.SeriesId], cancellationToken);

            if (existing is null)
            {
                db.XtreamSeriesCache.Add(new XtreamSeriesCache
                {
                    ProviderId = providerId,
                    SeriesId = stub.SeriesId,
                    LastModifiedEpoch = stub.LastModifiedEpoch,
                    EpisodesJson = episodesJson,
                });
            }
            else
            {
                existing.LastModifiedEpoch = stub.LastModifiedEpoch;
                existing.EpisodesJson = episodesJson;
            }
            saved++;
        }

        if (saved > 0)
            await db.SaveChangesAsync(CancellationToken.None);
        return saved;
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
