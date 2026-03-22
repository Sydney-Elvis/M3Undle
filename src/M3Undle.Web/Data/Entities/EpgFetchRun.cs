namespace M3Undle.Web.Data.Entities;

public sealed class EpgFetchRun
{
    public string EpgFetchRunId { get; set; } = string.Empty;
    public string EpgSourceId { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    /// <summary>running | ok | fail | not_modified</summary>
    public string Status { get; set; } = string.Empty;

    public int? Bytes { get; set; }
    public int? ChannelCount { get; set; }
    public int? ProgrammeCount { get; set; }
    public string? ErrorSummary { get; set; }

    public EpgSource EpgSource { get; set; } = null!;
}
