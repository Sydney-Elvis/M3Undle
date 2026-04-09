namespace M3Undle.Web.Data.Entities;

/// <summary>
/// A structured recurring-interest rule that controls how event channels are
/// auto-added or suppressed for a profile.
///
/// match_type values: keyword | team | league | sport | fighter | promotion | series
/// action values:     auto_add | notify | suppress
/// </summary>
public sealed class ProfileEventInterestRule
{
    public string RuleId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Optional — scope the rule to a specific provider.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Optional — scope the rule to a specific provider group.</summary>
    public string? ProviderGroupId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// One of: keyword | team | league | sport | fighter | promotion | series
    /// </summary>
    public string MatchType { get; set; } = "keyword";

    /// <summary>The value to match against (case-insensitive).</summary>
    public string MatchValue { get; set; } = string.Empty;

    /// <summary>
    /// One of: auto_add | notify | suppress
    /// </summary>
    public string Action { get; set; } = "auto_add";

    /// <summary>
    /// Priority for rule ordering — lower numbers are evaluated first.
    /// </summary>
    public int Priority { get; set; } = 100;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public Provider? Provider { get; set; }
    public ProviderGroup? ProviderGroup { get; set; }
}
