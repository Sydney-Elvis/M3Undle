using M3Undle.Core.Filtering;
using M3Undle.Core.IO;
using M3Undle.Core.M3u;

namespace M3Undle.Core.Tests.Filtering;

[TestClass]
public sealed class PlaylistGroupFilterTests
{
    [TestMethod]
    public void Apply_WithoutGroupSelection_KeepsAllEntriesAndCountsGroups()
    {
        var entries = new[]
        {
            Entry("Sports", "Channel 1"),
            Entry("Sports", "Channel 2"),
            Entry(null, "Channel 3"),
        };

        var result = PlaylistGroupFilter.Apply(entries, groupSelection: null);

        Assert.HasCount(3, result.Selected);
        Assert.AreEqual(2, result.KeptGroups["Sports"]);
        Assert.AreEqual(1, result.KeptGroups["(no group)"]);
        Assert.AreEqual(0, result.DroppedWithoutGroup);
        Assert.AreEqual(0, result.DroppedExcluded);
    }

    [TestMethod]
    public void Apply_WithSelection_DropsKnownExcludedGroupsAndMissingGroups()
    {
        var entries = new[]
        {
            Entry("Sports", "Channel 1"),
            Entry("News", "Channel 2"),
            Entry(null, "Channel 3"),
        };
        var selection = new GroupSelectionFile.GroupSelection(
            Keep: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Sports" },
            All: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Sports", "News" },
            PendingReview: []);

        var result = PlaylistGroupFilter.Apply(entries, selection);

        Assert.HasCount(1, result.Selected);
        Assert.AreEqual("Channel 1", result.Selected[0].Title);
        Assert.AreEqual(1, result.KeptGroups["Sports"]);
        Assert.AreEqual(1, result.DroppedWithoutGroup);
        Assert.AreEqual(1, result.DroppedExcluded);
    }

    [TestMethod]
    public void Apply_WithSelection_KeepsPendingReviewGroups()
    {
        var entries = new[]
        {
            Entry("Pending", "Channel 1"),
        };
        var selection = new GroupSelectionFile.GroupSelection(
            Keep: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pending" },
            All: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pending" },
            PendingReview: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pending" });

        var result = PlaylistGroupFilter.Apply(entries, selection);

        Assert.HasCount(1, result.Selected);
        Assert.AreEqual("Channel 1", result.Selected[0].Title);
        Assert.AreEqual(0, result.DroppedExcluded);
    }

    [TestMethod]
    public void Apply_WithSelection_AllowsUnknownNewGroups()
    {
        var entries = new[]
        {
            Entry("New Group", "Channel 1"),
        };
        var selection = new GroupSelectionFile.GroupSelection(
            Keep: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            All: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Known Group" },
            PendingReview: []);

        var result = PlaylistGroupFilter.Apply(entries, selection);

        Assert.HasCount(1, result.Selected);
        Assert.AreEqual("Channel 1", result.Selected[0].Title);
        Assert.AreEqual(0, result.DroppedExcluded);
    }

    [TestMethod]
    public void Apply_WithSelection_MatchesGroupsCaseInsensitively()
    {
        var entries = new[]
        {
            Entry("sports", "Channel 1"),
        };
        var selection = new GroupSelectionFile.GroupSelection(
            Keep: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SPORTS" },
            All: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SPORTS" },
            PendingReview: []);

        var result = PlaylistGroupFilter.Apply(entries, selection);

        Assert.HasCount(1, result.Selected);
        Assert.AreEqual(1, result.KeptGroups["SPORTS"]);
    }

    private static M3uEntry Entry(string? group, string title)
    {
        var metadata = group is null
            ? $"#EXTINF:-1,{title}"
            : $"#EXTINF:-1 group-title=\"{group}\",{title}";

        return new M3uEntry([metadata], $"http://example.com/{Uri.EscapeDataString(title)}");
    }
}
