using System.Text;
using System.Xml;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Epg;

/// <summary>
/// Compiles a merged XMLTV guide from multiple EPG sources using priority + coverage selection.
/// For each output channel the highest-priority source with sufficient programme coverage is chosen.
/// Produces a deterministic XMLTV string and a <see cref="EpgMergeReport"/> alongside it.
/// </summary>
public sealed class EpgCompiler(ILogger<EpgCompiler> logger)
{
    /// <summary>Hours ahead to check for "sufficient coverage" when selecting a source.</summary>
    private const int CoverageWindowHours = 24;

    /// <summary>
    /// Compile a merged guide.
    /// </summary>
    /// <param name="outputChannels">
    ///   Channels from the channel index — provides TvgId, DisplayName, LogoUrl.
    /// </param>
    /// <param name="sources">EPG sources ordered by priority ascending (1 = highest).</param>
    /// <param name="catalogues">
    ///   Per-source parsed catalogues; key is EpgSourceId.
    /// </param>
    /// <param name="mappings">
    ///   All channel mappings for the profile (ProviderChannelId → sorted by source priority).
    /// </param>
    public (string Xml, EpgMergeReport Report) Compile(
        IReadOnlyList<OutputChannel> outputChannels,
        IReadOnlyList<EpgSource> sources,
        IReadOnlyDictionary<string, EpgCatalogue> catalogues,
        ILookup<string, EpgChannelMapping> mappings)
    {
        var now = DateTimeOffset.UtcNow;
        var coverageWindow = now.AddHours(CoverageWindowHours);

        var report = new EpgMergeReport
        {
            GeneratedUtc = now.UtcDateTime,
            TotalChannels = outputChannels.Count,
        };

        // Priority order for source lookup
        var sourceById = sources.ToDictionary(s => s.EpgSourceId, s => s);
        var priorityIndex = sources
            .OrderBy(s => s.Priority)
            .Select((s, i) => (s.EpgSourceId, i))
            .ToDictionary(x => x.EpgSourceId, x => x.i);

        var sb = new StringBuilder();
        using var sw = new StringWriter(sb);
        using var writer = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            Indent = false,
            OmitXmlDeclaration = false,
        });

        writer.WriteStartDocument();
        writer.WriteStartElement("tv");
        writer.WriteAttributeString("generator-info-name", "M3Undle");

        int totalProgrammes = 0;
        int mappedCount = 0;

        // Track conflicts for the report (cap at 20 to keep the report small)
        var conflicts = new List<ProgrammeConflict>();

        foreach (var ch in outputChannels.OrderBy(c => c.TvgChno ?? int.MaxValue))
        {
            if (string.IsNullOrWhiteSpace(ch.TvgId))
                continue;

            // Find the best source for this channel
            var channelMappingsForChannel = mappings[ch.ProviderChannelId]
                .OrderBy(m => priorityIndex.TryGetValue(m.EpgSourceId, out var idx) ? idx : int.MaxValue)
                .ToList();

            EpgChannelMapping? selectedMapping = null;
            EpgCatalogue? selectedCatalogue = null;
            IReadOnlyList<EpgProgrammeRecord> selectedProgrammes = [];
            string selectionReason = "no_mapping";

            foreach (var mapping in channelMappingsForChannel)
            {
                if (!catalogues.TryGetValue(mapping.EpgSourceId, out var catalogue))
                    continue;
                if (!catalogue.ProgrammesByChannel.TryGetValue(mapping.XmltvChannelId, out var progs))
                    continue;

                // Check coverage in the window
                var hasCoverage = progs.Any(p => p.StartUtc < coverageWindow && p.StopUtc > now);
                if (hasCoverage)
                {
                    selectedMapping = mapping;
                    selectedCatalogue = catalogue;
                    selectedProgrammes = progs;
                    selectionReason = "coverage";
                    break;
                }
            }

            // Fallback: use first source that has any programmes
            if (selectedMapping is null)
            {
                foreach (var mapping in channelMappingsForChannel)
                {
                    if (!catalogues.TryGetValue(mapping.EpgSourceId, out var catalogue))
                        continue;
                    if (!catalogue.ProgrammesByChannel.TryGetValue(mapping.XmltvChannelId, out var progs) ||
                        progs.Count == 0)
                        continue;

                    selectedMapping = mapping;
                    selectedCatalogue = catalogue;
                    selectedProgrammes = progs;
                    selectionReason = "fallback";
                    break;
                }
            }

            // Write <channel>
            writer.WriteStartElement("channel");
            writer.WriteAttributeString("id", ch.TvgId);
            writer.WriteStartElement("display-name");
            writer.WriteString(ch.DisplayName);
            writer.WriteEndElement();
            if (ch.TvgChno.HasValue)
                writer.WriteElementString("display-number", ch.TvgChno.Value.ToString());
            if (!string.IsNullOrWhiteSpace(ch.LogoUrl))
            {
                writer.WriteStartElement("icon");
                writer.WriteAttributeString("src", ch.LogoUrl);
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); // </channel>

            // Write <programme> entries
            if (selectedProgrammes.Count > 0)
            {
                mappedCount++;
                var dedupedProgrammes = DeduplicateProgrammes(selectedProgrammes);
                var (resolved, channelConflicts) = ResolveConflicts(
                    dedupedProgrammes, selectedMapping!.EpgSourceId, ch.TvgId, conflicts.Count);

                conflicts.AddRange(channelConflicts);

                foreach (var prog in resolved)
                {
                    WriteProgramme(writer, prog, ch.TvgId);
                    totalProgrammes++;
                }
            }

            var sourceName = selectedMapping is not null && sourceById.TryGetValue(selectedMapping.EpgSourceId, out var src)
                ? src.Name : null;

            report.ChannelResults.Add(new ChannelMergeResult
            {
                TvgId = ch.TvgId,
                DisplayName = ch.DisplayName,
                SelectedSourceId = selectedMapping?.EpgSourceId,
                SelectedSourceName = sourceName,
                ProgrammeCount = selectedProgrammes.Count,
                SelectionReason = selectionReason,
            });
        }

        writer.WriteFullEndElement(); // </tv> — prevents self-closing <tv/> when empty
        writer.WriteEndDocument();
        writer.Flush();

        report.MappedChannels = mappedCount;
        report.TotalProgrammes = totalProgrammes;
        report.Conflicts = conflicts;

        logger.LogInformation(
            "EPG compiled — {Total} channels, {Mapped} with guide data, {Programmes} programmes, {Conflicts} conflicts.",
            outputChannels.Count, mappedCount, totalProgrammes, conflicts.Count);

        // XmlWriter writes the XML declaration with UTF-16 when using StringWriter;
        // replace with UTF-8 declaration to be standards-compliant.
        var xml = sb.ToString().Replace(
            "<?xml version=\"1.0\" encoding=\"utf-16\"?>",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            StringComparison.Ordinal);

        return (xml, report);
    }

    // -------------------------------------------------------------------------
    // Dedup and conflict resolution
    // -------------------------------------------------------------------------

    private static List<EpgProgrammeRecord> DeduplicateProgrammes(
        IReadOnlyList<EpgProgrammeRecord> programmes)
    {
        var seen = new HashSet<(DateTimeOffset, DateTimeOffset, string)>();
        var result = new List<EpgProgrammeRecord>(programmes.Count);
        foreach (var p in programmes)
        {
            var key = (p.StartUtc, p.StopUtc, p.Title.ToLowerInvariant());
            if (seen.Add(key))
                result.Add(p);
        }
        return result;
    }

    private static (List<EpgProgrammeRecord> Resolved, List<ProgrammeConflict> Conflicts) ResolveConflicts(
        List<EpgProgrammeRecord> programmes,
        string winnerSourceId,
        string outputChannelId,
        int existingConflictCount)
    {
        // Programmes are already sorted by start time.
        // Drop any programme whose start overlaps with the previous stop.
        var resolved = new List<EpgProgrammeRecord>(programmes.Count);
        var conflicts = new List<ProgrammeConflict>();
        DateTimeOffset? prevStop = null;

        foreach (var prog in programmes)
        {
            if (prevStop.HasValue && prog.StartUtc < prevStop.Value)
            {
                // Overlap — the prior entry wins (it came from higher priority via dedup).
                // Log a conflict sample (cap report at 20).
                if (existingConflictCount + conflicts.Count < 20)
                {
                    conflicts.Add(new ProgrammeConflict
                    {
                        XmltvChannelId = outputChannelId,
                        StartUtc = prog.StartUtc,
                        WinnerSourceId = winnerSourceId,
                        LoserSourceId = prog.SourceId,
                        WinnerTitle = resolved[^1].Title,
                        LoserTitle = prog.Title,
                    });
                }
                continue;
            }
            resolved.Add(prog);
            prevStop = prog.StopUtc;
        }

        return (resolved, conflicts);
    }

    // -------------------------------------------------------------------------
    // XML writing
    // -------------------------------------------------------------------------

    private static void WriteProgramme(XmlWriter writer, EpgProgrammeRecord prog, string channelId)
    {
        writer.WriteStartElement("programme");
        writer.WriteAttributeString("start", FormatXmltvTimestamp(prog.StartUtc));
        writer.WriteAttributeString("stop", FormatXmltvTimestamp(prog.StopUtc));
        writer.WriteAttributeString("channel", channelId);

        writer.WriteElementString("title", prog.Title);

        if (!string.IsNullOrWhiteSpace(prog.SubTitle))
            writer.WriteElementString("sub-title", prog.SubTitle);

        if (!string.IsNullOrWhiteSpace(prog.Description))
            writer.WriteElementString("desc", prog.Description);

        foreach (var cat in prog.Categories)
            writer.WriteElementString("category", cat);

        foreach (var ep in prog.EpisodeNums)
            writer.WriteElementString("episode-num", ep);

        if (!string.IsNullOrWhiteSpace(prog.IconUrl))
        {
            writer.WriteStartElement("icon");
            writer.WriteAttributeString("src", prog.IconUrl);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // </programme>
    }

    private static string FormatXmltvTimestamp(DateTimeOffset value)
    {
        // XMLTV format: YYYYMMDDHHmmss +0000
        var utc = value.UtcDateTime;
        return utc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + " +0000";
    }
}

/// <summary>A lightweight channel descriptor used as input to the compiler.</summary>
public sealed record OutputChannel(
    string ProviderChannelId,
    string TvgId,
    string DisplayName,
    string? LogoUrl,
    int? TvgChno = null);
