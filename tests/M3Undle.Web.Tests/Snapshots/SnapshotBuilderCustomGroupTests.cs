using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Snapshots;

/// <summary>
/// Tests for profile_custom_groups behaviour inside SnapshotBuilder.BuildChannelIndex.
///
/// In production, BuildAsync injects custom group channels into the channel list and adds a
/// GroupFilterConfig entry keyed by "\u0002cg\u001f{customGroupId}" before calling
/// BuildChannelIndex.  These tests exercise that same wiring at the pure-function level so
/// they run without any database or HTTP dependencies.
/// </summary>
[TestClass]
public sealed class SnapshotBuilderCustomGroupTests
{
    // The same key prefix that BuildAsync writes to the includedGroups dict.
    private const string CgPrefix = "\u0002cg\u001f";

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SnapshotBuilder.ChannelBuildData RegularChannel(string displayName, string cgKey)
        => new(string.Empty, null, displayName,
               $"http://stream/{displayName.Replace(" ", "-")}",
               "live", cgKey, null, null, null);

    private static SnapshotBuilder.ChannelBuildData EventChannel(
        string displayName, string cgKey, string? contentKey = null, bool isPlaceholder = false)
        => new(string.Empty, null, displayName,
               $"http://stream/{displayName.Replace(" ", "-")}",
               "live", cgKey, null, null, null,
               IsEvent: true, IsPlaceholder: isPlaceholder, EventContentKey: contentKey);

    /// <summary>Builds a GroupFilterConfig for a custom group and returns both the dict key and config.</summary>
    private static (string Key, SnapshotBuilder.GroupFilterConfig Config) CustomGroupFilter(
        string cgId,
        string outputName,
        int? autoStart = null,
        int? autoEnd = null,
        string? mode = null,
        string? trackingPolicy = null,
        string? trackingKeywords = null)
    {
        var key = CgPrefix + cgId;
        var cfg = new SnapshotBuilder.GroupFilterConfig(
            cgId,
            outputName,
            autoStart,
            autoEnd,
            SortOverride: null,
            GroupMode: mode ?? LineupReviewSemantics.GroupModeAutoUpdate,
            TrackingPolicy: trackingPolicy ?? LineupReviewSemantics.TrackingPolicyReview,
            TrackingKeywords: trackingKeywords);
        return (key, cfg);
    }

