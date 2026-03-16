namespace M3Undle.Web.Application.Epg;

/// <summary>Channel metadata discovered within an XMLTV source.</summary>
public sealed record EpgChannelRecord(
    string SourceId,
    string XmltvChannelId,
    string DisplayName,
    string? IconUrl);

/// <summary>A single programme entry parsed from XMLTV, normalised to UTC.</summary>
public sealed record EpgProgrammeRecord(
    string SourceId,
    string XmltvChannelId,
    DateTimeOffset StartUtc,
    DateTimeOffset StopUtc,
    string Title,
    string? SubTitle,
    string? Description,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> EpisodeNums,
    string? IconUrl);

/// <summary>
/// The result of parsing one XMLTV source — channels and per-channel programmes sorted by start time.
/// An empty catalogue (zero channels, empty programmes dictionary) is used when a source returns
/// no usable data rather than throwing.
/// </summary>
public sealed record EpgCatalogue(
    string SourceId,
    IReadOnlyList<EpgChannelRecord> Channels,
    IReadOnlyDictionary<string, IReadOnlyList<EpgProgrammeRecord>> ProgrammesByChannel)
{
    public static EpgCatalogue Empty(string sourceId) =>
        new(sourceId, [], new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>());
}
