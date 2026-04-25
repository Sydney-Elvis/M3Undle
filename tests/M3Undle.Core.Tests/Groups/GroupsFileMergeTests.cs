using M3Undle.Core.Groups;

namespace M3Undle.Core.Tests.Groups;

[TestClass]
public sealed class GroupsFileMergeTests
{
    [TestMethod]
    public void Merge_AddsOnlyNewGroupsAsPendingReview()
    {
        var existingLines = new[]
        {
            "######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######",
            "######  Created with bndl version 1.0.0 ######",
            string.Empty,
            "#Sports",
            "News",
            "Entertainment",
        };

        var result = GroupsFileMerge.Merge(existingLines, ["Sports", "News", "Movies"], "1.2.3");

        CollectionAssert.Contains(result.OutputLines.ToList(), "#Sports");
        CollectionAssert.Contains(result.OutputLines.ToList(), "News");
        CollectionAssert.Contains(result.OutputLines.ToList(), "Entertainment");
        CollectionAssert.Contains(result.OutputLines.ToList(), "##Movies");
        CollectionAssert.AreEquivalent(new[] { "Movies" }, result.NewGroups.ToList());
    }

    [TestMethod]
    public void Merge_MatchesExistingGroupsCaseInsensitively()
    {
        var existingLines = new[]
        {
            "######  Created with bndl version 1.0.0 ######",
            string.Empty,
            "#SPORTS",
        };

        var result = GroupsFileMerge.Merge(existingLines, ["sports", "Sports"], "1.2.3");

        Assert.HasCount(0, result.NewGroups);
        Assert.IsFalse(result.OutputLines.Any(line => line.Equals("##sports", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Merge_PreservesBlankLines()
    {
        var existingLines = new[]
        {
            "######  Created with bndl version 1.0.0 ######",
            string.Empty,
            "#Sports",
            string.Empty,
            "News",
            string.Empty,
        };

        var result = GroupsFileMerge.Merge(existingLines, ["Sports", "Movies"], "1.2.3");

        Assert.IsTrue(result.OutputLines.Any(string.IsNullOrWhiteSpace));
        CollectionAssert.Contains(result.OutputLines.ToList(), "##Movies");
    }

    [TestMethod]
    public void Merge_UpdatesVersionLineToCurrentVersion()
    {
        var existingLines = new[]
        {
            "######  Created with bndl version 1.0.0 ######",
            string.Empty,
            "#Sports",
        };

        var result = GroupsFileMerge.Merge(existingLines, ["Sports"], "1.2.3");
        var versionLine = result.OutputLines.Single(line => line.Contains("Created with bndl version"));

        StringAssert.Contains(versionLine, "version 1.2.3");
        Assert.AreEqual(88, versionLine.Length);
    }

    [TestMethod]
    public void Merge_RecognisesExistingGroups_WhenFileHasNoHeader()
    {
        var existingLines = new[]
        {
            "#Sports",
            "News",
        };

        var result = GroupsFileMerge.Merge(existingLines, ["Sports", "News", "Movies"], "1.2.3");

        CollectionAssert.Contains(result.OutputLines.ToList(), "#Sports");
        CollectionAssert.Contains(result.OutputLines.ToList(), "News");
        CollectionAssert.Contains(result.OutputLines.ToList(), "##Movies");
        CollectionAssert.AreEquivalent(new[] { "Movies" }, result.NewGroups.ToList());
    }

    [TestMethod]
    public void Merge_InsertsVersionLineAfterHeader_WhenMissing()
    {
        var existingLines = new[]
        {
            "######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######",
            "######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######",
            string.Empty,
            "#Sports",
        };

        var result = GroupsFileMerge.Merge(existingLines, ["Sports"], "1.2.3");
        var versionLine = result.OutputLines.Single(line => line.Contains("Created with bndl version"));
        var lines = result.OutputLines.ToList();

        StringAssert.Contains(versionLine, "version 1.2.3");
        Assert.IsLessThan(lines.IndexOf("#Sports"), lines.IndexOf(versionLine));
    }
}
