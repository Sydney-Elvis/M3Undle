using M3Undle.Web.Application.Epg;
using M3Undle.Web.Data.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Epg;

[TestClass]
public sealed class EpgCheckScheduleTests
{
    private static readonly DateTime Created = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Checked = new(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc);

    private static EpgSource Source(
        string? providerId = "provider-1",
        int? overrideHours = null,
        DateTime? checkedUtc = null,
        DateTime? success = null,
        DateTime? failure = null,
        bool enabled = true) => new()
    {
        EpgSourceId = "src",
        ProviderId = providerId,
        Enabled = enabled,
        RefreshIntervalHours = overrideHours,
        LastCheckedUtc = checkedUtc,
        LastSuccessUtc = success,
        LastFailureUtc = failure,
        CreatedUtc = Created,
    };

    [TestMethod]
    public void ManualSchedule_NeverProducesDeadline()
        => Assert.IsNull(EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(checkedUtc: Checked, overrideHours: 6), null));

    [TestMethod]
    public void StandaloneSource_NeverProducesDeadline()
        => Assert.IsNull(EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(providerId: null, checkedUtc: Checked), 6));

    [TestMethod]
    public void DisabledSource_NeverProducesDeadline()
        => Assert.IsNull(EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(checkedUtc: Checked, enabled: false), 6));

    [TestMethod]
    public void SourceCadenceShorterThanSchedule_IsExpectedOnNextRefresh()
        => Assert.AreEqual(Checked.AddHours(12),
            EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(checkedUtc: Checked, overrideHours: 6), 12));

    [TestMethod]
    public void SourceCadenceLongerThanSchedule_IsExpectedAtFirstRefreshAfterCadence()
        => Assert.AreEqual(Checked.AddHours(24 + 6),
            EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(checkedUtc: Checked, overrideHours: 24), 6));

    [TestMethod]
    public void InheritedCadence_FollowsTheProfileSchedule()
        => Assert.AreEqual(Checked.AddHours(6),
            EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(checkedUtc: Checked), 6));

    [TestMethod]
    public void LegacyEvidenceWithoutRealCheck_IsUnknownNotBackfilled()
    {
        Assert.IsNull(EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(success: Checked), 6));
        Assert.IsNull(EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(failure: Checked), 6));
    }

    [TestMethod]
    public void NeverChecked_UsesCreationTime()
        => Assert.AreEqual(Created.AddHours(6), EpgCheckSchedule.ExpectedCheckDeadlineUtc(Source(), 6));

    [TestMethod]
    public void CacheFreshness_DelegatesToCadenceGate()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(EpgCheckSchedule.IsCacheFresh(now.UtcDateTime.AddHours(-5), 6, now));
        Assert.IsFalse(EpgCheckSchedule.IsCacheFresh(now.UtcDateTime.AddHours(-6), 6, now));
        Assert.IsFalse(EpgCheckSchedule.IsCacheFresh(null, 6, now));
    }
}
