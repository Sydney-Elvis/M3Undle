using System.Text.Json.Serialization;

namespace M3Undle.Web.Application.Epg;

/// <summary>
/// Optional merge diagnostics model for epg_merge_report.json.
/// Records which EPG source was selected per channel and summary statistics.
/// </summary>
public sealed class EpgMergeReport
{
    [JsonPropertyName("generatedUtc")]
    public DateTime GeneratedUtc { get; set; }

    [JsonPropertyName("channelResults")]
    public List<ChannelMergeResult> ChannelResults { get; set; } = [];

    [JsonPropertyName("totalChannels")]
    public int TotalChannels { get; set; }

    [JsonPropertyName("mappedChannels")]
    public int MappedChannels { get; set; }

    [JsonPropertyName("totalProgrammes")]
    public int TotalProgrammes { get; set; }

    [JsonPropertyName("conflicts")]
    public List<ProgrammeConflict> Conflicts { get; set; } = [];
}

public sealed class ChannelMergeResult
{
    [JsonPropertyName("tvgId")]
    public string TvgId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("selectedSourceId")]
    public string? SelectedSourceId { get; set; }

    [JsonPropertyName("selectedSourceName")]
    public string? SelectedSourceName { get; set; }

    [JsonPropertyName("programmeCount")]
    public int ProgrammeCount { get; set; }

    [JsonPropertyName("selectionReason")]
    public string SelectionReason { get; set; } = string.Empty;
}

public sealed class ProgrammeConflict
{
    [JsonPropertyName("xmltvChannelId")]
    public string XmltvChannelId { get; set; } = string.Empty;

    [JsonPropertyName("startUtc")]
    public DateTimeOffset StartUtc { get; set; }

    [JsonPropertyName("winnerSourceId")]
    public string WinnerSourceId { get; set; } = string.Empty;

    [JsonPropertyName("loserSourceId")]
    public string LoserSourceId { get; set; } = string.Empty;

    [JsonPropertyName("winnerTitle")]
    public string WinnerTitle { get; set; } = string.Empty;

    [JsonPropertyName("loserTitle")]
    public string LoserTitle { get; set; } = string.Empty;
}
