using M3Undle.Web.Application;

namespace M3Undle.Web.Tests.Stubs;

public sealed class NullSeriesExpansionQueue : IXtreamSeriesExpansionQueue
{
    public bool TryEnqueue(XtreamSeriesExpansionJob job) => true;

    public Task<IReadOnlyList<XtreamSeriesExpanded>> TryExpandInlineAsync(
        XtreamSeriesExpansionJob job, TimeSpan budget, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<XtreamSeriesExpanded>>([]);

    public IReadOnlyList<XtreamSeriesExpansionStatus> ActiveJobs => [];
    public int WaitingJobs => 0;
}

// Records inline-expansion requests without performing any fetches — the job is
// captured exactly as the background queue would have received it.
public sealed class RecordingSeriesExpansionQueue : IXtreamSeriesExpansionQueue
{
    public List<XtreamSeriesExpansionJob> Jobs { get; } = [];

    // Results returned from TryExpandInlineAsync, keyed by series id.
    public Dictionary<int, XtreamSeriesExpanded> InlineResults { get; } = [];

    public bool TryEnqueue(XtreamSeriesExpansionJob job)
    {
        Jobs.Add(job);
        return true;
    }

    public Task<IReadOnlyList<XtreamSeriesExpanded>> TryExpandInlineAsync(
        XtreamSeriesExpansionJob job, TimeSpan budget, CancellationToken cancellationToken)
    {
        Jobs.Add(job);
        var hits = job.Series
            .Where(s => InlineResults.ContainsKey(s.SeriesId))
            .Select(s => InlineResults[s.SeriesId])
            .ToList();
        return Task.FromResult<IReadOnlyList<XtreamSeriesExpanded>>(hits);
    }

    public IReadOnlyList<XtreamSeriesExpansionStatus> ActiveJobs => [];
    public int WaitingJobs => 0;
}

public sealed class RecordingRefreshTrigger : IRefreshTrigger
{
    public int RefreshCount { get; private set; }
    public bool IsRefreshing => false;
    public DateTime? RefreshStartedAt => null;
    public string? CurrentActivity => null;
    public bool TriggerRefresh()
    {
        RefreshCount++;
        return true;
    }
    public bool TriggerBuildOnly() => true;
    public void CancelRefresh() { }
}
