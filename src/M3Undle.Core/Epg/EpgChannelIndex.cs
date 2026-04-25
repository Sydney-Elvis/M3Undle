namespace M3Undle.Core.Epg;

/// <summary>
/// Pre-built lookup index over a single EPG source's channel list.
/// Build once per source catalogue, then call FindBestMatch for each candidate channel
/// to avoid rebuilding lookup dictionaries on every call.
/// </summary>
public sealed class EpgChannelIndex
{
    private readonly Dictionary<string, EpgChannelRecord> _byExactId;
    private readonly Dictionary<string, EpgChannelRecord> _byNormName;
    private readonly IReadOnlyList<EpgChannelRecord> _all;

    public EpgChannelIndex(IReadOnlyList<EpgChannelRecord> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        _all = channels;
        _byExactId = channels
            .GroupBy(c => c.XmltvChannelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _byNormName = channels
            .GroupBy(c => EpgChannelMatcher.NormalizeName(c.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public EpgChannelMatch? FindBestMatch(EpgChannelMatchCandidate channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return EpgChannelMatcher.FindBestMatch(channel, _byExactId, _byNormName, _all);
    }
}
