using M3Undle.Core.IO;
using M3Undle.Core.M3u;

namespace M3Undle.Core.Filtering;

public static class PlaylistGroupFilter
{
    private const string UngroupedLabel = "(no group)";

    public static PlaylistGroupFilterResult Apply(
        IReadOnlyList<M3uEntry> entries,
        GroupSelectionFile.GroupSelection? groupSelection)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (groupSelection is null)
        {
            var selected = new List<M3uEntry>(entries);
            var counts = BuildGroupCounts(selected);
            return new PlaylistGroupFilterResult(selected, counts, 0, 0);
        }

        var selectedEntries = new List<M3uEntry>();
        var keepSet = groupSelection.Keep;
        var allSet = groupSelection.All;
        var droppedMissingGroup = 0;
        var droppedExcluded = 0;

        foreach (var entry in entries)
        {
            var group = entry.Group;

            if (string.IsNullOrWhiteSpace(group))
            {
                droppedMissingGroup++;
                continue;
            }

            if (allSet.Contains(group) && !keepSet.Contains(group))
            {
                droppedExcluded++;
                continue;
            }

            selectedEntries.Add(entry);
        }

        var groupCounts = BuildGroupCounts(selectedEntries);
        return new PlaylistGroupFilterResult(selectedEntries, groupCounts, droppedMissingGroup, droppedExcluded);
    }

    private static Dictionary<string, int> BuildGroupCounts(IEnumerable<M3uEntry> entries)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = string.IsNullOrWhiteSpace(entry.Group) ? UngroupedLabel : entry.Group!;
            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }

        return counts;
    }
}

public sealed record PlaylistGroupFilterResult(
    IReadOnlyList<M3uEntry> Selected,
    IReadOnlyDictionary<string, int> KeptGroups,
    int DroppedWithoutGroup,
    int DroppedExcluded);
