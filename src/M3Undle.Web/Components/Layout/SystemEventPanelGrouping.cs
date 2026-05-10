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
        List<SystemEventPanelItem> items = [];
        PendingGroup? pending = null;

        foreach (var evt in events)
        {
            var relative = relativeTime(evt.OccurredAt, nowUtc);
            var key = new GroupKey(
                evt.EventType,
                evt.Severity,
                evt.Title,
                evt.Detail,
                evt.ProviderId,
                evt.IntegrationId,
                relative);

            if (pending is not null && pending.Key == key)
            {
                pending.EventIds.Add(evt.SystemEventId);
                pending.TotalOccurrences += Math.Max(1, evt.OccurrenceCount);
                continue;
            }

            FlushPending();
            pending = new PendingGroup(
                key,
                evt,
                [evt.SystemEventId],
                Math.Max(1, evt.OccurrenceCount),
                relative);
        }

        FlushPending();
        return items;

        void FlushPending()
        {
            if (pending is null)
                return;

            items.Add(new SystemEventPanelItem(
                pending.Event,
                pending.EventIds,
                pending.TotalOccurrences,
                pending.RelativeTime));
        }
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
        string? IntegrationId,
        string RelativeTime);
}
