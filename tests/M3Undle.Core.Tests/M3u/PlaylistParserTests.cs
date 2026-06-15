using M3Undle.Core.M3u;

namespace M3Undle.Core.Tests.M3u;

[TestClass]
public sealed class PlaylistParserTests
{
    private readonly PlaylistParser _parser = new();

    [TestMethod]
    public void Parse_StandardEntry_ParsesCorrectly()
    {
        const string content = """
            #EXTM3U
            #EXTINF:-1 tvg-id="cnn.us" group-title="News",CNN
            https://example.com/stream/cnn
            """;

        var doc = _parser.Parse(content);

        Assert.AreEqual(1, doc.Entries.Count);
        Assert.AreEqual("CNN", doc.Entries[0].Title);
        Assert.AreEqual("https://example.com/stream/cnn", doc.Entries[0].Url);
    }

    [TestMethod]
    public void Parse_EntryWithExtvlcopt_ParsesUrlCorrectly()
    {
        const string content = """
            #EXTM3U
            #EXTINF:-1 tvg-id="pbs.us" group-title="US",PBS
            #EXTVLCOPT:http-user-agent=Mozilla/5.0
            https://example.com/stream/pbs.m3u8
            """;

        var doc = _parser.Parse(content);

        Assert.AreEqual(1, doc.Entries.Count);
        Assert.AreEqual("https://example.com/stream/pbs.m3u8", doc.Entries[0].Url);
    }

    [TestMethod]
    public void Parse_EntryMissingUrl_DoesNotConsumeNextExtinf()
    {
        // This reproduces the cascading-drop bug where a channel with no URL
        // causes the parser to consume the next #EXTINF as the URL, losing
        // the following channel entirely.
        const string content = """
            #EXTM3U
            #EXTINF:-1 tvg-id="ket2.us" group-title="US",PBS KET2
            #EXTINF:-1 tvg-id="ket.us" group-title="US",PBS KET KY
            https://example.com/stream/ket.m3u8
            """;

        var doc = _parser.Parse(content);

        // PBS KET2 has no URL → entry exists but Url is null/empty
        // PBS KET KY must still be parsed correctly
        Assert.AreEqual(2, doc.Entries.Count);

        var ket2 = doc.Entries[0];
        var ketKy = doc.Entries[1];

        Assert.AreEqual("PBS KET2", ket2.Title);
        Assert.IsTrue(string.IsNullOrEmpty(ket2.Url), "KET2 should have no URL");

        Assert.AreEqual("PBS KET KY", ketKy.Title);
        Assert.AreEqual("https://example.com/stream/ket.m3u8", ketKy.Url);
    }

    [TestMethod]
    public void Parse_MultipleEntriesNoUrls_AllParsedWithoutCascade()
    {
        const string content = """
            #EXTM3U
            #EXTINF:-1 group-title="US",Channel A
            #EXTINF:-1 group-title="US",Channel B
            #EXTINF:-1 group-title="US",Channel C
            https://example.com/stream/c.m3u8
            """;

        var doc = _parser.Parse(content);

        Assert.AreEqual(3, doc.Entries.Count);
        Assert.IsTrue(string.IsNullOrEmpty(doc.Entries[0].Url));
        Assert.IsTrue(string.IsNullOrEmpty(doc.Entries[1].Url));
        Assert.AreEqual("https://example.com/stream/c.m3u8", doc.Entries[2].Url);
    }

    [TestMethod]
    public void Parse_BlankLineBetweenEntries_ParsesCorrectly()
    {
        const string content =
            "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"US\",Channel A\n" +
            "https://example.com/a.m3u8\n" +
            "\n" +
            "#EXTINF:-1 group-title=\"US\",Channel B\n" +
            "https://example.com/b.m3u8\n";

        var doc = _parser.Parse(content);

        Assert.AreEqual(2, doc.Entries.Count);
        Assert.AreEqual("https://example.com/a.m3u8", doc.Entries[0].Url);
        Assert.AreEqual("https://example.com/b.m3u8", doc.Entries[1].Url);
    }
}
