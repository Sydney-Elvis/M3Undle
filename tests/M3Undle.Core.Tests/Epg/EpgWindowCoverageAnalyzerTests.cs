using M3Undle.Core.Epg;

namespace M3Undle.Core.Tests.Epg;

[TestClass]
public sealed class EpgWindowCoverageAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(12);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    private static EpgInterval I(double startHours, double stopHours) =>
        new(Now.AddHours(startHours), Now.AddHours(stopHours));

    [TestMethod]
    public void ContinuousCoverageThroughHorizon_IsUsable()
        => Assert.IsTrue(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(-1, 6), I(6, 13)], Now, Horizon, Gap));

    [TestMethod]
    public void SmallGapsUpToTheAllowance_AreTolerated()
        => Assert.IsTrue(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(-1, 5), I(5.5, 13)], Now, Horizon, Gap));

    [TestMethod]
    public void GapLongerThanAllowance_BreaksCoverage()
        => Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(-1, 5), I(5.6, 13)], Now, Horizon, Gap));

    [TestMethod]
    public void CoverageEndingShortOfHorizon_FailsBeyondTheAllowance()
    {
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(-1, 8)], Now, Horizon, Gap));
        Assert.IsTrue(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(-1, 11.6)], Now, Horizon, Gap), "A shortfall within the gap allowance at the end is tolerated.");
    }

    [TestMethod]
    public void FarFutureProgrammesAlone_CannotMakeAChannelUsable()
    {
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(40, 48)], Now, Horizon, Gap));
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(40, 48), I(48, 72)], Now, TimeSpan.FromHours(1), TimeSpan.FromHours(2)),
            "Even a generous gap allowance must not turn a programme starting after the window into coverage.");
    }

    [TestMethod]
    public void ExpiredProgrammesAlone_AreNotUsableAndNotCurrentOrFutureData()
    {
        var expired = new[] { I(-30, -20), I(-20, -1) };
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage(expired, Now, Horizon, Gap));
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasCurrentOrFutureData(expired, Now));
    }

    [TestMethod]
    public void InvalidIntervals_AreIgnored()
    {
        var invalid = new[] { I(0, 0), I(5, 1), new EpgInterval(default, default) };
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage(invalid, Now, Horizon, Gap));
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasCurrentOrFutureData(invalid, Now));
        Assert.IsTrue(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(5, 1), I(-1, 13)], Now, Horizon, Gap), "An invalid interval does not disturb valid ones.");
    }

    [TestMethod]
    public void OverlappingAndUnorderedIntervals_AreMerged()
        => Assert.IsTrue(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(6, 13), I(-1, 7), I(2, 3)], Now, Horizon, Gap));

    [TestMethod]
    public void LeadInGapFromNow_CountsAgainstTheAllowance()
    {
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(1, 13)], Now, Horizon, Gap));
        Assert.IsTrue(EpgWindowCoverageAnalyzer.HasUsableCoverage([I(0.25, 13)], Now, Horizon, Gap));
    }

    [TestMethod]
    public void Evaluate_ReportsFractionAndNotMonitoredAndNoData()
    {
        var healthy = new[] { I(-1, 13) };
        var gappy = new[] { I(-1, 3) };
        var empty = Array.Empty<EpgInterval>();

        var result = EpgWindowCoverageAnalyzer.Evaluate([healthy, healthy, gappy, empty], Now, Horizon, Gap);
        Assert.AreEqual(4, result.RelevantChannels);
        Assert.AreEqual(2, result.UsableChannels);
        Assert.AreEqual(3, result.ChannelsWithCurrentOrFutureData);
        Assert.AreEqual(0.5, result.Fraction, 1e-9);
        Assert.IsFalse(result.NotMonitored);
        Assert.IsFalse(result.NoUsableData);

        Assert.IsTrue(EpgWindowCoverageAnalyzer.Evaluate([], Now, Horizon, Gap).NotMonitored);
        Assert.IsTrue(EpgWindowCoverageAnalyzer.Evaluate([empty, gappy.Select(x => new EpgInterval(x.StartUtc.AddDays(-9), x.StopUtc.AddDays(-9))).ToArray()], Now, Horizon, Gap).NoUsableData);
    }

    [TestMethod]
    public void ExistingAnyOverlapAnalyzer_KeepsItsLooserContract()
    {
        var programme = new EpgProgrammeRecord("s", "c", Now.AddHours(11), Now.AddHours(12), "t", null, null, [], [], null);
        Assert.IsTrue(EpgCoverageAnalyzer.HasCoverage([programme], Now, Now.AddHours(12)),
            "HasCoverage still means any overlap; continuous coverage is the new analyzer's job.");
        Assert.IsFalse(EpgWindowCoverageAnalyzer.HasUsableCoverage([new EpgInterval(programme.StartUtc, programme.StopUtc)], Now, Horizon, Gap));
    }
}
