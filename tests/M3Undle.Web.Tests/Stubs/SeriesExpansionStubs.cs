using M3Undle.Web.Application;

namespace M3Undle.Web.Tests.Stubs;

public sealed class NullSeriesExpansionQueue : IXtreamSeriesExpansionQueue
{
    public bool TryEnqueue(XtreamSeriesExpansionJob job) => true;
    public XtreamSeriesExpansionStatus? CurrentStatus => null;
}

public sealed class RecordingSeriesExpansionQueue : IXtreamSeriesExpansionQueue
{
    public List<XtreamSeriesExpansionJob> Jobs { get; } = [];
    public bool TryEnqueue(XtreamSeriesExpansionJob job)
    {
        Jobs.Add(job);
        return true;
    }
    public XtreamSeriesExpansionStatus? CurrentStatus => null;
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
