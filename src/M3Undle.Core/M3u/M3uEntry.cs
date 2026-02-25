using System.Text.RegularExpressions;

namespace M3Undle.Core.M3u;

public sealed class M3uEntry
{
    private static readonly Regex GroupTitleRegex = new("group-title=\"(?<group>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TvgGroupRegex = new("tvg-group=\"(?<group>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> MetadataLines { get; }
    public string? Url { get; }
    public string? Group { get; }
    public string Title { get; }

    public M3uEntry(IReadOnlyList<string> metadataLines, string? url)
    {
        MetadataLines = metadataLines;
        Url = url;
        Group = ExtractGroup(metadataLines);
        Title = ExtractTitle(metadataLines);
    }

    private static string? ExtractGroup(IReadOnlyList<string> metadataLines)
    {
        if (metadataLines.Count == 0)
        {
            return null;
        }

        var first = metadataLines[0];
        var match = GroupTitleRegex.Match(first);
        if (match.Success)
        {
            return match.Groups["group"].Value.Trim();
        }

        match = TvgGroupRegex.Match(first);
        return match.Success ? match.Groups["group"].Value.Trim() : null;
    }

    private static string ExtractTitle(IReadOnlyList<string> metadataLines)
    {
        if (metadataLines.Count == 0)
        {
            return string.Empty;
        }

        var line = metadataLines[0];
        var separatorIndex = line.IndexOf(',', StringComparison.Ordinal);
        if (separatorIndex >= 0 && separatorIndex + 1 < line.Length)
        {
            return line[(separatorIndex + 1)..].Trim();
        }

        return string.Empty;
    }
}

