using M3Undle.Web.Application.Epg;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Epg;

[TestClass]
public sealed class XmltvParserTests
{
    private readonly XmltvParser _parser = new();

    private const string SampleXmltv = """
        <?xml version="1.0" encoding="utf-8"?>
        <tv generator-info-name="Test">
          <channel id="cnn.us">
            <display-name>CNN</display-name>
            <icon src="http://logos.com/cnn.png"/>
          </channel>
          <channel id="espn.us">
            <display-name>ESPN</display-name>
          </channel>
          <programme start="20260315120000 +0000" stop="20260315130000 +0000" channel="cnn.us">
            <title>Breaking News</title>
            <sub-title>World Events</sub-title>
            <desc>Latest updates.</desc>
            <category>News</category>
            <episode-num system="xmltv_ns">1.2.0/1</episode-num>
          </programme>
          <programme start="20260315130000 +0000" stop="20260315140000 +0000" channel="cnn.us">
            <title>Midday Report</title>
          </programme>
          <programme start="20260315120000 +0000" stop="20260315130000 +0000" channel="espn.us">
            <title>SportsCenter</title>
            <category>Sports</category>
          </programme>
        </tv>
        """;

    // -------------------------------------------------------------------------
    // Channel parsing
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Parse_ExtractsChannelCount()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        Assert.HasCount(2, catalogue.Channels);
    }

    [TestMethod]
    public void Parse_ExtractsChannelId()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        Assert.IsTrue(catalogue.Channels.Any(c => c.XmltvChannelId == "cnn.us"));
    }

    [TestMethod]
    public void Parse_ExtractsDisplayName()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var cnn = catalogue.Channels.First(c => c.XmltvChannelId == "cnn.us");
        Assert.AreEqual("CNN", cnn.DisplayName);
    }

    [TestMethod]
    public void Parse_ExtractsIconUrl()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var cnn = catalogue.Channels.First(c => c.XmltvChannelId == "cnn.us");
        Assert.AreEqual("http://logos.com/cnn.png", cnn.IconUrl);
    }

    [TestMethod]
    public void Parse_ChannelWithoutIcon_IconUrlIsNull()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var espn = catalogue.Channels.First(c => c.XmltvChannelId == "espn.us");
        Assert.IsNull(espn.IconUrl);
    }

    // -------------------------------------------------------------------------
    // Programme parsing
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Parse_ExtractsProgrammesForCnn()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        Assert.IsTrue(catalogue.ProgrammesByChannel.ContainsKey("cnn.us"));
        Assert.HasCount(2, catalogue.ProgrammesByChannel["cnn.us"]);
    }

    [TestMethod]
    public void Parse_ProgrammeTitle()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var first = catalogue.ProgrammesByChannel["cnn.us"][0];
        Assert.AreEqual("Breaking News", first.Title);
    }

    [TestMethod]
    public void Parse_ProgrammeSubTitle()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var first = catalogue.ProgrammesByChannel["cnn.us"][0];
        Assert.AreEqual("World Events", first.SubTitle);
    }

    [TestMethod]
    public void Parse_ProgrammeDescription()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var first = catalogue.ProgrammesByChannel["cnn.us"][0];
        Assert.AreEqual("Latest updates.", first.Description);
    }

    [TestMethod]
    public void Parse_ProgrammeCategory()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var first = catalogue.ProgrammesByChannel["cnn.us"][0];
        Assert.HasCount(1, first.Categories);
        Assert.AreEqual("News", first.Categories[0]);
    }

    [TestMethod]
    public void Parse_ProgrammeEpisodeNum()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var first = catalogue.ProgrammesByChannel["cnn.us"][0];
        Assert.HasCount(1, first.EpisodeNums);
    }

    [TestMethod]
    public void Parse_ProgrammesSortedByStartTime()
    {
        var catalogue = _parser.Parse("src1", SampleXmltv);
        var progs = catalogue.ProgrammesByChannel["cnn.us"];
        Assert.IsTrue(progs[0].StartUtc <= progs[1].StartUtc);
    }

    // -------------------------------------------------------------------------
    // Timestamp parsing
    // -------------------------------------------------------------------------

    [TestMethod]
    public void TryParseXmltvTimestamp_UtcOffset_ParsesCorrectly()
    {
        var ok = XmltvParser.TryParseXmltvTimestamp("20260315120000 +0000", out var result);
        Assert.IsTrue(ok);
        Assert.AreEqual(2026, result.Year);
        Assert.AreEqual(3, result.Month);
        Assert.AreEqual(15, result.Day);
        Assert.AreEqual(12, result.Hour);
        Assert.AreEqual(TimeSpan.Zero, result.Offset);
    }

    [TestMethod]
    public void TryParseXmltvTimestamp_PositiveOffset_ConvertsToUtc()
    {
        var ok = XmltvParser.TryParseXmltvTimestamp("20260315120000 +0500", out var result);
        Assert.IsTrue(ok);
        // 12:00 +05:00 = 07:00 UTC
        Assert.AreEqual(7, result.UtcDateTime.Hour);
    }

    [TestMethod]
    public void TryParseXmltvTimestamp_NegativeOffset_ConvertsToUtc()
    {
        var ok = XmltvParser.TryParseXmltvTimestamp("20260315120000 -0500", out var result);
        Assert.IsTrue(ok);
        // 12:00 -05:00 = 17:00 UTC
        Assert.AreEqual(17, result.UtcDateTime.Hour);
    }

    [TestMethod]
    public void TryParseXmltvTimestamp_NoOffset_TreatedAsUtc()
    {
        var ok = XmltvParser.TryParseXmltvTimestamp("20260315120000", out var result);
        Assert.IsTrue(ok);
        Assert.AreEqual(12, result.UtcDateTime.Hour);
    }

    [TestMethod]
    public void TryParseXmltvTimestamp_Empty_ReturnsFalse()
    {
        Assert.IsFalse(XmltvParser.TryParseXmltvTimestamp("", out _));
    }

    [TestMethod]
    public void TryParseXmltvTimestamp_InvalidFormat_ReturnsFalse()
    {
        Assert.IsFalse(XmltvParser.TryParseXmltvTimestamp("not-a-date", out _));
    }

    // -------------------------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Parse_EmptyString_ReturnsEmptyCatalogue()
    {
        var catalogue = _parser.Parse("src1", "");
        Assert.IsEmpty(catalogue.Channels);
        Assert.IsEmpty(catalogue.ProgrammesByChannel);
    }

    [TestMethod]
    public void Parse_MalformedXml_ReturnsPartialOrEmpty()
    {
        // Should not throw
        var catalogue = _parser.Parse("src1", "<tv><channel id=\"x\"><display-name>X</display><<INVALID");
        Assert.IsNotNull(catalogue);
    }

    [TestMethod]
    public void Parse_ProgrammeMissingStop_Skipped()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <tv>
              <channel id="ch1"><display-name>Ch1</display-name></channel>
              <programme start="20260315120000 +0000" channel="ch1">
                <title>No Stop</title>
              </programme>
            </tv>
            """;

        var catalogue = _parser.Parse("src1", xml);
        Assert.AreEqual(0, catalogue.ProgrammesByChannel.GetValueOrDefault("ch1")?.Count ?? 0);
    }

    [TestMethod]
    public void Parse_SetsSourceId()
    {
        var catalogue = _parser.Parse("my-source-id", SampleXmltv);
        Assert.AreEqual("my-source-id", catalogue.SourceId);
        Assert.IsTrue(catalogue.Channels.All(c => c.SourceId == "my-source-id"));
        Assert.IsTrue(catalogue.ProgrammesByChannel.Values
            .SelectMany(p => p)
            .All(p => p.SourceId == "my-source-id"));
    }
}
