using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace M3Undle.Core.Epg;

/// <summary>
/// Stateless streaming XMLTV parser.
/// Uses XmlReader to stream the top-level document and XDocument to parse
/// individual &lt;channel&gt; and &lt;programme&gt; elements. This avoids
/// loading the entire file into memory while keeping element parsing simple.
/// Never throws on malformed input — returns whatever was parseable.
/// </summary>
public sealed class XmltvParser
{
    /// <summary>
    /// Parse <paramref name="xml"/> and return a normalised catalogue.
    /// Programmes are sorted by StartUtc per channel.
    /// </summary>
    public EpgCatalogue Parse(string sourceId, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return EpgCatalogue.Empty(sourceId);

        var channels = new List<EpgChannelRecord>();
        var programmes = new Dictionary<string, List<EpgProgrammeRecord>>(StringComparer.Ordinal);

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                });

            // ReadOuterXml() advances the reader past the element it captures,
            // so we must NOT call reader.Read() again after it — otherwise we skip
            // every other sibling. Use a manual advance loop instead.
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "channel")
                {
                    // ReadOuterXml reads the whole element AND advances to the next sibling
                    var elementXml = reader.ReadOuterXml();
                    var ch = ParseChannel(elementXml, sourceId);
                    if (ch is not null)
                        channels.Add(ch);
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
                {
                    var elementXml = reader.ReadOuterXml();
                    var prog = ParseProgramme(elementXml, sourceId);
                    if (prog is not null)
                    {
                        if (!programmes.TryGetValue(prog.XmltvChannelId, out var list))
                        {
                            list = [];
                            programmes[prog.XmltvChannelId] = list;
                        }
                        list.Add(prog);
                    }
                }
                else
                {
                    reader.Read();
                }
            }
        }
        catch
        {
            // Return whatever was parsed before the error
        }

        // Sort programmes by start time per channel
        var sorted = programmes.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<EpgProgrammeRecord>)kv.Value
                .OrderBy(p => p.StartUtc)
                .ToList(),
            StringComparer.Ordinal);

        return new EpgCatalogue(sourceId, channels, sorted);
    }

    // -------------------------------------------------------------------------
    // Element parsers (use LINQ to XML on the isolated element string)
    // -------------------------------------------------------------------------

    private static EpgChannelRecord? ParseChannel(string elementXml, string sourceId)
    {
        try
        {
            var el = XElement.Parse(elementXml);
            var id = el.Attribute("id")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var displayName = el.Elements("display-name").FirstOrDefault()?.Value?.Trim()
                              ?? id;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = id;

            var iconUrl = el.Elements("icon").FirstOrDefault()?.Attribute("src")?.Value?.Trim();

            return new EpgChannelRecord(sourceId, id, displayName, iconUrl);
        }
        catch
        {
            return null;
        }
    }

    private static EpgProgrammeRecord? ParseProgramme(string elementXml, string sourceId)
    {
        try
        {
            var el = XElement.Parse(elementXml);

            var startRaw = el.Attribute("start")?.Value;
            var stopRaw = el.Attribute("stop")?.Value;
            var channelId = el.Attribute("channel")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(startRaw) ||
                string.IsNullOrWhiteSpace(stopRaw) ||
                string.IsNullOrWhiteSpace(channelId))
                return null;

            if (!TryParseXmltvTimestamp(startRaw, out var start) ||
                !TryParseXmltvTimestamp(stopRaw, out var stop))
                return null;

            var title = el.Element("title")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var subTitle = el.Element("sub-title")?.Value?.Trim();
            var description = el.Element("desc")?.Value?.Trim();
            var iconUrl = el.Element("icon")?.Attribute("src")?.Value?.Trim();

            var categories = el.Elements("category")
                .Select(c => c.Value.Trim())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();

            var episodeNums = el.Elements("episode-num")
                .Select(e => e.Value.Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();

            return new EpgProgrammeRecord(
                sourceId, channelId!, start, stop,
                title, subTitle, description,
                categories, episodeNums, iconUrl);
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Timestamp parsing
    // -------------------------------------------------------------------------

    // XMLTV timestamp format: YYYYMMDDHHmmss [+/-HHMM]
    internal static bool TryParseXmltvTimestamp(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        var spaceIdx = value.IndexOf(' ');
        var datePart = spaceIdx > 0 ? value[..spaceIdx] : value;
        var offsetPart = spaceIdx > 0 ? value[(spaceIdx + 1)..].Trim() : "+0000";

        if (datePart.Length < 14)
            return false;

        if (!DateTime.TryParseExact(datePart[..14], "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;

        if (!TryParseOffset(offsetPart, out var offset))
            offset = TimeSpan.Zero;

        result = new DateTimeOffset(dt, offset).ToUniversalTime();
        return true;
    }

    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 5)
            return false;

        var sign = value[0] == '-' ? -1 : 1;
        var digits = value.TrimStart('+', '-');
        if (digits.Length < 4)
            return false;

        if (!int.TryParse(digits[..2], out var hours) ||
            !int.TryParse(digits[2..4], out var minutes))
            return false;

        offset = TimeSpan.FromMinutes(sign * (hours * 60 + minutes));
        return true;
    }
}
