using M3Undle.Core.M3u;

namespace M3Undle.Core.Groups;

public static class PlaylistGroupDiscovery
{
    public static SortedSet<string> Discover(IEnumerable<M3uEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var groups = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Group))
            {
                groups.Add(entry.Group);
            }
        }

        return groups;
    }
}
