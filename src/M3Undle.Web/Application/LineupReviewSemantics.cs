namespace M3Undle.Web.Application;

internal static class LineupReviewSemantics
{
    public const string GroupDecisionPending = "pending";
    public const string GroupDecisionInclude = "include";
    public const string GroupDecisionExclude = "exclude";
    public const string GroupDecisionLegacyHold = "hold";

    // Stored in profile_group_filters.channel_mode
    public const string GroupModeManualReview = "select";
    public const string GroupModeAutoUpdate = "all";

    public const string TrackingPolicyReview = "review";
    public const string TrackingPolicyNotify = "notify";
    public const string TrackingPolicyAutoAddAll = "auto_add_all";
    public const string TrackingPolicyAutoAddPopulated = "auto_add_populated";
    public const string TrackingPolicyAutoAddMatching = "auto_add_matching";

    public const string ChannelStatePending = "pending";
    public const string ChannelStateIncluded = "included";
    public const string ChannelStateExcluded = "excluded";

    public static bool IsGroupPending(string? decision, bool isNew)
    {
        if (string.Equals(decision, GroupDecisionPending, StringComparison.Ordinal))
            return true;

        // Backward compatibility for pre-migration rows.
        return string.Equals(decision, GroupDecisionLegacyHold, StringComparison.Ordinal) && isNew;
    }

    public static bool IsGroupIncluded(string? decision, bool isNew)
    {
        if (string.Equals(decision, GroupDecisionInclude, StringComparison.Ordinal))
            return true;

        // Backward compatibility for pre-migration rows.
        return string.Equals(decision, GroupDecisionLegacyHold, StringComparison.Ordinal) && !isNew;
    }

    public static bool IsGroupExcluded(string? decision)
        => string.Equals(decision, GroupDecisionExclude, StringComparison.Ordinal);

    public static string NormalizeGroupDecision(string? decision, bool isNew)
    {
        if (IsGroupExcluded(decision))
            return GroupDecisionExclude;
        if (IsGroupPending(decision, isNew))
            return GroupDecisionPending;
        if (IsGroupIncluded(decision, isNew))
            return GroupDecisionInclude;

        return GroupDecisionPending;
    }

    public static string NormalizeGroupMode(string? mode)
        => string.Equals(mode, GroupModeAutoUpdate, StringComparison.Ordinal)
            ? GroupModeAutoUpdate
            : GroupModeManualReview;

    public static string NormalizeTrackingPolicy(string? policy)
    {
        if (string.Equals(policy, TrackingPolicyNotify, StringComparison.Ordinal))
            return TrackingPolicyNotify;
        if (string.Equals(policy, TrackingPolicyAutoAddAll, StringComparison.Ordinal))
            return TrackingPolicyAutoAddAll;
        if (string.Equals(policy, TrackingPolicyAutoAddPopulated, StringComparison.Ordinal))
            return TrackingPolicyAutoAddPopulated;
        if (string.Equals(policy, TrackingPolicyAutoAddMatching, StringComparison.Ordinal))
            return TrackingPolicyAutoAddMatching;

        return TrackingPolicyReview;
    }

    public static bool ShouldQueuePending(string? policy)
        => string.Equals(NormalizeTrackingPolicy(policy), TrackingPolicyReview, StringComparison.Ordinal);

    public static bool ShouldAutoAddAll(string? policy)
        => string.Equals(NormalizeTrackingPolicy(policy), TrackingPolicyAutoAddAll, StringComparison.Ordinal);

    public static bool ShouldAutoAddPopulated(string? policy)
        => string.Equals(NormalizeTrackingPolicy(policy), TrackingPolicyAutoAddPopulated, StringComparison.Ordinal);

    public static bool ShouldAutoAddMatching(string? policy)
        => string.Equals(NormalizeTrackingPolicy(policy), TrackingPolicyAutoAddMatching, StringComparison.Ordinal);

    public static bool MatchesTrackingKeywords(string? rawKeywords, params string?[] candidateTexts)
    {
        var terms = ParseTrackingKeywords(rawKeywords);
        if (terms.Count == 0)
            return false;

        var haystack = string.Join('\n',
            candidateTexts
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim().ToUpperInvariant()));

        if (haystack.Length == 0)
            return false;

        foreach (var term in terms)
        {
            if (haystack.Contains(term, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static List<string> ParseTrackingKeywords(string? rawKeywords)
    {
        if (string.IsNullOrWhiteSpace(rawKeywords))
            return [];

        return rawKeywords
            .Split(['\n', '\r', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length >= 2)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string NormalizeChannelState(string? state)
    {
        if (string.Equals(state, ChannelStateExcluded, StringComparison.Ordinal))
            return ChannelStateExcluded;
        if (string.Equals(state, ChannelStatePending, StringComparison.Ordinal))
            return ChannelStatePending;
        if (string.Equals(state, ChannelStateIncluded, StringComparison.Ordinal))
            return ChannelStateIncluded;

        // Legacy rows had no state column; treat as explicitly included.
        return ChannelStateIncluded;
    }
}
