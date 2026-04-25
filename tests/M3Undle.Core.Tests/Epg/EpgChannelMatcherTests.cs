using M3Undle.Core.Epg;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Core.Tests.Epg;

[TestClass]
public sealed class EpgChannelMatcherTests
{
    // -------------------------------------------------------------------------
    // NormalizeName
    // -------------------------------------------------------------------------

    [TestMethod]
    public void NormalizeName_Null_ReturnsEmpty()
        => Assert.AreEqual(string.Empty, EpgChannelMatcher.NormalizeName(null));

    [TestMethod]
    public void NormalizeName_Whitespace_ReturnsEmpty()
        => Assert.AreEqual(string.Empty, EpgChannelMatcher.NormalizeName("   "));

    [TestMethod]
    public void NormalizeName_LowercasesInput()
        => Assert.AreEqual("cnn", EpgChannelMatcher.NormalizeName("CNN"));

    [TestMethod]
    public void NormalizeName_StripsPunctuation()
        => Assert.AreEqual("cnn us", EpgChannelMatcher.NormalizeName("CNN-US!"));

    [TestMethod]
    public void NormalizeName_CollapsesWhitespace()
        => Assert.AreEqual("cnn us", EpgChannelMatcher.NormalizeName("CNN  US"));

    [TestMethod]
    public void NormalizeName_PreservesAlphanumeric()
        => Assert.AreEqual("espn2", EpgChannelMatcher.NormalizeName("ESPN2"));

    // -------------------------------------------------------------------------
    // Tokenize
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Tokenize_SplitsOnSpaces()
    {
        var tokens = EpgChannelMatcher.Tokenize("cnn us hd");
        Assert.HasCount(3, tokens);
    }

    [TestMethod]
    public void Tokenize_Null_ReturnsEmpty()
        => Assert.IsEmpty(EpgChannelMatcher.Tokenize(null));

    [TestMethod]
    public void Tokenize_PunctuationOnly_ReturnsEmpty()
        => Assert.IsEmpty(EpgChannelMatcher.Tokenize("---!!!"));

    // -------------------------------------------------------------------------
    // FindBestMatch
    // -------------------------------------------------------------------------

    [TestMethod]
    public void FindBestMatch_ExactTvgId_ReturnsAutoIdMatch()
    {
        var match = EpgChannelMatcher.FindBestMatch(
            new EpgChannelMatchCandidate("Something Else", "cnn.us", null),
            [new EpgChannelRecord("source-1", "cnn.us", "CNN", null)]);

        Assert.IsNotNull(match);
        Assert.AreEqual("cnn.us", match.Channel.XmltvChannelId);
        Assert.AreEqual("auto_id", match.Mode);
        Assert.AreEqual(1.0f, match.Confidence);
    }

    [TestMethod]
    public void FindBestMatch_NormalizedDisplayName_ReturnsAutoNameMatch()
    {
        var match = EpgChannelMatcher.FindBestMatch(
            new EpgChannelMatchCandidate("CNN-US!", null, null),
            [new EpgChannelRecord("source-1", "cnn.us", "CNN US", null)]);

        Assert.IsNotNull(match);
        Assert.AreEqual("cnn.us", match.Channel.XmltvChannelId);
        Assert.AreEqual("auto_name", match.Mode);
        Assert.AreEqual(0.9f, match.Confidence);
    }

    [TestMethod]
    public void FindBestMatch_NormalizedTvgName_ReturnsAutoNameMatch()
    {
        var match = EpgChannelMatcher.FindBestMatch(
            new EpgChannelMatchCandidate("Unrelated", null, "CNN-US!"),
            [new EpgChannelRecord("source-1", "cnn.us", "CNN US", null)]);

        Assert.IsNotNull(match);
        Assert.AreEqual("cnn.us", match.Channel.XmltvChannelId);
        Assert.AreEqual("auto_name", match.Mode);
        Assert.AreEqual(0.9f, match.Confidence);
    }

    [TestMethod]
    public void FindBestMatch_FuzzyTokenOverlap_ReturnsAutoFuzzyMatch()
    {
        var match = EpgChannelMatcher.FindBestMatch(
            new EpgChannelMatchCandidate("ESPN Deportes HD", null, null),
            [new EpgChannelRecord("source-1", "espn.deportes", "ESPN Deportes", null)]);

        Assert.IsNotNull(match);
        Assert.AreEqual("espn.deportes", match.Channel.XmltvChannelId);
        Assert.AreEqual("auto_fuzzy", match.Mode);
        Assert.AreEqual(2f / 3f, match.Confidence, 0.0001f);
    }

    [TestMethod]
    public void FindBestMatch_NoMatch_ReturnsNull()
    {
        var match = EpgChannelMatcher.FindBestMatch(
            new EpgChannelMatchCandidate("CNN", null, null),
            [new EpgChannelRecord("source-1", "espn.us", "ESPN", null)]);

        Assert.IsNull(match);
    }
}
