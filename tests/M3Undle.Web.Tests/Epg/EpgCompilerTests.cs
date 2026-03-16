using M3Undle.Web.Application.Epg;
using M3Undle.Web.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Epg;

[TestClass]
public sealed class EpgCompilerTests
{
    private readonly EpgCompiler _compiler = new(NullLogger<EpgCompiler>.Instance);
    private readonly XmltvParser _parser = new();

    private const string Source1Id = "source-1";
    private const string Source2Id = "source-2";

    private static EpgSource MakeSource(string id, int priority, string name = "")
        => new() { EpgSourceId = id, Priority = priority, Name = string.IsNullOrEmpty(name) ? id : name, Enabled = true };

    private static OutputChannel MakeChannel(string providerChannelId, string tvgId, string displayName)
        => new(providerChannelId, tvgId, displayName, null);

    private static EpgChannelMapping MakeMapping(string profileId, string providerChannelId, string sourceId, string xmltvChannelId, string mode = "auto_id")
        => new()
        {
            EpgChannelMappingId = Guid.NewGuid().ToString(),
            ProfileId = profileId,
            ProviderChannelId = providerChannelId,
            EpgSourceId = sourceId,
            XmltvChannelId = xmltvChannelId,
            MappingMode = mode,
            Confidence = 1.0f,
        };

    private static EpgCatalogue MakeCatalogue(string sourceId, string channelId, DateTimeOffset start, DateTimeOffset stop, string title)
    {
        var ch = new EpgChannelRecord(sourceId, channelId, channelId, null);
        var prog = new EpgProgrammeRecord(sourceId, channelId, start, stop, title, null, null, [], [], null);
        return new EpgCatalogue(sourceId, [ch], new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>
        {
            [channelId] = [prog]
        });
    }

    // -------------------------------------------------------------------------
    // Basic compile
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Compile_SingleSource_ProducesChannelAndProgramme()
    {
        var now = DateTimeOffset.UtcNow;
        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue>
        {
            [Source1Id] = MakeCatalogue(Source1Id, "cnn.us", now, now.AddHours(1), "Breaking News")
        };
        var outputChannels = new List<OutputChannel> { MakeChannel("pc-1", "cnn.us", "CNN") };
        var mappings = new List<EpgChannelMapping>
        {
            MakeMapping("profile-1", "pc-1", Source1Id, "cnn.us")
        }.ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (xml, report) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.IsTrue(xml.Contains("<channel id=\"cnn.us\">"), "Should contain channel element");
        Assert.IsTrue(xml.Contains("<programme"), "Should contain programme element");
        Assert.IsTrue(xml.Contains("Breaking News"), "Should contain programme title");
        Assert.AreEqual(1, report.TotalProgrammes);
        Assert.AreEqual(1, report.MappedChannels);
    }

    [TestMethod]
    public void Compile_ChannelIdMatchesTvgId()
    {
        var now = DateTimeOffset.UtcNow;
        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue>
        {
            [Source1Id] = MakeCatalogue(Source1Id, "espn.us", now, now.AddHours(1), "SportsCenter")
        };
        var outputChannels = new List<OutputChannel> { MakeChannel("pc-2", "espn.us", "ESPN") };
        var mappings = new List<EpgChannelMapping>
        {
            MakeMapping("profile-1", "pc-2", Source1Id, "espn.us")
        }.ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (xml, _) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        // Programme channel attribute must use the lineup tvg-id, not the source channel id
        Assert.IsTrue(xml.Contains("channel=\"espn.us\""));
    }

    [TestMethod]
    public void Compile_NoMappings_ProducesChannelNoProgammes()
    {
        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue>
        {
            [Source1Id] = EpgCatalogue.Empty(Source1Id)
        };
        var outputChannels = new List<OutputChannel> { MakeChannel("pc-3", "fox.us", "FOX") };
        var mappings = Enumerable.Empty<EpgChannelMapping>()
            .ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (xml, report) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.IsTrue(xml.Contains("<channel id=\"fox.us\">"));
        Assert.AreEqual(0, report.TotalProgrammes);
        Assert.AreEqual(0, report.MappedChannels);
    }

