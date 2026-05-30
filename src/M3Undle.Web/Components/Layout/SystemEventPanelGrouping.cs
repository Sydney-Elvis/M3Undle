using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Components.Layout;

internal sealed record SystemEventPanelItem(
    SystemEvent Event,
    IReadOnlyList<string> EventIds,
    int TotalOccurrences,
    string RelativeTime);

internal static class SystemEventPanelGrouping
{
    public static IReadOnlyList<SystemEventPanelItem> Group(
        IEnumerable<SystemEvent> events,
        DateTime nowUtc,
        Func<DateTime, DateTime, string> relativeTime)
    {
        var groups = new Dictionary<GroupKey, PendingGroup>();
        var order = new List<GroupKey>();

        foreach (var evt in events)
        {
            var key = new GroupKey(
                evt.EventType,
                evt.Severity,
                evt.Title,
                evt.Detail,
                evt.ProviderId,
                evt.IntegrationId);

            if (groups.TryGetValue(key, out var existing))
            {
                existing.EventIds.Add(evt.SystemEventId);
                existing.TotalOccurrences += Math.Max(1, evt.OccurrenceCount);
            }
            else
            {
                var group = new PendingGroup(
                    key,
                    evt,
                    [evt.SystemEventId],
                    Math.Max(1, evt.OccurrenceCount),
                    relativeTime(evt.OccurredAt, nowUtc));
                groups[key] = group;
                order.Add(key);
            }
        }

        return order
            .Select(k =>
            {
                var g = groups[k];
                return new SystemEventPanelItem(g.Event, g.EventIds, g.TotalOccurrences, g.RelativeTime);
            })
            .ToList();
    }

    public static string RelativeTime(DateTime utc, DateTime nowUtc)
    {
        var diff = nowUtc - utc;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }

    private sealed class PendingGroup(
        GroupKey key,
        SystemEvent eventItem,
        List<string> eventIds,
        int totalOccurrences,
        string relativeTime)
    {
        public GroupKey Key { get; } = key;
        public SystemEvent Event { get; } = eventItem;
        public List<string> EventIds { get; } = eventIds;
        public int TotalOccurrences { get; set; } = totalOccurrences;
        public string RelativeTime { get; } = relativeTime;
    }

    private sealed record GroupKey(
        string EventType,
        string Severity,
        string Title,
        string? Detail,
        string? ProviderId,
        string? IntegrationId);
}
