using System;
using System.Collections.Generic;
using System.Linq;
using M3Undle.Cli;
using M3Undle.Cli.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.Commands;

[TestClass]
public class CommandOptionParserTests
{
    [TestMethod]
    public void Parse_EmptyArgs_ReturnsEmptyOptions()
    {
        var result = CommandOptionParser.Parse(Array.Empty<string>());
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void Parse_SingleFlag_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--verbose" });
        Assert.IsTrue(result.IsFlagSet("verbose"));
    }

    [TestMethod]
    public void Parse_MultipleFlags_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--verbose", "--live" });
        Assert.IsTrue(result.IsFlagSet("verbose"));
        Assert.IsTrue(result.IsFlagSet("live"));
    }

    [TestMethod]
    public void Parse_OptionWithValue_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--config", "/path/to/config.yml" });
        Assert.AreEqual("/path/to/config.yml", result.GetSingleValue("config"));
    }

    [TestMethod]
    public void Parse_OptionWithEqualsSign_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--config=/path/to/config.yml" });
        Assert.AreEqual("/path/to/config.yml", result.GetSingleValue("config"));
    }

    [TestMethod]
    public void Parse_OptionWithEqualsSignAndSpaces_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--config=/path/with spaces/config.yml" });
        Assert.AreEqual("/path/with spaces/config.yml", result.GetSingleValue("config"));
    }

    [TestMethod]
    public void Parse_MixedOptionsAndFlags_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] 
        { 
            "--config", "/path/config.yml",
            "--verbose",
            "--profile", "default",
            "--live"
        });
        
        Assert.AreEqual("/path/config.yml", result.GetSingleValue("config"));
        Assert.IsTrue(result.IsFlagSet("verbose"));
        Assert.AreEqual("default", result.GetSingleValue("profile"));
        Assert.IsTrue(result.IsFlagSet("live"));
    }

    [TestMethod]
    public void Parse_MultipleValuesForSameOption_StoresAll()
    {
        var result = CommandOptionParser.Parse(new[] 
        { 
            "--group", "News",
            "--group", "Sports",
            "--group", "Movies"
        });
        
        var values = result.GetValues("group").ToList();
        Assert.HasCount(3, values);
        CollectionAssert.Contains(values, "News");
        CollectionAssert.Contains(values, "Sports");
        CollectionAssert.Contains(values, "Movies");
    }

    [TestMethod]
    public void Parse_OptionWithEmptyValue_ParsesEmptyString()
    {
        var result = CommandOptionParser.Parse(new[] { "--key=" });
        Assert.AreEqual("", result.GetSingleValue("key"));
    }

    [TestMethod]
    public void Parse_CaseInsensitiveOptionNames_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--Config", "/path/config.yml" });
        Assert.AreEqual("/path/config.yml", result.GetSingleValue("config"));
        Assert.AreEqual("/path/config.yml", result.GetSingleValue("CONFIG"));
        Assert.AreEqual("/path/config.yml", result.GetSingleValue("CoNfIg"));
    }

    [TestMethod]
    public void Parse_UrlWithQueryString_ParsesCorrectly()
    {
        var url = "https://example.com/get.php?username=user&password=pass&type=m3u";
        var result = CommandOptionParser.Parse(new[] { "--playlist-url", url });
        Assert.AreEqual(url, result.GetSingleValue("playlist-url"));
    }

    [TestMethod]
    public void Parse_UrlWithEqualsSign_ParsesCorrectly()
    {
        var url = "https://example.com/get.php?username=user&password=pass&type=m3u";
        var result = CommandOptionParser.Parse(new[] { $"--playlist-url={url}" });
        Assert.AreEqual(url, result.GetSingleValue("playlist-url"));
    }

    [TestMethod]
    public void Parse_TokenWithoutDashes_ThrowsException()
    {
        Assert.Throws<CommandOptionException>(() => 
            CommandOptionParser.Parse(new[] { "invalid" }));
    }

    [TestMethod]
    public void Parse_TokenWithSingleDash_ThrowsException()
    {
        Assert.Throws<CommandOptionException>(() => 
     CommandOptionParser.Parse(new[] { "-v" }));
    }

[TestMethod]
public void Parse_EmptyOptionName_ThrowsException()
    {
      Assert.Throws<CommandOptionException>(() => 
            CommandOptionParser.Parse(new[] { "--", "value" }));
    }

    [TestMethod]
    public void Parse_EmptyOptionNameWithEquals_ThrowsException()
 {
        Assert.Throws<CommandOptionException>(() => 
            CommandOptionParser.Parse(new[] { "--=value" }));
    }

    [TestMethod]
    public void Parse_DashInStdout_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[] { "--out-playlist", "-" });
        Assert.AreEqual("-", result.GetSingleValue("out-playlist"));
    }

    [TestMethod]
    public void Parse_ComplexRealWorldScenario_ParsesCorrectly()
    {
        var result = CommandOptionParser.Parse(new[]
        {
            "--playlist-url", "https://provider.com/get.php?username=test&password=secret123",
            "--epg-url", "https://provider.com/epg.xml",
            "--groups-file", "/config/groups.txt",
            "--out-groups", "/data/groups.txt",
            "--out-playlist", "/data/playlist.m3u",
            "--out-epg", "/data/epg.xml",
            "--live",
            "--verbose"
        });

        Assert.Contains("username=test", result.GetSingleValue("playlist-url") ?? "");
        Assert.Contains("epg.xml", result.GetSingleValue("epg-url") ?? "");
        Assert.AreEqual("/config/groups.txt", result.GetSingleValue("groups-file"));
        Assert.AreEqual("/data/groups.txt", result.GetSingleValue("out-groups"));
        Assert.AreEqual("/data/playlist.m3u", result.GetSingleValue("out-playlist"));
        Assert.AreEqual("/data/epg.xml", result.GetSingleValue("out-epg"));
        Assert.IsTrue(result.IsFlagSet("live"));
        Assert.IsTrue(result.IsFlagSet("verbose"));
    }
}