    // -------------------------------------------------------------------------
    // Priority selection
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Compile_TwoSources_HigherPriorityWins()
    {
        var now = DateTimeOffset.UtcNow;
        var sources = new List<EpgSource>
        {
            MakeSource(Source1Id, 1, "Primary"),    // higher priority
            MakeSource(Source2Id, 2, "Secondary"),  // lower priority
        };

        var catalogues = new Dictionary<string, EpgCatalogue>
        {
            [Source1Id] = MakeCatalogue(Source1Id, "cnn.us", now, now.AddHours(1), "Primary Title"),
            [Source2Id] = MakeCatalogue(Source2Id, "cnn.us", now, now.AddHours(1), "Secondary Title"),
        };

        var outputChannels = new List<OutputChannel> { MakeChannel("pc-1", "cnn.us", "CNN") };
        var mappings = new List<EpgChannelMapping>
        {
            MakeMapping("p", "pc-1", Source1Id, "cnn.us"),
            MakeMapping("p", "pc-1", Source2Id, "cnn.us"),
        }.ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (xml, report) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.IsTrue(xml.Contains("Primary Title"), "Higher-priority source title should appear");
        Assert.IsFalse(xml.Contains("Secondary Title"), "Lower-priority source title should not appear");
    }

    // -------------------------------------------------------------------------
    // Deduplication
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Compile_DuplicateProgrammes_Deduplicated()
    {
        var now = DateTimeOffset.UtcNow;
        var prog = new EpgProgrammeRecord(Source1Id, "ch.us", now, now.AddHours(1), "Same Show", null, null, [], [], null);
        var catalogue = new EpgCatalogue(Source1Id, [new EpgChannelRecord(Source1Id, "ch.us", "CH", null)],
            new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>
            {
                ["ch.us"] = [prog, prog] // exact duplicate
            });

        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue> { [Source1Id] = catalogue };
        var outputChannels = new List<OutputChannel> { MakeChannel("pc-1", "ch.us", "CH") };
        var mappings = new List<EpgChannelMapping>
        {
            MakeMapping("p", "pc-1", Source1Id, "ch.us")
        }.ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (_, report) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.AreEqual(1, report.TotalProgrammes, "Duplicate programme should be collapsed to one");
    }

    // -------------------------------------------------------------------------
    // Output XML structure
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Compile_OutputXml_HasUtf8Declaration()
    {
        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue> { [Source1Id] = EpgCatalogue.Empty(Source1Id) };
        var outputChannels = new List<OutputChannel> { MakeChannel("pc-1", "ch.us", "CH") };
        var mappings = Enumerable.Empty<EpgChannelMapping>()
            .ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (xml, _) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.IsTrue(xml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"), "Should have UTF-8 declaration");
    }

    [TestMethod]
    public void Compile_OutputXml_HasTvRootElement()
    {
        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue> { [Source1Id] = EpgCatalogue.Empty(Source1Id) };
        var outputChannels = new List<OutputChannel>();
        var mappings = Enumerable.Empty<EpgChannelMapping>()
            .ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (xml, _) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.IsTrue(xml.Contains("<tv ") || xml.Contains("<tv>"));
        Assert.IsTrue(xml.Contains("</tv>"));
    }

    // -------------------------------------------------------------------------
    // Report
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Compile_Report_TotalChannelsMatchesInput()
    {
        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue> { [Source1Id] = EpgCatalogue.Empty(Source1Id) };
        var outputChannels = new List<OutputChannel>
        {
            MakeChannel("pc-1", "ch1.us", "CH1"),
            MakeChannel("pc-2", "ch2.us", "CH2"),
            MakeChannel("pc-3", "ch3.us", "CH3"),
        };
        var mappings = Enumerable.Empty<EpgChannelMapping>()
            .ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var (_, report) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.AreEqual(3, report.TotalChannels);
    }

    [TestMethod]
    public void Compile_Report_GeneratedUtcIsRecent()
    {
        var sources = new List<EpgSource> { MakeSource(Source1Id, 1) };
        var catalogues = new Dictionary<string, EpgCatalogue> { [Source1Id] = EpgCatalogue.Empty(Source1Id) };
        var outputChannels = new List<OutputChannel>();
        var mappings = Enumerable.Empty<EpgChannelMapping>()
            .ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);

        var before = DateTime.UtcNow.AddSeconds(-1);
        var (_, report) = _compiler.Compile(outputChannels, sources, catalogues, mappings);

        Assert.IsTrue(report.GeneratedUtc >= before);
    }
}
