namespace M3Undle.Core.M3u;

public sealed class PlaylistDocument
{
    public IReadOnlyList<string> Preamble { get; }
    public IReadOnlyList<M3uEntry> Entries { get; }

    public PlaylistDocument(IReadOnlyList<string> preamble, IReadOnlyList<M3uEntry> entries)
    {
        Preamble = preamble;
        Entries = entries;
    }

    public IEnumerable<string> EnumerateLines(IEnumerable<M3uEntry> selectedEntries)
    {
        var wroteHeader = false;
        foreach (var line in Preamble)
        {
            if (!wroteHeader && line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                wroteHeader = true;
            }
            yield return line;
        }

        if (!wroteHeader)
        {
            yield return "#EXTM3U";
        }

        foreach (var entry in selectedEntries)
        {
            foreach (var metadata in entry.MetadataLines)
            {
                yield return metadata;
            }

            if (!string.IsNullOrEmpty(entry.Url))
            {
                yield return entry.Url;
            }
        }
    }
}

