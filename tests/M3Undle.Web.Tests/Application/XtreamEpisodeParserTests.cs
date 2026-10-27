using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class XtreamEpisodeParserTests
{
    [TestMethod]
    public void Parse_EpisodesKeyedBySeason_ReturnsEpisodesWithSeasonKeyMarkers()
    {
        const string json = """
            {"episodes":{"1":[{"id":"10","episode_num":1,"title":"System","container_extension":"mkv"}],
                         "2":[{"id":"20","episode_num":3,"title":"Pop","container_extension":"mp4"}]}}
            """;

        var episodes = XtreamEpisodeParser.Parse("The Bear", json);

        Assert.AreEqual(2, episodes.Count);
        Assert.AreEqual("The Bear S01E01 — System", episodes[0].DisplayName);
        Assert.AreEqual("The Bear S02E03 — Pop", episodes[1].DisplayName);
        Assert.AreEqual("mp4", episodes[1].Extension);
    }

    [TestMethod]
    public void Parse_EpisodesAsArrayOfSeasons_UsesArrayIndexAsSeasonNumber()
    {
        // PHP encodes a map keyed exactly 0..n-1 as a JSON array (seasons 0-1 here).
        const string json = """
            {"episodes":[[{"id":"5","episode_num":1,"title":"Gary","container_extension":"mkv"}],
                         [{"id":"6","episode_num":1,"title":"System","container_extension":"mkv"},
                          {"id":"7","episode_num":2,"title":"Hands","container_extension":"mkv"}]]}
            """;

        var episodes = XtreamEpisodeParser.Parse("The Bear", json);

        Assert.AreEqual(3, episodes.Count);
        Assert.AreEqual("The Bear S00E01 — Gary", episodes[0].DisplayName);
        Assert.AreEqual("The Bear S01E02 — Hands", episodes[2].DisplayName);
        Assert.AreEqual(7, episodes[2].EpisodeId);
    }

    [TestMethod]
    public void Parse_EmptyOrMalformedEpisodes_ReturnsEmpty()
    {
        Assert.AreEqual(0, XtreamEpisodeParser.Parse("X", """{"episodes":[]}""").Count);
        Assert.AreEqual(0, XtreamEpisodeParser.Parse("X", """{"episodes":"nope"}""").Count);
        Assert.AreEqual(0, XtreamEpisodeParser.Parse("X", "not json").Count);
    }
}
