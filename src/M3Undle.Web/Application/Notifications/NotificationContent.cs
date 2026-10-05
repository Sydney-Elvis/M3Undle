using System.Text;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Notifications;

public sealed record NotificationText(string Title, string Body, string Severity);

/// <summary>
/// Plain-text, transport-neutral wording. Only counts, durations, names the administrator chose, and failure classes
/// appear here — never upstream error text, URLs, credentials, addresses or user agents.
/// </summary>
public static class NotificationContent
{
    public static NotificationText Opening(NotificationIncident incident)
    {
        var label = Clean(incident.SubjectLabel, "a source");
        var since = incident.FirstUnhealthyUtc.ToString("u");
        return incident.NotificationKey switch
        {
            NotificationKeys.EpgFetchFailed => new(
                Clean($"Guide source '{label}' is failing to update"),
                $"Guide source '{label}' has not updated successfully since {since}. Last failure type: {Clean(incident.SafeDetail, "unknown")}. " +
                "M3Undle continues to serve cached guide data.",
                incident.Severity),
            NotificationKeys.EpgRefreshOverdue => new(
                Clean($"Guide source '{label}' check is overdue"),
                $"Guide source '{label}' was expected to be checked by {since}, but no check has completed since.",
                incident.Severity),
            NotificationKeys.EpgCoverageInsufficient => new(
                Clean($"Guide coverage is low for '{label}'"),
                $"{Clean(incident.SafeDetail, "Too few channels have guide data for the coming hours.")} Condition began {since}.",
                incident.Severity),
            NotificationKeys.ProviderFetchFailed => new(
                Clean($"Provider '{label}' playlist refresh is failing"),
                $"Provider '{label}' has not refreshed successfully since {since}. Last failure type: {Clean(incident.SafeDetail, "unknown")}. " +
                "M3Undle keeps serving the last good lineup.",
                incident.Severity),
            NotificationKeys.DownstreamRefreshFailed => new(
                Clean($"Downstream integration '{label}' is failing"),
                $"The last commands sent to the integration '{label}' have failed since {since}. Last failure type: {Clean(incident.SafeDetail, "unknown")}.",
                incident.Severity),
            NotificationKeys.StreamUnstable => new(
                Clean($"Stream unstable: {label}"),
                $"{Clean(incident.SafeDetail, "Repeated upstream failures were recorded.")} The channel has been unhealthy since {since}.",
                incident.Severity),
            _ => new(Clean($"{Definition(incident)} — {label}"), $"{Definition(incident)} for '{label}' since {since}. {Clean(incident.SafeDetail, string.Empty)}".Trim(), incident.Severity),
        };
    }

    public static NotificationText Reminder(NotificationIncident incident, DateTime nowUtc)
    {
        var opening = Opening(incident);
        return new(
            "Reminder: " + opening.Title,
            $"Still unresolved after {Duration(nowUtc - incident.FirstUnhealthyUtc)}. {opening.Body}",
            incident.Severity);
    }

    public static NotificationText Recovery(NotificationIncident incident, DateTime resolvedUtc)
    {
        var label = Clean(incident.SubjectLabel, "a source");
        var lasted = Duration(resolvedUtc - incident.FirstUnhealthyUtc);
        var what = incident.NotificationKey switch
        {
            NotificationKeys.EpgFetchFailed => $"Guide source '{label}' is updating again.",
            NotificationKeys.EpgRefreshOverdue => $"Guide source '{label}' has been checked again.",
            NotificationKeys.EpgCoverageInsufficient => $"Guide coverage for '{label}' has recovered.",
            NotificationKeys.ProviderFetchFailed => $"Provider '{label}' is refreshing again.",
            NotificationKeys.DownstreamRefreshFailed => $"Downstream integration '{label}' is working again.",
            NotificationKeys.StreamUnstable => $"The stream for {label} has been healthy for a sustained period.",
            _ => $"{Definition(incident)} has recovered for '{label}'.",
        };
        return new("Recovered: " + what.TrimEnd('.'), $"{what} It was affected for {lasted}; resolved {resolvedUtc:u}.", "Info");
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalMinutes < 1)
            return "under a minute";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public static string Clean(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsControl(c) ? ' ' : c);
        var text = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static string Definition(NotificationIncident incident) =>
        NotificationCatalog.Find(incident.NotificationKey)?.Label ?? incident.NotificationKey;
}