    private static Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>> NoOverrides()
        => new(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // 1. Channels in a custom group appear in the snapshot output
    // -------------------------------------------------------------------------

    [TestMethod]
    public void CustomGroup_AllMode_ChannelsAppearsInOutput()
    {
        var (key, cfg) = CustomGroupFilter("cg-sports", "My Sports Picks", 500, 599,
            mode: LineupReviewSemantics.GroupModeAutoUpdate);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var ch = RegularChannel("Sky Sports 1", key);

        var result = SnapshotBuilder.BuildChannelIndex([ch], "p1", groups, NoOverrides(), false, false);

        Assert.HasCount(1, result, "Channel in a custom group must appear in output.");
        Assert.AreEqual("Sky Sports 1", result[0].DisplayName);
    }

    /// <summary>
    /// The channel's GroupTitle in the output is the custom group's OutputName,
    /// not the raw internal key (which contains the control-character prefix).
    /// </summary>
    [TestMethod]
    public void CustomGroup_OutputNameIsUsed_NotTheInternalKey()
    {
        var (key, cfg) = CustomGroupFilter("cg-news", "Weekend News", 600, 699,
            mode: LineupReviewSemantics.GroupModeAutoUpdate);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var ch = RegularChannel("BBC News", key);

        var result = SnapshotBuilder.BuildChannelIndex([ch], "p1", groups, NoOverrides(), false, false);

        Assert.HasCount(1, result);
        Assert.AreEqual("Weekend News", result[0].GroupTitle,
            "GroupTitle in output must be the configured OutputName, not the internal CG key.");
    }

    // -------------------------------------------------------------------------
    // 2. Custom group mode: select (manual review) vs all (auto-update)
    // -------------------------------------------------------------------------

    [TestMethod]
    public void CustomGroup_SelectMode_ExcludesChannelsWithoutExplicitInclude()
    {
        var (key, cfg) = CustomGroupFilter("cg-curated", "Curated", 700, 799,
            mode: LineupReviewSemantics.GroupModeManualReview);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        // No override recorded for this channel → should not appear.
        var ch = RegularChannel("CNN", key);

        var result = SnapshotBuilder.BuildChannelIndex([ch], "p1", groups, NoOverrides(), false, false);

        Assert.HasCount(0, result,
            "In select mode, channels without an explicit include override must be excluded.");
    }

    [TestMethod]
    public void CustomGroup_SelectMode_IncludesOnlyExplicitlyIncludedChannels()
    {
        var (key, cfg) = CustomGroupFilter("cg-curated", "Curated", 700, 799,
            mode: LineupReviewSemantics.GroupModeManualReview);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var included = RegularChannel("CNN", key);
        var notIncluded = RegularChannel("Fox News", key);

        // Only CNN has an explicit include override.
        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["cg-curated"] = new(StringComparer.Ordinal)
            {
                [included.StreamUrl!] = new(
                    LineupReviewSemantics.ChannelStateIncluded, null, null, null, null),
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex(
            [included, notIncluded], "p1", groups, overrides, false, false);

        Assert.HasCount(1, result, "Only the explicitly included channel must appear.");
        Assert.AreEqual("CNN", result[0].DisplayName);
    }

    [TestMethod]
    public void CustomGroup_AllMode_AutoIncludesRegularChannels()
    {
        var (key, cfg) = CustomGroupFilter("cg-all", "All Channels", 800, 899,
            mode: LineupReviewSemantics.GroupModeAutoUpdate,
            trackingPolicy: LineupReviewSemantics.TrackingPolicyReview);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var ch1 = RegularChannel("Channel A", key);
        var ch2 = RegularChannel("Channel B", key);

        var result = SnapshotBuilder.BuildChannelIndex([ch1, ch2], "p1", groups, NoOverrides(), false, false);

        Assert.HasCount(2, result, "All mode must auto-include regular (non-event) channels without any override.");
    }

    // -------------------------------------------------------------------------
    // 3. Tracking policy applies to event channels in a custom group
    // -------------------------------------------------------------------------

    [TestMethod]
    public void CustomGroup_AutoAddAll_PublishesEventChannels()
    {
        var (key, cfg) = CustomGroupFilter("cg-events", "My Events", 3100, 3199,
            mode: LineupReviewSemantics.GroupModeAutoUpdate,
            trackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddAll);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var ev = EventChannel("PPV Event 1: Big Fight", key, contentKey: "EVENTS::BIG FIGHT");

        var result = SnapshotBuilder.BuildChannelIndex([ev], "p1", groups, NoOverrides(), false, false);

        Assert.HasCount(1, result, "auto_add_all in a custom group must publish event channels.");
        Assert.AreEqual("PPV Event 1: Big Fight", result[0].DisplayName);
    }

    [TestMethod]
    public void CustomGroup_TrackingPolicyReview_ExcludesEventChannels()
    {
        // review policy in a custom group: event channels are not auto-published,
        // same as in a regular provider group.
        var (key, cfg) = CustomGroupFilter("cg-review", "Review Group", 3200, 3299,
            mode: LineupReviewSemantics.GroupModeAutoUpdate,
            trackingPolicy: LineupReviewSemantics.TrackingPolicyReview);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var ev = EventChannel("PPV Event 1: Title Fight", key, contentKey: "EVENTS::TITLE FIGHT");

        var result = SnapshotBuilder.BuildChannelIndex([ev], "p1", groups, NoOverrides(), false, false);

        Assert.HasCount(0, result,
            "review policy in a custom group must not auto-publish event channels.");
    }

    // -------------------------------------------------------------------------
    // 4. Per-channel overrides: channel_number and output_group_name
    // -------------------------------------------------------------------------

    [TestMethod]
    public void CustomGroup_PerChannelOverride_ChannelNumberApplied()
    {
        var (key, cfg) = CustomGroupFilter("cg-nums", "Numbered Group", 5000, 5099,
            mode: LineupReviewSemantics.GroupModeAutoUpdate);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var ch = RegularChannel("Sky One", key);

        // Pin the channel at 5042 via per-channel override.
        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["cg-nums"] = new(StringComparer.Ordinal)
            {
                [ch.StreamUrl!] = new(null, 5042, null),
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([ch], "p1", groups, overrides, false, false);

        Assert.HasCount(1, result);
        Assert.AreEqual(5042, result[0].TvgChno,
            "Per-channel channel_number override in a custom group must be honoured.");
    }

    [TestMethod]
    public void CustomGroup_PerChannelOverride_OutputGroupNameApplied()
    {
        var (key, cfg) = CustomGroupFilter("cg-remap", "Default Output", 5100, 5199,
            mode: LineupReviewSemantics.GroupModeAutoUpdate);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [key] = cfg,
        };

        var ch = RegularChannel("BT Sport 1", key);

        // Override the output group name for this specific channel.
        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["cg-remap"] = new(StringComparer.Ordinal)
            {
                [ch.StreamUrl!] = new("Sports Remapped", null, null),
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([ch], "p1", groups, overrides, false, false);

        Assert.HasCount(1, result);
        Assert.AreEqual("Sports Remapped", result[0].GroupTitle,
            "Per-channel output_group_name override in a custom group must override the group's OutputName.");
    }

    // -------------------------------------------------------------------------
    // 5. Multiple custom groups coexist without interfering
    // -------------------------------------------------------------------------

    [TestMethod]
    public void MultipleCustomGroups_ChannelsRoutedToCorrectOutputNames()
    {
        var (keyA, cfgA) = CustomGroupFilter("cg-sports", "Sports Picks", 5200, 5299,
            mode: LineupReviewSemantics.GroupModeAutoUpdate);
        var (keyB, cfgB) = CustomGroupFilter("cg-news", "News Picks", 5300, 5399,
            mode: LineupReviewSemantics.GroupModeAutoUpdate);

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            [keyA] = cfgA,
            [keyB] = cfgB,
        };

        var sports = RegularChannel("Sky Sports 1", keyA);
        var news = RegularChannel("Sky News", keyB);

        var result = SnapshotBuilder.BuildChannelIndex([sports, news], "p1", groups, NoOverrides(), false, false);

        Assert.HasCount(2, result);
        var byName = result.ToDictionary(e => e.DisplayName);
        Assert.AreEqual("Sports Picks", byName["Sky Sports 1"].GroupTitle);
        Assert.AreEqual("News Picks", byName["Sky News"].GroupTitle);
    }
}
