namespace M3Undle.Core.Epg;

public static class EpgCoverageAnalyzer
{
    public static bool HasCoverage(
        IReadOnlyList<EpgProgrammeRecord> programmes,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        ArgumentNullException.ThrowIfNull(programmes);

        return programmes.Any(p => OverlapsWindow(p, windowStartUtc, windowEndUtc));
    }

    public static bool HasChannelCoverage(
        EpgCatalogue catalogue,
        string xmltvChannelId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        return catalogue.ProgrammesByChannel.TryGetValue(xmltvChannelId, out var programmes)
            && HasCoverage(programmes, windowStartUtc, windowEndUtc);
    }

    public static bool OverlapsWindow(
        EpgProgrammeRecord programme,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        return programme.StartUtc < windowEndUtc && programme.StopUtc > windowStartUtc;
    }
}
