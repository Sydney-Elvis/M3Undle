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
}
