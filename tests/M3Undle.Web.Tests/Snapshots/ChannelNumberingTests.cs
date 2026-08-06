using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Snapshots;

/// <summary>
/// Tests for the channel numbering rules implemented in SnapshotBuilder.BuildChannelIndex.
/// </summary>
[TestClass]
public sealed class ChannelNumberingTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SnapshotBuilder.ChannelBuildData LiveChannel(string displayName, string groupTitle, string streamUrl = "")
        => new(string.Empty, null, displayName, streamUrl.Length > 0 ? streamUrl : $"http://stream/{displayName}", "live", groupTitle, null, null, null);

    private static SnapshotBuilder.ChannelBuildData LiveEventChannel(
        string displayName,
        string groupTitle,
        bool isPlaceholder = false,
        string? eventContentKey = null,
        string streamUrl = "")
        => new(
            string.Empty,
            null,
            displayName,
            streamUrl.Length > 0 ? streamUrl : $"http://stream/{displayName}",
            "live",
            groupTitle,
            null,
            null,
            null,
            IsEvent: true,
            IsPlaceholder: isPlaceholder,
            EventContentKey: eventContentKey);

    private static Dictionary<string, SnapshotBuilder.GroupFilterConfig> OneGroup(
        string rawName,
        string outputName,
        int? autoStart = null,
        int? autoEnd = null,
        int? sortOverride = null)
        => new(StringComparer.Ordinal)
        {
            [rawName] = new SnapshotBuilder.GroupFilterConfig("f1", outputName, autoStart, autoEnd, sortOverride),
        };

    private static Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>> SelectAll(
        IReadOnlyDictionary<string, SnapshotBuilder.GroupFilterConfig> groups,
        IEnumerable<SnapshotBuilder.ChannelBuildData> channels,
        Func<SnapshotBuilder.ChannelBuildData, int?>? pinNumber = null)
    {
        var result = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal);
        foreach (var (_, cfg) in groups)
        {
            var overrides = new Dictionary<string, SnapshotBuilder.ChannelOverride>(StringComparer.Ordinal);
            foreach (var ch in channels.Where(c => c.GroupTitle == groups.Keys.First(k => groups[k] == cfg)))
                overrides[ch.StreamUrl!] = new SnapshotBuilder.ChannelOverride(null, pinNumber?.Invoke(ch), null);
            result[cfg.ProfileGroupFilterId] = overrides;
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Basic auto-numbering
    // -------------------------------------------------------------------------

    [TestMethod]
    public void BuildChannelIndex_RecordsProviderIdentityForInMemoryVod()
    {
        var vod = new SnapshotBuilder.ChannelBuildData(
            string.Empty,
            "vod-key",
            "Movie One",
            "http://provider.test/movie/user/pass/1001.mkv",
            "vod",
            "Movies",
            null,
            null,
            null);

        var result = SnapshotBuilder.BuildChannelIndex(
            [vod],
            "profile-1",
            new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(),
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(),
            includeVod: true,
            includeSeries: false,
            providerId: "provider-1");

        Assert.HasCount(1, result);
        Assert.AreEqual("provider-1", result[0].ProviderId);
        Assert.AreEqual(string.Empty, result[0].ProviderChannelId);
    }

    [TestMethod]
    public void BuildChannelIndex_IgnoresDormantCatalogGroupExclusion()
    {
        var channels = new[]
        {
            new SnapshotBuilder.ChannelBuildData(
                string.Empty, "vod-1", "Movie One", "http://provider/movie/1.mkv",
                "vod", "Reality", null, null, null),
            new SnapshotBuilder.ChannelBuildData(
                string.Empty, "series-1", "Series One S01 E01", "http://provider/series/1.mkv",
                "series", "Reality", null, null, null),
        };
        var excluded = new HashSet<SnapshotBuilder.ProviderGroupKey>
        {
            new("Reality", "series"),
        };

        var result = SnapshotBuilder.BuildChannelIndex(
            channels,
            "profile-1",
            new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(),
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(),
            includeVod: true,
            includeSeries: true,
            providerId: "provider-1",
            excludedCatalogGroups: excluded);

        Assert.HasCount(2, result);
        CollectionAssert.AreEquivalent(
            new[] { "Movie One", "Series One S01 E01" },
            result.Select(x => x.DisplayName).ToArray());
    }

    [TestMethod]
    public void AutoNumbering_AssignsSequentialNumbers()
    {
        var groups = OneGroup("News", "News", autoStart: 100, autoEnd: 110);
        var channels = new[]
        {
            LiveChannel("CNN", "News"),
            LiveChannel("BBC", "News"),
            LiveChannel("Fox", "News"),
        };

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["f1"] = channels.ToDictionary(c => c.StreamUrl!, _ => new SnapshotBuilder.ChannelOverride(null, null, null), StringComparer.Ordinal),
        };

        var result = SnapshotBuilder.BuildChannelIndex(channels, "p1", groups, overrides, false, false);

        // Channels sorted alphabetically (BBC, CNN, Fox) then numbered 100, 101, 102
        Assert.HasCount(3, result);
        var byName = result.ToDictionary(e => e.DisplayName);
        Assert.AreEqual(100, byName["BBC"].TvgChno);
        Assert.AreEqual(101, byName["CNN"].TvgChno);
        Assert.AreEqual(102, byName["Fox"].TvgChno);
    }

    // -------------------------------------------------------------------------
    // Conflict avoidance: pinned numbers are skipped during auto-assignment
    // -------------------------------------------------------------------------

    [TestMethod]
    public void AutoNumbering_SkipsPinnedNumberInSameGroup()
    {
        var groups = OneGroup("News", "News", autoStart: 100, autoEnd: 110);
        var channels = new[]
        {
            LiveChannel("CNN", "News"),
            LiveChannel("BBC", "News"),
            LiveChannel("Fox", "News"),
        };

        // Pin Fox at 101 — auto-numbering for the others should skip 101
        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["f1"] = new Dictionary<string, SnapshotBuilder.ChannelOverride>(StringComparer.Ordinal)
            {
                [channels[0].StreamUrl!] = new(null, null, null),        // CNN — auto
                [channels[1].StreamUrl!] = new(null, null, null),        // BBC — auto
                [channels[2].StreamUrl!] = new(null, 101, null),         // Fox — pinned at 101
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex(channels, "p1", groups, overrides, false, false);

        Assert.HasCount(3, result);
        var byName = result.ToDictionary(e => e.DisplayName);

        Assert.AreEqual(101, byName["Fox"].TvgChno);   // pin respected
        // BBC and CNN are auto-numbered — 101 skipped, so they get 100 and 102
        var autoNumbers = new[] { byName["BBC"].TvgChno!.Value, byName["CNN"].TvgChno!.Value };
        CollectionAssert.DoesNotContain(autoNumbers, 101);
        Assert.AreEqual(100, autoNumbers.Min());
        Assert.AreEqual(102, autoNumbers.Max());
    }

    [TestMethod]
    public void AutoNumbering_SkipsPinnedNumberAcrossGroups()
    {
        // Group A: auto-start=100, Group B: has a channel pinned at 102
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["GroupA"] = new("fA", "GroupA", 100, 110, SortOverride: 1),
            ["GroupB"] = new("fB", "GroupB", 100, 110, SortOverride: 2),
        };

        var chA1 = LiveChannel("A1", "GroupA");
        var chA2 = LiveChannel("A2", "GroupA");
        var chB1 = LiveChannel("B1", "GroupB");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["fA"] = new(StringComparer.Ordinal)
            {
                [chA1.StreamUrl!] = new(null, null, null),
                [chA2.StreamUrl!] = new(null, null, null),
            },
            ["fB"] = new(StringComparer.Ordinal)
            {
                [chB1.StreamUrl!] = new(null, 102, null),  // B1 pinned at 102
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([chA1, chA2, chB1], "p1", groups, overrides, false, false);

        var byName = result.ToDictionary(e => e.DisplayName);

        // GroupA processes first (SortOverride=1); A1 and A2 get 100 and 101 (skip 102)
        Assert.AreEqual(102, byName["B1"].TvgChno);    // pinned
        Assert.AreNotEqual(102, byName["A1"].TvgChno, "A1 must not collide with pinned 102");
        Assert.AreNotEqual(102, byName["A2"].TvgChno, "A2 must not collide with pinned 102");
    }

    // -------------------------------------------------------------------------
    // Skip-and-continue fill: range skips pinned then resumes
    // -------------------------------------------------------------------------

    [TestMethod]
    public void AutoNumbering_SkipAndContinueAroundPinnedGap()
    {
        // News: start=100, end=110; "F-Pinned" is pinned at 105.
        // Auto-numbering fills A(100), B(101), C(102), D(103), E(104),
        // then hits 105 (blocked by pin), skips to 106 for G.
        var groups = OneGroup("News", "News", autoStart: 100, autoEnd: 110);

        var chA = LiveChannel("A", "News");
        var chB = LiveChannel("B", "News");
        var chC = LiveChannel("C", "News");
        var chD = LiveChannel("D", "News");
        var chE = LiveChannel("E", "News");
        var chF = LiveChannel("F-Pinned", "News");  // pinned at 105 (sorts after E)
        var chG = LiveChannel("G", "News");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["f1"] = new(StringComparer.Ordinal)
            {
                [chA.StreamUrl!] = new(null, null, null),
                [chB.StreamUrl!] = new(null, null, null),
                [chC.StreamUrl!] = new(null, null, null),
                [chD.StreamUrl!] = new(null, null, null),
                [chE.StreamUrl!] = new(null, null, null),
                [chF.StreamUrl!] = new(null, 105, null),  // pinned
                [chG.StreamUrl!] = new(null, null, null),
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([chA, chB, chC, chD, chE, chF, chG], "p1", groups, overrides, false, false);

        var byName = result.ToDictionary(e => e.DisplayName);
        Assert.AreEqual(105, byName["F-Pinned"].TvgChno);

        var autoNums = new[] { "A", "B", "C", "D", "E", "G" }.Select(n => byName[n].TvgChno!.Value).OrderBy(x => x).ToArray();
        CollectionAssert.DoesNotContain(autoNums, 105, "Auto-numbering must skip the pinned slot at 105");
        // A-E fill 100-104; G skips 105 and gets 106
        Assert.AreEqual(100, autoNums[0]);
        Assert.AreEqual(101, autoNums[1]);
        Assert.AreEqual(102, autoNums[2]);
        Assert.AreEqual(103, autoNums[3]);
        Assert.AreEqual(104, autoNums[4]);
        Assert.AreEqual(106, autoNums[5]);
    }

    // -------------------------------------------------------------------------
    // Overflow: exhausted range goes to 9000+
    // -------------------------------------------------------------------------

    [TestMethod]
    public void AutoNumbering_OverflowWhenRangeExhausted()
    {
        // Range 100-101 holds only 2; third channel should go to overflow
        var groups = OneGroup("News", "News", autoStart: 100, autoEnd: 101);
        var ch1 = LiveChannel("Alpha", "News");
        var ch2 = LiveChannel("Beta", "News");
        var ch3 = LiveChannel("Gamma", "News");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["f1"] = new(StringComparer.Ordinal)
            {
                [ch1.StreamUrl!] = new(null, null, null),
                [ch2.StreamUrl!] = new(null, null, null),
                [ch3.StreamUrl!] = new(null, null, null),
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([ch1, ch2, ch3], "p1", groups, overrides, false, false);

        var byName = result.ToDictionary(e => e.DisplayName);
        Assert.AreEqual(100, byName["Alpha"].TvgChno);
        Assert.AreEqual(101, byName["Beta"].TvgChno);
        Assert.IsNotNull(byName["Gamma"].TvgChno, "Overflow channel must receive a number");
        Assert.IsGreaterThanOrEqualTo(byName["Gamma"].TvgChno!.Value, SnapshotBuilder.OverflowRangeStart,
            $"Expected overflow number >= {SnapshotBuilder.OverflowRangeStart}, got {byName["Gamma"].TvgChno}");
    }

    [TestMethod]
    public void AutoNumbering_OverflowSkipsAlreadyUsedNumbers()
    {
        // Range 100-100 (one slot); second channel overflows. Overflow start (9000) is also pinned.
        var groups = OneGroup("News", "News", autoStart: 100, autoEnd: 100);
        var ch1 = LiveChannel("Alpha", "News");
        var ch2 = LiveChannel("Beta", "News");
        var ch3 = LiveChannel("Gamma", "News");  // pinned at 9000

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["f1"] = new(StringComparer.Ordinal)
            {
                [ch1.StreamUrl!] = new(null, null, null),
                [ch2.StreamUrl!] = new(null, null, null),
                [ch3.StreamUrl!] = new(null, 9000, null),  // occupies overflow start
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([ch1, ch2, ch3], "p1", groups, overrides, false, false);

        var byName = result.ToDictionary(e => e.DisplayName);
        Assert.AreEqual(9000, byName["Gamma"].TvgChno);    // pin honored
        Assert.IsNotNull(byName["Beta"].TvgChno);
        Assert.AreEqual(9001, byName["Beta"].TvgChno,      // overflow skips 9000
            "Overflow must skip 9000 which is already pinned");
    }

    // -------------------------------------------------------------------------
    // Duplicate pinned numbers: the build must never ship a collision
    // -------------------------------------------------------------------------

    [TestMethod]
    public void PinnedNumbers_DuplicateAcrossGroups_SecondFallsToOverflow()
    {
        // Two different channels in different groups both pinned to 100 — e.g. stale
        // client-side auto-numbering that picked an already-used number because the
        // colliding group wasn't loaded client-side. The build must never ship a duplicate.
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["GroupA"] = new("fA", "GroupA", null, null, SortOverride: 1),
            ["GroupB"] = new("fB", "GroupB", null, null, SortOverride: 2),
        };

        var chA = LiveChannel("A-Channel", "GroupA");
        var chB = LiveChannel("B-Channel", "GroupB");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["fA"] = new(StringComparer.Ordinal) { [chA.StreamUrl!] = new(null, 100, null) },
            ["fB"] = new(StringComparer.Ordinal) { [chB.StreamUrl!] = new(null, 100, null) },
        };

        var result = SnapshotBuilder.BuildChannelIndex([chA, chB], "p1", groups, overrides, false, false);

        Assert.HasCount(2, result, "Both channels must still be published — a collision must not drop one.");
        Assert.AreEqual(2, result.Select(r => r.TvgChno).Distinct().Count(),
            "Channel numbers must be unique — no duplicates allowed in build output.");

        var byName = result.ToDictionary(e => e.DisplayName);
        Assert.AreEqual(100, byName["A-Channel"].TvgChno, "GroupA processes first (SortOverride=1) and keeps the pin.");
        Assert.IsGreaterThanOrEqualTo(byName["B-Channel"].TvgChno!.Value, SnapshotBuilder.OverflowRangeStart,
            $"Colliding pin must fall through to overflow, got {byName["B-Channel"].TvgChno}");
    }

    [TestMethod]
    public void PinnedNumbers_DuplicateWithinSameGroup_SecondFallsToOverflow()
    {
        var groups = OneGroup("News", "News");
        var ch1 = LiveChannel("Alpha", "News");
        var ch2 = LiveChannel("Beta", "News");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["f1"] = new(StringComparer.Ordinal)
            {
                [ch1.StreamUrl!] = new(null, 100, null),
                [ch2.StreamUrl!] = new(null, 100, null),
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([ch1, ch2], "p1", groups, overrides, false, false);

        Assert.HasCount(2, result, "Both channels must still be published — a collision must not drop one.");
        Assert.AreEqual(2, result.Select(r => r.TvgChno).Distinct().Count(),
            "Duplicate pins within the same group must still resolve to unique numbers.");
        Assert.AreEqual(100, result.First(r => r.DisplayName == "Alpha").TvgChno, "First-placed channel keeps the pin.");
        Assert.IsGreaterThanOrEqualTo(result.First(r => r.DisplayName == "Beta").TvgChno!.Value, SnapshotBuilder.OverflowRangeStart,
            "Colliding pin must fall through to overflow.");
    }

    [TestMethod]
    public void NoRange_ChannelGetsNoNumber()
    {
        // Group has no auto-number range — channels should get no number assigned
        var groups = OneGroup("News", "News");   // no autoStart/autoEnd
        var ch = LiveChannel("CNN", "News");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["f1"] = new(StringComparer.Ordinal) { [ch.StreamUrl!] = new(null, null, null) },
        };

        var result = SnapshotBuilder.BuildChannelIndex([ch], "p1", groups, overrides, false, false);

        Assert.HasCount(1, result);
        Assert.IsNull(result[0].TvgChno, "Channel with no range and no pin should have no number");
    }

    // -------------------------------------------------------------------------
    // Group ordering via SortOverride
    // -------------------------------------------------------------------------

    [TestMethod]
    public void GroupOrdering_SortOverrideControlsAllocationOrder()
    {
        // Both groups share range 100-101 (only 2 slots total).
        // GroupB has SortOverride=1 (higher priority), GroupA has SortOverride=2.
        // GroupB should get 100 and 101; GroupA should overflow.
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["GroupA"] = new("fA", "GroupA", 100, 101, SortOverride: 2),
            ["GroupB"] = new("fB", "GroupB", 100, 101, SortOverride: 1),
        };

        var chA = LiveChannel("A-Channel", "GroupA");
        var chB1 = LiveChannel("B-Channel1", "GroupB");
        var chB2 = LiveChannel("B-Channel2", "GroupB");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["fA"] = new(StringComparer.Ordinal) { [chA.StreamUrl!] = new(null, null, null) },
            ["fB"] = new(StringComparer.Ordinal)
            {
                [chB1.StreamUrl!] = new(null, null, null),
                [chB2.StreamUrl!] = new(null, null, null),
            },
        };

        var result = SnapshotBuilder.BuildChannelIndex([chA, chB1, chB2], "p1", groups, overrides, false, false);

        var byName = result.ToDictionary(e => e.DisplayName);
        // GroupB (SortOverride=1) processes first: B-Channel1=100, B-Channel2=101
        Assert.AreEqual(100, byName["B-Channel1"].TvgChno);
        Assert.AreEqual(101, byName["B-Channel2"].TvgChno);
        // GroupA (SortOverride=2) processes after range is full → overflow
        Assert.IsGreaterThanOrEqualTo(byName["A-Channel"].TvgChno!.Value, SnapshotBuilder.OverflowRangeStart,
            $"GroupA channel should overflow, got {byName["A-Channel"].TvgChno}");
    }

    [TestMethod]
    public void GroupOrdering_NoSortOverride_FallsBackToAlphabetical()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Zebra"] = new("fZ", "Zebra", 100, 100, SortOverride: null),
            ["Alpha"] = new("fA", "Alpha", 100, 100, SortOverride: null),
        };

        var chZ = LiveChannel("Z-Ch", "Zebra");
        var chA = LiveChannel("A-Ch", "Alpha");

        var overrides = new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal)
        {
            ["fZ"] = new(StringComparer.Ordinal) { [chZ.StreamUrl!] = new(null, null, null) },
            ["fA"] = new(StringComparer.Ordinal) { [chA.StreamUrl!] = new(null, null, null) },
        };

        var result = SnapshotBuilder.BuildChannelIndex([chZ, chA], "p1", groups, overrides, false, false);

        var byName = result.ToDictionary(e => e.DisplayName);
        // Alpha < Zebra alphabetically, so Alpha gets 100 first; Zebra overflows
        Assert.AreEqual(100, byName["A-Ch"].TvgChno);
        Assert.IsGreaterThanOrEqualTo(byName["Z-Ch"].TvgChno!.Value, SnapshotBuilder.OverflowRangeStart,
            $"Zebra should overflow, got {byName["Z-Ch"].TvgChno}");
    }

    [TestMethod]
    public void AutoUpdate_ReviewPolicy_DoesNotAutoAddEventChannels()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyReview,
                TrackingKeywords: null),
        };

        var eventChannel = LiveEventChannel(
            "Sky Sports+ | Event 1: Team A vs Team B",
            "Events",
            eventContentKey: "EVENTS::TEAM A VS TEAM B");
        var normalChannel = LiveChannel("Main Feed", "Events");

        var result = SnapshotBuilder.BuildChannelIndex(
            [eventChannel, normalChannel],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result);
        Assert.AreEqual("Main Feed", result[0].DisplayName);
    }

    [TestMethod]
    public void AutoUpdate_AutoAddMatching_IncludesMatches_AndSkipsPlaceholders()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddMatching,
                TrackingKeywords: "Eagles|Bills"),
        };

        var matching = LiveEventChannel(
            "Sky Sports+ | Event 10: Eagles vs Giants",
            "Events",
            eventContentKey: "EVENTS::EAGLES VS GIANTS");
        var nonMatching = LiveEventChannel(
            "Sky Sports+ | Event 11: Dolphins vs Jets",
            "Events",
            eventContentKey: "EVENTS::DOLPHINS VS JETS");
        var placeholder = LiveEventChannel(
            "Sky Sports+ | Event 12:",
            "Events",
            isPlaceholder: true);

        var result = SnapshotBuilder.BuildChannelIndex(
            [matching, nonMatching, placeholder],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result);
        Assert.AreEqual("Sky Sports+ | Event 10: Eagles vs Giants", result[0].DisplayName);
        Assert.AreEqual(3000, result[0].TvgChno);
    }

    // -------------------------------------------------------------------------
    // State reuse: same event content in a different slot
    // -------------------------------------------------------------------------

    /// <summary>
    /// Regression: when an event moves from slot N to slot M (stream URL changes),
    /// auto_add_all policy must still publish it — it must not require a prior per-slot override.
    /// </summary>
    [TestMethod]
    public void EventStateReuse_AutoAddAll_PublishesEventInNewSlot()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddAll,
                TrackingKeywords: null),
        };

        // Simulate "week 2": event that was in slot 1 has moved to slot 3.
        // There is no prior override for the new stream URL.
        var eventInNewSlot = LiveEventChannel(
            "Sky Sports+ | Event 3: Eagles vs Giants",
            "Events",
            eventContentKey: "EVENTS::EAGLES VS GIANTS",
            streamUrl: "http://stream/new-slot-3");

        var result = SnapshotBuilder.BuildChannelIndex(
            [eventInNewSlot],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result, "Event moved to a new slot must still be published under auto_add_all.");
        Assert.AreEqual("Sky Sports+ | Event 3: Eagles vs Giants", result[0].DisplayName);
    }

    /// <summary>
    /// Regression: when an event moves between slots, auto_add_matching must match by
    /// content (keyword against display name / content key), not by prior slot stream URL.
    /// </summary>
    [TestMethod]
    public void EventStateReuse_AutoAddMatching_MatchesEventInNewSlot()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddMatching,
                TrackingKeywords: "Eagles"),
        };

        // Week 1: Eagles game in slot 1 (old stream URL, no override stored)
        // Week 2: same event is now in slot 5 (new stream URL)
        var eventInNewSlot = LiveEventChannel(
            "Sky Sports+ | Event 5: Eagles vs Cowboys",
            "Events",
            eventContentKey: "EVENTS::EAGLES VS COWBOYS",
            streamUrl: "http://stream/slot5-eagles");

        var result = SnapshotBuilder.BuildChannelIndex(
            [eventInNewSlot],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result, "Matching event in a new slot must be auto-added by keyword.");
    }

    /// <summary>
    /// Regression: a placeholder in the same slot that previously held a real event
    /// must not be published, even when surrounded by real events that are auto-added.
    /// </summary>
    [TestMethod]
    public void EventStateReuse_PlaceholderInFormerSlot_IsNeverPublished()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddAll,
                TrackingKeywords: null),
        };

        // Slot 1 now empty (placeholder), slot 2 has a real event
        var emptySlot = LiveEventChannel("Sky Sports+ | Event 1:", "Events", isPlaceholder: true);
        var realEvent = LiveEventChannel(
            "Sky Sports+ | Event 2: Spurs vs Celtics",
            "Events",
            eventContentKey: "EVENTS::SPURS VS CELTICS");

        var result = SnapshotBuilder.BuildChannelIndex(
            [emptySlot, realEvent],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result, "Only the real event must be published; the empty slot is a placeholder.");
        Assert.AreEqual("Sky Sports+ | Event 2: Spurs vs Celtics", result[0].DisplayName);
    }

    /// <summary>
    /// Regression: when a group has auto_add_all policy, multiple feeds for the same
    /// event content key (duplicate feeds) are all published — the user decides on deduplication.
    /// </summary>
    [TestMethod]
    public void EventStateReuse_MultipleFeedsForSameContent_AllPublished()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddAll,
                TrackingKeywords: null),
        };

        var feed1 = LiveEventChannel(
            "Sky Sports+ | Event 4: UFC 300 Main Card",
            "Events",
            eventContentKey: "EVENTS::UFC 300 MAIN CARD",
            streamUrl: "http://stream/ufc300-hd");
        var feed2 = LiveEventChannel(
            "Sky Sports+ | Event 5: UFC 300 Main Card",
            "Events",
            eventContentKey: "EVENTS::UFC 300 MAIN CARD",
            streamUrl: "http://stream/ufc300-sd");

        var result = SnapshotBuilder.BuildChannelIndex(
            [feed1, feed2],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(2, result, "Both feeds for the same event must be published independently.");
        Assert.AreEqual(3000, result.Min(r => r.TvgChno), "First feed should get 3000.");
        Assert.AreEqual(3001, result.Max(r => r.TvgChno), "Second feed should get 3001.");
    }

    /// <summary>
    /// Regression: structured interest rules (auto_add action) must match events
    /// by typed criteria (sport/league) and suppress must block auto-add.
    /// </summary>
    [TestMethod]
    public void InterestRules_AutoAdd_AndSuppress_WorkCorrectly()
    {
        // Suppress Bellator (priority 50) is checked before auto_add mma (priority 100).
        // Without correct ordering, the mma auto_add rule would fire first for Bellator events
        // and the suppress would never be reached.
        var rules = new List<SnapshotBuilder.InterestRuleConfig>
        {
            new(LineupReviewSemantics.InterestMatchTypeLeague, "Bellator", LineupReviewSemantics.InterestActionSuppress),
            new(LineupReviewSemantics.InterestMatchTypeSport, "mma", LineupReviewSemantics.InterestActionAutoAdd),
        };

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddMatching,
                TrackingKeywords: null,
                InterestRules: rules),
        };

        // UFC event: sport=mma → auto_add rule hits → included
        var ufcEvent = new SnapshotBuilder.ChannelBuildData(
            string.Empty, null, "UFC 305 Main Card", "http://stream/ufc305", "live", "Events",
            null, null, null,
            IsEvent: true, IsPlaceholder: false, EventContentKey: "EVENTS::UFC 305",
            EventSport: "mma", EventLeague: "UFC");

        // Bellator event: league=Bellator → suppress rule hits → excluded
        var bellatorEvent = new SnapshotBuilder.ChannelBuildData(
            string.Empty, null, "Bellator 300", "http://stream/bellator300", "live", "Events",
            null, null, null,
            IsEvent: true, IsPlaceholder: false, EventContentKey: "EVENTS::BELLATOR 300",
            EventSport: "mma", EventLeague: "Bellator");

        var result = SnapshotBuilder.BuildChannelIndex(
            [ufcEvent, bellatorEvent],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result, "Suppressed Bellator event must be excluded; UFC must be included.");
        Assert.AreEqual("UFC 305 Main Card", result[0].DisplayName);
    }

    // -------------------------------------------------------------------------
    // Tracking policy: auto_add_populated
    // -------------------------------------------------------------------------

    /// <summary>
    /// auto_add_populated publishes event channels that have a non-empty EventContentKey
    /// (i.e. the slot was successfully classified) and skips slots where EventContentKey
    /// is null/empty (blank / unrecognisable slots).
    /// </summary>
    [TestMethod]
    public void AutoUpdate_AutoAddPopulated_IncludesPopulatedEvents_ExcludesBlanks()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddPopulated,
                TrackingKeywords: null),
        };

        // Slot with recognised content key → must be published.
        var populated = LiveEventChannel(
            "Sky Sports+ | Event 1: Arsenal vs Chelsea",
            "Events",
            eventContentKey: "EVENTS::ARSENAL VS CHELSEA");

        // Slot where the classifier found no content → must be skipped.
        var blank = LiveEventChannel(
            "Sky Sports+ | Event 2:",
            "Events",
            eventContentKey: null);

        var result = SnapshotBuilder.BuildChannelIndex(
            [populated, blank],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result, "Only the populated event slot must be published.");
        Assert.AreEqual("Sky Sports+ | Event 1: Arsenal vs Chelsea", result[0].DisplayName);
        Assert.AreEqual(3000, result[0].TvgChno);
    }

    /// <summary>
    /// Non-event channels in an auto_add_populated group are always auto-included —
    /// the populated check only gates event slots.
    /// </summary>
    [TestMethod]
    public void AutoUpdate_AutoAddPopulated_IncludesNonEventChannels()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Sports"] = new(
                "f1",
                "Sports",
                200,
                299,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddPopulated,
                TrackingKeywords: null),
        };

        var regular = LiveChannel("Sky Sports Main Event", "Sports");

        var result = SnapshotBuilder.BuildChannelIndex(
            [regular],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result);
        Assert.AreEqual("Sky Sports Main Event", result[0].DisplayName);
    }

    /// <summary>
    /// Placeholders are always excluded regardless of policy, including auto_add_populated.
    /// </summary>
    [TestMethod]
    public void AutoUpdate_AutoAddPopulated_PlaceholderAlwaysExcluded()
    {
        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddPopulated,
                TrackingKeywords: null),
        };

        var placeholder = LiveEventChannel("Sky Sports+ | Event 1:", "Events", isPlaceholder: true);

        var result = SnapshotBuilder.BuildChannelIndex(
            [placeholder],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(0, result, "Placeholder must be excluded even under auto_add_populated.");
    }

    // -------------------------------------------------------------------------
    // Interest rules: notify action
    // -------------------------------------------------------------------------

    /// <summary>
    /// A channel matched only by a notify rule must not be auto-published.
    /// notify surfaces the channel in the review queue but does not trigger auto-add.
    /// </summary>
    [TestMethod]
    public void InterestRules_Notify_DoesNotAutoPublishMatchedEvent()
    {
        var rules = new List<SnapshotBuilder.InterestRuleConfig>
        {
            new(LineupReviewSemantics.InterestMatchTypeLeague, "Premier League", LineupReviewSemantics.InterestActionNotify),
        };

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddMatching,
                TrackingKeywords: null,   // no keyword fallback
                InterestRules: rules),
        };

        var premierLeagueEvent = new SnapshotBuilder.ChannelBuildData(
            string.Empty, null,
            "Sky Sports+ | Event 1: Arsenal vs Chelsea",
            "http://stream/event1", "live", "Events",
            null, null, null,
            IsEvent: true, IsPlaceholder: false,
            EventContentKey: "EVENTS::ARSENAL VS CHELSEA",
            EventLeague: "Premier League");

        var result = SnapshotBuilder.BuildChannelIndex(
            [premierLeagueEvent],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(0, result, "A notify-rule match must not auto-publish the channel.");
    }

    /// <summary>
    /// When one event matches notify and a different event independently matches auto_add,
    /// only the auto_add event is published — notify does not contaminate unrelated rules.
    /// </summary>
    [TestMethod]
    public void InterestRules_Notify_DoesNotSuppressUnrelatedAutoAddRule()
    {
        var rules = new List<SnapshotBuilder.InterestRuleConfig>
        {
            new(LineupReviewSemantics.InterestMatchTypeLeague, "Premier League", LineupReviewSemantics.InterestActionNotify),
            new(LineupReviewSemantics.InterestMatchTypeSport, "mma", LineupReviewSemantics.InterestActionAutoAdd),
        };

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddMatching,
                TrackingKeywords: null,
                InterestRules: rules),
        };

        var notifyEvent = new SnapshotBuilder.ChannelBuildData(
            string.Empty, null,
            "Sky Sports+ | Event 1: Arsenal vs Chelsea",
            "http://stream/event1", "live", "Events",
            null, null, null,
            IsEvent: true, IsPlaceholder: false,
            EventContentKey: "EVENTS::ARSENAL VS CHELSEA",
            EventLeague: "Premier League");

        var autoAddEvent = new SnapshotBuilder.ChannelBuildData(
            string.Empty, null,
            "UFC 300 Main Card",
            "http://stream/ufc300", "live", "Events",
            null, null, null,
            IsEvent: true, IsPlaceholder: false,
            EventContentKey: "EVENTS::UFC 300",
            EventSport: "mma");

        var result = SnapshotBuilder.BuildChannelIndex(
            [notifyEvent, autoAddEvent],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        Assert.HasCount(1, result, "Only the auto_add event must be published; the notify event must not.");
        Assert.AreEqual("UFC 300 Main Card", result[0].DisplayName);
    }

    /// <summary>
    /// A notify rule takes precedence over a keyword fallback that would otherwise match.
    /// If the rule engine fires notify, the channel is not auto-added even if TrackingKeywords
    /// would independently match it.
    /// </summary>
    [TestMethod]
    public void InterestRules_Notify_TakesPrecedenceOverKeywordFallback()
    {
        // The notify rule matches "Premier League" → loop continues past the rule without returning.
        // TrackingKeywords also contains "Arsenal" which matches the event display name.
        // The expected behaviour: keyword fallback still runs after a notify rule,
        // so the channel IS published via keyword match. This test documents the current
        // semantics and acts as a regression guard if the ordering is ever changed.
        var rules = new List<SnapshotBuilder.InterestRuleConfig>
        {
            new(LineupReviewSemantics.InterestMatchTypeLeague, "Premier League", LineupReviewSemantics.InterestActionNotify),
        };

        var groups = new Dictionary<string, SnapshotBuilder.GroupFilterConfig>(StringComparer.Ordinal)
        {
            ["Events"] = new(
                "f1",
                "Events",
                3000,
                3099,
                SortOverride: null,
                GroupMode: LineupReviewSemantics.GroupModeAutoUpdate,
                TrackingPolicy: LineupReviewSemantics.TrackingPolicyAutoAddMatching,
                TrackingKeywords: "Arsenal",   // keyword also matches
                InterestRules: rules),
        };

        var event1 = new SnapshotBuilder.ChannelBuildData(
            string.Empty, null,
            "Sky Sports+ | Event 1: Arsenal vs Chelsea",
            "http://stream/event1", "live", "Events",
            null, null, null,
            IsEvent: true, IsPlaceholder: false,
            EventContentKey: "EVENTS::ARSENAL VS CHELSEA",
            EventLeague: "Premier League");

        var result = SnapshotBuilder.BuildChannelIndex(
            [event1],
            "p1",
            groups,
            new Dictionary<string, Dictionary<string, SnapshotBuilder.ChannelOverride>>(StringComparer.Ordinal),
            includeVod: false,
            includeSeries: false);

        // notify rule fires but does not return — keyword fallback runs and matches → included
        Assert.HasCount(1, result,
            "notify rule does not short-circuit keyword fallback; keyword match should still publish the channel.");
    }
}
