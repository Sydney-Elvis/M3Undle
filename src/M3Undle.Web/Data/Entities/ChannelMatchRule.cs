namespace M3Undle.Web.Data.Entities;

public sealed class ChannelMatchRule
{
    public string RuleId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string MatchValue { get; set; } = string.Empty;
    public string? TargetChannelId { get; set; }
    public string? TargetGroupName { get; set; }
    public int DefaultPriority { get; set; } = 1;
    public bool IsEventRule { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Profile Profile { get; set; } = null!;
    public CanonicalChannel? TargetChannel { get; set; }
}

