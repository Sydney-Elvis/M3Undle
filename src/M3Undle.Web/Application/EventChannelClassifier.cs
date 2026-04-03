using System.Text.RegularExpressions;

namespace M3Undle.Web.Application;

internal readonly record struct EventChannelClassification(
    bool IsEvent,
    bool IsPlaceholder,
    string? EventSlotKey,
    string? EventContentKey);

internal static partial class EventChannelClassifier
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^(?<prefix>.+?)\s*\|\s*Event\s*(?<num>\d{1,3})\s*:\s*(?<tail>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PipeEventSlotRegex();

    [GeneratedRegex(@"^PPV\s*EVENT\s*(?<num>\d{1,3})\s*:\s*(?<tail>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PpvEventSlotRegex();

    [GeneratedRegex(@"^PPV\s*(?<num>\d{1,3})\s*\|\s*(?<tail>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PpvPipeSlotRegex();

    [GeneratedRegex(@"^(?<num>\d{1,3})\.\s*(?<tail>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberDotSlotRegex();

    [GeneratedRegex(@"^\d{1,2}/\d{1,2}\s*-\s*[^|]+\|\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingDatePipeRegex();

    public static EventChannelClassification Classify(string displayName, string? groupTitle)
    {
        var normalizedDisplay = Normalize(displayName);
        var normalizedGroup = Normalize(groupTitle);

        var hasEventLikeGroup = IsEventLikeGroup(normalizedGroup);
        var hasEventLikeName = IsEventLikeName(normalizedDisplay);

        var (slotKey, contentCandidate) = ExtractSlotAndContent(normalizedDisplay, normalizedGroup);
        if (contentCandidate is null)
            contentCandidate = normalizedDisplay;

        var isPlaceholder = IsPlaceholder(normalizedDisplay, contentCandidate);
        var isEvent = hasEventLikeGroup || hasEventLikeName || slotKey is not null;
        if (!isEvent && !isPlaceholder)
            return new EventChannelClassification(false, false, null, null);

        if (isPlaceholder)
            return new EventChannelClassification(true, true, slotKey, null);

        var contentKey = BuildContentKey(contentCandidate, normalizedGroup);
        return new EventChannelClassification(true, false, slotKey, contentKey);
    }

    private static (string? SlotKey, string? Content) ExtractSlotAndContent(string displayName, string groupTitle)
    {
        var m = PipeEventSlotRegex().Match(displayName);
        if (m.Success)
        {
            var prefix = Normalize(m.Groups["prefix"].Value);
            var num = m.Groups["num"].Value;
            var tail = Normalize(m.Groups["tail"].Value);
            return ($"{prefix}|event:{num}", tail);
        }

        m = PpvEventSlotRegex().Match(displayName);
        if (m.Success)
        {
            var num = m.Groups["num"].Value;
            var tail = Normalize(m.Groups["tail"].Value);
            var scope = groupTitle.Length > 0 ? groupTitle : "ppv";
            return ($"{scope}|ppv_event:{num}", tail);
        }

        m = PpvPipeSlotRegex().Match(displayName);
        if (m.Success)
        {
            var num = m.Groups["num"].Value;
            var tail = Normalize(m.Groups["tail"].Value);
            var scope = groupTitle.Length > 0 ? groupTitle : "ppv";
            return ($"{scope}|ppv_pipe:{num}", tail);
        }

        m = NumberDotSlotRegex().Match(displayName);
        if (m.Success)
        {
            var num = m.Groups["num"].Value;
            var tail = Normalize(m.Groups["tail"].Value);
            if (groupTitle.Length > 0)
                return ($"{groupTitle}|slot:{num}", tail);
        }

        return (null, null);
    }

    private static string? BuildContentKey(string contentCandidate, string groupTitle)
    {
        var content = Normalize(contentCandidate);
        if (content.Length == 0)
            return null;

        content = LeadingDatePipeRegex().Replace(content, string.Empty);
        content = Normalize(content);
        if (content.Length == 0)
            return null;

        if (content.Contains("start:", StringComparison.OrdinalIgnoreCase))
            content = content[..content.IndexOf("start:", StringComparison.OrdinalIgnoreCase)];

        content = Normalize(content);
        if (content.Length == 0)
            return null;

        return groupTitle.Length == 0
            ? content.ToUpperInvariant()
            : $"{groupTitle.ToUpperInvariant()}::{content.ToUpperInvariant()}";
    }

    private static bool IsPlaceholder(string displayName, string contentCandidate)
    {
        if (displayName.Length == 0)
            return true;

        if (displayName.EndsWith(":", StringComparison.Ordinal)
            || displayName.EndsWith("|", StringComparison.Ordinal))
            return true;

        if (Regex.IsMatch(displayName, @"^\d{1,3}\.\s*$", RegexOptions.IgnoreCase))
            return true;

        var upper = displayName.ToUpperInvariant();
        if (Regex.IsMatch(upper, @"^PPV\s*EVENT\s*\d{1,3}\s*:?\s*$", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(upper, @"^EVENT\s*\d{1,3}\s*:?\s*$", RegexOptions.IgnoreCase))
            return true;

        var content = Normalize(contentCandidate).ToUpperInvariant();
        if (content.Length == 0)
            return true;

        return content is "OFFLINE" or "TBD" or "N/A";
    }

    private static bool IsEventLikeGroup(string groupTitle)
    {
        if (groupTitle.Length == 0)
            return false;

        var upper = groupTitle.ToUpperInvariant();
        return upper.Contains("EVENT", StringComparison.Ordinal)
               || upper.Contains("PPV", StringComparison.Ordinal)
               || upper.Contains("LIVE ONLY", StringComparison.Ordinal)
               || upper.Contains("LIVE EVENTS", StringComparison.Ordinal)
               || upper.Contains("SPORTS+", StringComparison.Ordinal)
               || upper.Contains("FIGHT", StringComparison.Ordinal);
    }

    private static bool IsEventLikeName(string displayName)
    {
        if (displayName.Length == 0)
            return false;

        var upper = displayName.ToUpperInvariant();
        return upper.Contains(" VS ", StringComparison.Ordinal)
               || upper.Contains(" PPV ", StringComparison.Ordinal)
               || upper.StartsWith("PPV ", StringComparison.Ordinal)
               || upper.Contains("EVENT ", StringComparison.Ordinal)
               || upper.Contains(" START:", StringComparison.Ordinal)
               || upper.Contains(" STOP:", StringComparison.Ordinal)
               || Regex.IsMatch(displayName, @"^\d{1,2}/\d{1,2}\s*-\s*");
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return MultiSpaceRegex().Replace(value.Trim(), " ");
    }
}
