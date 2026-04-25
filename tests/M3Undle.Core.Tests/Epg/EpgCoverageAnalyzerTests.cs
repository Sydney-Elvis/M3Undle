using M3Undle.Core.Epg;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Core.Tests.Epg;

[TestClass]
public sealed class EpgCoverageAnalyzerTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = WindowStart.AddHours(24);

    [TestMethod]
    public void OverlapsWindow_ProgrammeInsideWindow_ReturnsTrue()
    {
        var programme = NewProgramme(WindowStart.AddHours(1), WindowStart.AddHours(2));

        Assert.IsTrue(EpgCoverageAnalyzer.OverlapsWindow(programme, WindowStart, WindowEnd));
    }

    [TestMethod]
    public void OverlapsWindow_ProgrammeStartsBeforeWindowAndStopsInside_ReturnsTrue()
    {
        var programme = NewProgramme(WindowStart.AddHours(-1), WindowStart.AddMinutes(30));

        Assert.IsTrue(EpgCoverageAnalyzer.OverlapsWindow(programme, WindowStart, WindowEnd));
    }

    [TestMethod]
    public void OverlapsWindow_ProgrammeStartsInsideAndStopsAfterWindow_ReturnsTrue()
    {
        var programme = NewProgramme(WindowEnd.AddHours(-1), WindowEnd.AddHours(1));

        Assert.IsTrue(EpgCoverageAnalyzer.OverlapsWindow(programme, WindowStart, WindowEnd));
    }

    [TestMethod]
    public void OverlapsWindow_ProgrammeEndsAtWindowStart_ReturnsFalse()
    {
        var programme = NewProgramme(WindowStart.AddHours(-1), WindowStart);

        Assert.IsFalse(EpgCoverageAnalyzer.OverlapsWindow(programme, WindowStart, WindowEnd));
    }

    [TestMethod]
    public void OverlapsWindow_ProgrammeStartsAtWindowEnd_ReturnsFalse()
    {
        var programme = NewProgramme(WindowEnd, WindowEnd.AddHours(1));

        Assert.IsFalse(EpgCoverageAnalyzer.OverlapsWindow(programme, WindowStart, WindowEnd));
    }

    [TestMethod]
    public void HasCoverage_EmptyProgrammeList_ReturnsFalse()
    {
        Assert.IsFalse(EpgCoverageAnalyzer.HasCoverage([], WindowStart, WindowEnd));
    }

    [TestMethod]
    public void HasCoverage_AnyOverlappingProgramme_ReturnsTrue()
    {
        var programmes = new[]
        {
            NewProgramme(WindowStart.AddHours(-3), WindowStart.AddHours(-2)),
            NewProgramme(WindowStart.AddHours(2), WindowStart.AddHours(3)),
        };

        Assert.IsTrue(EpgCoverageAnalyzer.HasCoverage(programmes, WindowStart, WindowEnd));
    }

    [TestMethod]
    public void HasChannelCoverage_MissingChannel_ReturnsFalse()
    {
        var catalogue = new EpgCatalogue(
            "source-1",
            [new EpgChannelRecord("source-1", "cnn.us", "CNN", null)],
            new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>());

        Assert.IsFalse(EpgCoverageAnalyzer.HasChannelCoverage(catalogue, "cnn.us", WindowStart, WindowEnd));
    }

    [TestMethod]
    public void HasChannelCoverage_ChannelWithOverlappingProgramme_ReturnsTrue()
    {
        var catalogue = new EpgCatalogue(
            "source-1",
            [new EpgChannelRecord("source-1", "cnn.us", "CNN", null)],
            new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>
            {
                ["cnn.us"] = [NewProgramme(WindowStart.AddHours(1), WindowStart.AddHours(2))],
            });

        Assert.IsTrue(EpgCoverageAnalyzer.HasChannelCoverage(catalogue, "cnn.us", WindowStart, WindowEnd));
    }

    private static EpgProgrammeRecord NewProgramme(DateTimeOffset start, DateTimeOffset stop)
        => new(
            "source-1",
            "cnn.us",
            start,
            stop,
            "Programme",
            null,
            null,
            [],
            [],
            null);
}
