using System;
using System.Collections.Generic;
using System.Linq;
using M3Undle.Cli.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.Commands;

[TestClass]
public class CommandOptionSetTests
{
    [TestMethod]
    public void IsFlagSet_FlagPresent_ReturnsTrue()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["verbose"] = new List<string> { CommandOptionParser.FlagPresentValue }
        };
        var options = new CommandOptionSet(values);
        
        Assert.IsTrue(options.IsFlagSet("verbose"));
    }

    [TestMethod]
    public void IsFlagSet_FlagNotPresent_ReturnsFalse()
    {
        var values = new Dictionary<string, List<string>>();
        var options = new CommandOptionSet(values);
        
        Assert.IsFalse(options.IsFlagSet("verbose"));
    }

    [TestMethod]
    public void IsFlagSet_OptionWithValue_ReturnsFalse()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["config"] = new List<string> { "/path/config.yml" }
        };
        var options = new CommandOptionSet(values);
        
        Assert.IsFalse(options.IsFlagSet("config"));
    }

    [TestMethod]
    public void IsFlagSet_CaseInsensitive_ReturnsTrue()
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["verbose"] = new List<string> { CommandOptionParser.FlagPresentValue }
        };
        var options = new CommandOptionSet(values);
        
        Assert.IsTrue(options.IsFlagSet("VERBOSE"));
        Assert.IsTrue(options.IsFlagSet("Verbose"));
        Assert.IsTrue(options.IsFlagSet("verbose"));
    }

    [TestMethod]
    public void GetSingleValue_OptionExists_ReturnsValue()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["config"] = new List<string> { "/path/config.yml" }
        };
        var options = new CommandOptionSet(values);
        
        Assert.AreEqual("/path/config.yml", options.GetSingleValue("config"));
    }

    [TestMethod]
    public void GetSingleValue_OptionNotExists_ReturnsNull()
    {
        var values = new Dictionary<string, List<string>>();
        var options = new CommandOptionSet(values);
        
        Assert.IsNull(options.GetSingleValue("config"));
    }

    [TestMethod]
    public void GetSingleValue_MultipleValues_ReturnsLast()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["profile"] = new List<string> { "first", "second", "third" }
        };
        var options = new CommandOptionSet(values);
        
        Assert.AreEqual("third", options.GetSingleValue("profile"));
    }

    [TestMethod]
    public void GetSingleValue_FlagValue_ReturnsTrue()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["verbose"] = new List<string> { CommandOptionParser.FlagPresentValue }
        };
        var options = new CommandOptionSet(values);
        
        Assert.AreEqual("true", options.GetSingleValue("verbose"));
    }

    [TestMethod]
    public void GetSingleValue_EmptyString_ReturnsEmptyString()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["key"] = new List<string> { "" }
        };
        var options = new CommandOptionSet(values);
        
        Assert.AreEqual("", options.GetSingleValue("key"));
    }

    [TestMethod]
    public void GetValues_MultipleValues_ReturnsAll()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["group"] = new List<string> { "News", "Sports", "Movies" }
        };
        var options = new CommandOptionSet(values);
        
        var result = options.GetValues("group").ToList();
        Assert.HasCount(3, result);
        CollectionAssert.AreEqual(new[] { "News", "Sports", "Movies" }, result);
    }

    [TestMethod]
    public void GetValues_NoValues_ReturnsEmpty()
    {
        var values = new Dictionary<string, List<string>>();
        var options = new CommandOptionSet(values);
        
        var result = options.GetValues("group").ToList();
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void GetValues_FlagValues_ReturnsTrueStrings()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["flags"] = new List<string> 
            { 
                CommandOptionParser.FlagPresentValue,
                CommandOptionParser.FlagPresentValue 
            }
        };
        var options = new CommandOptionSet(values);
        
        var result = options.GetValues("flags").ToList();
        Assert.HasCount(2, result);
        Assert.IsTrue(result.All(v => v == "true"));
    }

    [TestMethod]
    public void GetValues_MixedFlagsAndValues_ReturnsConverted()
    {
        var values = new Dictionary<string, List<string>>
        {
            ["mixed"] = new List<string> 
            { 
                "value1",
                CommandOptionParser.FlagPresentValue,
                "value2"
            }
        };
        var options = new CommandOptionSet(values);
        
        var result = options.GetValues("mixed").ToList();
        CollectionAssert.AreEqual(new[] { "value1", "true", "value2" }, result);
    }
}

