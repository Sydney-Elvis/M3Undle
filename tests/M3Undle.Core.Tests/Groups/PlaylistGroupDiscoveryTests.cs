using M3Undle.Core.Groups;
using M3Undle.Core.M3u;

namespace M3Undle.Core.Tests.Groups;

[TestClass]
public sealed class PlaylistGroupDiscoveryTests
{
    [TestMethod]
    public void Discover_ReturnsDistinctSortedGroups()
    {
        var entries = new[]
        {
            Entry("Sports"),
            Entry("news"),
            Entry("SPORTS"),
            Entry(null),
            Entry("Movies"),
        };

        var groups = PlaylistGroupDiscovery.Discover(entries).ToList();

        CollectionAssert.AreEqual(new[] { "Movies", "news", "Sports" }, groups);
    }

    [TestMethod]
    public void Discover_IgnoresEntriesWithoutGroups()
    {
        var entries = new[]
        {
            Entry(null),
            Entry(string.Empty),
        };

        var groups = PlaylistGroupDiscovery.Discover(entries);

        Assert.HasCount(0, groups);
    }

    private static M3uEntry Entry(string? group)
    {
        var metadata = group is null
            ? "#EXTINF:-1,Channel"
            : $"#EXTINF:-1 group-title=\"{group}\",Channel";

        return new M3uEntry([metadata], "http://example.com/stream.ts");
    }
}
