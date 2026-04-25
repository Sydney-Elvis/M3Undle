using System.Text.Json;
using System.Text.RegularExpressions;

namespace M3Undle.Core.Events;

public readonly record struct EventChannelClassification(
    bool IsEvent,
    bool IsPlaceholder,
    string? EventSlotKey,
    string? EventContentKey,
    string? EventTitle,
    string? EventSport,
    string? EventLeague,
    string? EventParticipantsJson);

public static partial class EventChannelClassifier
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

    // Extracts participants from "Team A vs Team B" or "Fighter A vs. Fighter B"
    [GeneratedRegex(@"^(?<p1>.+?)\s+vs\.?\s+(?<p2>.+?)(?:\s*[\(\[].+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersusRegex();

    // Provider-specific: delta "GAME N:" or "MATCH N:" prefixes
    [GeneratedRegex(@"^(?:GAME|MATCH|RACE|BOUT|FIGHT)\s*(?<num>\d{1,3})\s*:\s*(?<tail>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex GameMatchSlotRegex();

    // Detect sport keywords in group titles or channel names
    private static readonly (string Keyword, string Sport)[] SportKeywords =
    [
        ("FOOTBALL", "football"), ("NFL", "football"), ("NCAA FOOTBALL", "football"),
        ("SOCCER", "soccer"), ("FUTBOL", "soccer"), ("FOOTBALL", "soccer"),
        ("MLS", "soccer"), ("EPL", "soccer"), ("PREMIER LEAGUE", "soccer"),
        ("LA LIGA", "soccer"), ("BUNDESLIGA", "soccer"), ("SERIE A", "soccer"),
        ("CHAMPIONS LEAGUE", "soccer"), ("WORLD CUP", "soccer"),
        ("BASKETBALL", "basketball"), ("NBA", "basketball"), ("NCAAB", "basketball"),
        ("BASEBALL", "baseball"), ("MLB", "baseball"), ("MiLB", "baseball"),
        ("HOCKEY", "hockey"), ("NHL", "hockey"),
        ("TENNIS", "tennis"), ("ATP", "tennis"), ("WTA", "tennis"), ("WIMBLEDON", "tennis"),
        ("GOLF", "golf"), ("PGA", "golf"),
        ("MMA", "mma"), ("UFC", "mma"), ("BELLATOR", "mma"),
        ("BOXING", "boxing"), ("FIGHT", "boxing"),
        ("WRESTLING", "wrestling"), ("WWE", "wrestling"), ("AEW", "wrestling"), ("PPV", "wrestling"),
        ("RACING", "racing"), ("F1", "racing"), ("FORMULA 1", "racing"), ("FORMULA ONE", "racing"),
        ("INDYCAR", "racing"), ("NASCAR", "racing"), ("MOTOGP", "racing"),
        ("RUGBY", "rugby"),
        ("CRICKET", "cricket"),
    ];

    // Known league/promotion/series keywords
    private static readonly (string Keyword, string League)[] LeagueKeywords =
    [
        ("NFL", "NFL"), ("NBA", "NBA"), ("MLB", "MLB"), ("NHL", "NHL"), ("MLS", "MLS"),
        ("UFC", "UFC"), ("BELLATOR", "Bellator"), ("WWE", "WWE"), ("AEW", "AEW"),
        ("FORMULA 1", "Formula 1"), ("FORMULA ONE", "Formula 1"), ("F1", "Formula 1"),
        ("INDYCAR", "IndyCar"), ("NASCAR", "NASCAR"), ("MOTOGP", "MotoGP"),
        ("PREMIER LEAGUE", "Premier League"), ("EPL", "Premier League"),
        ("LA LIGA", "La Liga"), ("BUNDESLIGA", "Bundesliga"),
        ("SERIE A", "Serie A"), ("CHAMPIONS LEAGUE", "Champions League"),
        ("MiLB", "MiLB"), ("NCAAB", "NCAAB"), ("ATP", "ATP"), ("WTA", "WTA"),
        ("PGA", "PGA"), ("TRILLER", "Triller"),
    ];

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
            return new EventChannelClassification(false, false, null, null, null, null, null, null);

        if (isPlaceholder)
            return new EventChannelClassification(true, true, slotKey, null, null, null, null, null);

        var contentKey = BuildContentKey(contentCandidate, normalizedGroup);

        // Extract enriched metadata from the content candidate and group title
        var sport = DetectSport(normalizedDisplay, normalizedGroup);
        var league = DetectLeague(normalizedDisplay, normalizedGroup);
        var (eventTitle, participantsJson) = ExtractEventTitleAndParticipants(contentCandidate);

        return new EventChannelClassification(
            true, false, slotKey, contentKey,
            eventTitle, sport, league, participantsJson);
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

        // Provider-specific: "Game 3: Team A vs Team B", "Bout 1: Fighter A vs Fighter B"
        m = GameMatchSlotRegex().Match(displayName);
        if (m.Success)
        {
            var num = m.Groups["num"].Value;
            var tail = Normalize(m.Groups["tail"].Value);
            var scope = groupTitle.Length > 0 ? groupTitle : "event";
            return ($"{scope}|game:{num}", tail);
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

    private static (string? EventTitle, string? ParticipantsJson) ExtractEventTitleAndParticipants(string contentCandidate)
    {
        var cleaned = LeadingDatePipeRegex().Replace(contentCandidate, string.Empty).Trim();
        if (cleaned.Length == 0)
            return (null, null);

        // Strip start/stop time suffixes
        if (cleaned.Contains("start:", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[..cleaned.IndexOf("start:", StringComparison.OrdinalIgnoreCase)].Trim();

        cleaned = Normalize(cleaned);
        if (cleaned.Length == 0)
            return (null, null);

        var title = cleaned;

        var m = VersusRegex().Match(cleaned);
        if (m.Success)
        {
            var p1 = Normalize(m.Groups["p1"].Value);
            var p2 = Normalize(m.Groups["p2"].Value);
            if (p1.Length > 0 && p2.Length > 0)
            {
                var participants = JsonSerializer.Serialize(new[] { p1, p2 });
                return (title, participants);
            }
        }

        return (title, null);
    }

    private static string? DetectSport(string displayName, string groupTitle)
    {
        var haystack = $"{groupTitle} {displayName}".ToUpperInvariant();
        foreach (var (keyword, sport) in SportKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return sport;
        }
        return null;
    }

    private static string? DetectLeague(string displayName, string groupTitle)
    {
        var haystack = $"{groupTitle} {displayName}".ToUpperInvariant();
        foreach (var (keyword, league) in LeagueKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return league;
        }
        return null;
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
        if (Regex.IsMatch(upper, @"^(GAME|MATCH|RACE|BOUT|FIGHT)\s*\d{1,3}\s*:?\s*$", RegexOptions.IgnoreCase))
            return true;

        // Provider-specific: onyx "PPV 1 |" with nothing after pipe
        if (Regex.IsMatch(upper, @"^PPV\s*\d{1,3}\s*\|\s*$", RegexOptions.IgnoreCase))
            return true;

        var content = Normalize(contentCandidate).ToUpperInvariant();
        if (content.Length == 0)
            return true;

        return content is "OFFLINE" or "TBD" or "N/A" or "COMING SOON" or "TO BE ANNOUNCED" or "TBA";
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
               || upper.Contains("FIGHT", StringComparison.Ordinal)
               || upper.Contains("GAMES", StringComparison.Ordinal)       // e.g. "USA NBA GAMES"
               || upper.Contains("LIVE |", StringComparison.Ordinal)      // onyx "LIVE | Sky Sports+"
               || upper.StartsWith("LIVE |", StringComparison.Ordinal)
               || upper.Contains("REQUESTED LIVE", StringComparison.Ordinal)
               || upper.Contains("FIGHTPASS", StringComparison.Ordinal)
               || upper.Contains("FLOSPORTS", StringComparison.Ordinal)
               || upper.Contains("FANATIZ", StringComparison.Ordinal)
               || upper.Contains("TRILLER", StringComparison.Ordinal);
    }

    private static bool IsEventLikeName(string displayName)
    {
        if (displayName.Length == 0)
            return false;

        var upper = displayName.ToUpperInvariant();
        return upper.Contains(" VS ", StringComparison.Ordinal)
               || upper.Contains(" VS. ", StringComparison.Ordinal)
               || upper.Contains(" PPV ", StringComparison.Ordinal)
               || upper.StartsWith("PPV ", StringComparison.Ordinal)
               || upper.Contains("EVENT ", StringComparison.Ordinal)
               || upper.Contains(" START:", StringComparison.Ordinal)
               || upper.Contains(" STOP:", StringComparison.Ordinal)
               || upper.Contains("LIVE ONLY", StringComparison.Ordinal)
               || Regex.IsMatch(displayName, @"^\d{1,2}/\d{1,2}\s*-\s*");
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return MultiSpaceRegex().Replace(value.Trim(), " ");
    }
}
