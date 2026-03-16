using M3Undle.Web.Application.Epg;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Epg;

[TestClass]
public sealed class EpgChannelMapperTests
{
    // -------------------------------------------------------------------------
    // NormalizeName
    // -------------------------------------------------------------------------

    [TestMethod]
    public void NormalizeName_Null_ReturnsEmpty()
        => Assert.AreEqual(string.Empty, EpgChannelMapper.NormalizeName(null));

    [TestMethod]
    public void NormalizeName_Whitespace_ReturnsEmpty()
        => Assert.AreEqual(string.Empty, EpgChannelMapper.NormalizeName("   "));

    [TestMethod]
    public void NormalizeName_LowercasesInput()
        => Assert.AreEqual("cnn", EpgChannelMapper.NormalizeName("CNN"));

    [TestMethod]
    public void NormalizeName_StripsPunctuation()
        => Assert.AreEqual("cnn us", EpgChannelMapper.NormalizeName("CNN-US!"));

    [TestMethod]
    public void NormalizeName_CollapsesWhitespace()
        => Assert.AreEqual("cnn us", EpgChannelMapper.NormalizeName("CNN  US"));

    [TestMethod]
    public void NormalizeName_PreservesAlphanumeric()
        => Assert.AreEqual("espn2", EpgChannelMapper.NormalizeName("ESPN2"));

    // -------------------------------------------------------------------------
    // Tokenize
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Tokenize_SplitsOnSpaces()
    {
        var tokens = EpgChannelMapper.Tokenize("cnn us hd");
        Assert.AreEqual(3, tokens.Count);
    }

    [TestMethod]
    public void Tokenize_Null_ReturnsEmpty()
        => Assert.AreEqual(0, EpgChannelMapper.Tokenize(null).Count);

    [TestMethod]
    public void Tokenize_PunctuationOnly_ReturnsEmpty()
        => Assert.AreEqual(0, EpgChannelMapper.Tokenize("---!!!").Count);
}
