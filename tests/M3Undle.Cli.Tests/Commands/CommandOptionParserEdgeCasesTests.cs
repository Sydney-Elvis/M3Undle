using System;
using System.Collections.Generic;
using System.Linq;
using M3Undle.Cli.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.Commands;

[TestClass]
public class CommandOptionParserEdgeCasesTests
{
    [TestMethod]
    public void Parse_SpecialCharactersInValue_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--password", "p@ssw0rd!#$%^&*()" });
        Assert.AreEqual("p@ssw0rd!#$%^&*()", result.GetSingleValue("password"));
    }

    [TestMethod]
    public void Parse_UnicodeInValue_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--name", "???" });
        Assert.AreEqual("???", result.GetSingleValue("name"));
    }

    [TestMethod]
    public void Parse_VeryLongValue_ParsesCorrectly()
    {
        var longValue = new string('x', 10000);
        var result = CommandOptionParser.Parse(new[] { "--data", longValue });
        Assert.AreEqual(longValue, result.GetSingleValue("data"));
    }

    [TestMethod]
    public void Parse_PathWithBackslashes_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--path", @"C:\Users\Test\config.yml" });
        Assert.AreEqual(@"C:\Users\Test\config.yml", result.GetSingleValue("path"));
    }

    [TestMethod]
    public void Parse_UrlWithFragments_ParsesCorrectly()
    {
        var url = "https://example.com/path#fragment?query=value";
        var result = CommandOptionParser.Parse(new[] { "--url", url });
        Assert.AreEqual(url, result.GetSingleValue("url"));
    }

    [TestMethod]
    public void Parse_JsonStringAsValue_ParsesCorrectly()
    {
        var json = "{\"key\":\"value\",\"nested\":{\"data\":123}}";
        var result = CommandOptionParser.Parse(new[] { "--json", json });
        Assert.AreEqual(json, result.GetSingleValue("json"));
    }

    [TestMethod]
    public void Parse_ValueWithMultipleEqualsSign_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--data=key=value=extra" });
        Assert.AreEqual("key=value=extra", result.GetSingleValue("data"));
    }

    [TestMethod]
    public void Parse_EmptyValueWithSpaceAfter_TreatsAsFlag()
    {
        var result = CommandOptionParser.Parse(new[] { "--flag", "--another" });
        Assert.IsTrue(result.IsFlagSet("flag"));
        Assert.IsTrue(result.IsFlagSet("another"));
    }

    [TestMethod]
    public void Parse_OptionNameWithNumbers_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--retry-count", "3" });
        Assert.AreEqual("3", result.GetSingleValue("retry-count"));
    }

    [TestMethod]
    public void Parse_OptionNameWithUnderscores_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--max_download_mb", "500" });
        Assert.AreEqual("500", result.GetSingleValue("max_download_mb"));
    }

    [TestMethod]
    public void Parse_QuotedValueNotStrippedByShell_ParsesWithQuotes()
    {
        // Note: Shell would normally strip quotes, but if they're passed through
        var result = CommandOptionParser.Parse(new[] { "--value", "\"quoted\"" });
        Assert.AreEqual("\"quoted\"", result.GetSingleValue("value"));
    }

    [TestMethod]
    public void Parse_WhitespaceOnlyValue_ParsesAsWhitespace()
    {
        var result = CommandOptionParser.Parse(new[] { "--space", "   " });
        Assert.AreEqual("   ", result.GetSingleValue("space"));
    }

    [TestMethod]
    public void Parse_NegativeNumber_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--offset", "-100" });
        Assert.AreEqual("-100", result.GetSingleValue("offset"));
    }

    [TestMethod]
    public void Parse_BooleanLikeValues_ParsesAsStrings()
    {
        var result = CommandOptionParser.Parse(new[] 
        { 
            "--bool1", "true",
            "--bool2", "false",
            "--bool3", "TRUE",
            "--bool4", "False"
        });
        
        Assert.AreEqual("true", result.GetSingleValue("bool1"));
        Assert.AreEqual("false", result.GetSingleValue("bool2"));
        Assert.AreEqual("TRUE", result.GetSingleValue("bool3"));
        Assert.AreEqual("False", result.GetSingleValue("bool4"));
    }

    [TestMethod]
    public void Parse_OnlyDashes_ThrowsException()
    {
        Assert.Throws<CommandOptionException>(() => 
              CommandOptionParser.Parse(new[] { "--" }));
    }

    [TestMethod]
    public void Parse_OptionStartingWithNumber_IsValid()
    {
        // Option names starting with numbers are actually valid
        var result = CommandOptionParser.Parse(new[] { "--123", "value" });
        Assert.AreEqual("value", result.GetSingleValue("123"));
    }

    [TestMethod]
    public void Parse_RepeatFlagMultipleTimes_OnlyLastCounts()
    {
        var result = CommandOptionParser.Parse(new[] { "--verbose", "--verbose", "--verbose" });
        Assert.IsTrue(result.IsFlagSet("verbose"));
        var values = result.GetValues("verbose").ToList();
        Assert.HasCount(3, values);
        Assert.IsTrue(values.All(v => v == "true"));
    }

    [TestMethod]
    public void Parse_OverrideValueMultipleTimes_LastWins()
    {
        var result = CommandOptionParser.Parse(new[] 
        { 
            "--profile", "dev",
            "--profile", "staging",
            "--profile", "prod"
        });
        
        Assert.AreEqual("prod", result.GetSingleValue("profile"));
    }

    [TestMethod]
    public void Parse_ZeroValue_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--timeout", "0" });
        Assert.AreEqual("0", result.GetSingleValue("timeout"));
    }

    [TestMethod]
    public void Parse_IpAddressAsValue_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--host", "192.168.1.1" });
        Assert.AreEqual("192.168.1.1", result.GetSingleValue("host"));
    }

    [TestMethod]
    public void Parse_IpV6AddressAsValue_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--host", "2001:0db8:85a3:0000:0000:8a2e:0370:7334" });
        Assert.AreEqual("2001:0db8:85a3:0000:0000:8a2e:0370:7334", result.GetSingleValue("host"));
    }

    [TestMethod]
    public void Parse_FileProtocolUrl_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--input", "file:///C:/Users/test/playlist.m3u" });
        Assert.AreEqual("file:///C:/Users/test/playlist.m3u", result.GetSingleValue("input"));
    }
}

