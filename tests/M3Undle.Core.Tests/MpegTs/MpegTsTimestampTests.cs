using M3Undle.Core.MpegTs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Core.Tests.MpegTs;

[TestClass]
public sealed class MpegTsTimestampTests
{
    [TestMethod]
    public void Delta_ForwardDistance_IsPositive()
    {
        Assert.AreEqual(90000, MpegTsTimestamp.Delta(0, 90000));
        Assert.AreEqual(1.0, MpegTsTimestamp.DeltaSeconds(0, 90000));
    }

    [TestMethod]
    public void Delta_BackwardDistance_IsNegative()
    {
        Assert.AreEqual(-90000, MpegTsTimestamp.Delta(90000, 0));
        Assert.AreEqual(-1.0, MpegTsTimestamp.DeltaSeconds(90000, 0));
    }

    [TestMethod]
    public void Delta_SamePosition_IsZero()
        => Assert.AreEqual(0, MpegTsTimestamp.Delta(123456789, 123456789));

    [TestMethod]
    public void Delta_ForwardAcrossWrap_IsSmallPositive()
    {
        // 1 s before the wrap point → 1 s after it: forward 2 s, not backward ~26.5 h.
        var before = MpegTsTimestamp.Wrap - 90000;
        Assert.AreEqual(180000, MpegTsTimestamp.Delta(before, 90000));
    }

    [TestMethod]
    public void Delta_BackwardAcrossWrap_IsSmallNegative()
    {
        // 1 s after the wrap point → 1 s before it: backward 2 s.
        var after = 90000L;
        Assert.AreEqual(-180000, MpegTsTimestamp.Delta(after, MpegTsTimestamp.Wrap - 90000));
    }

    [TestMethod]
    public void Delta_HalfCircle_IsNegativeBoundary()
        => Assert.AreEqual(-MpegTsTimestamp.Wrap / 2, MpegTsTimestamp.Delta(0, MpegTsTimestamp.Wrap / 2));

    [TestMethod]
    public void Delta_TypicalProviderRewind_IsNegativeSixtySeconds()
    {
        // Live edge at t, reconnected stream serves from t-60 s.
        var liveEdge = 3384789480L;
        var rewound = liveEdge - 60 * 90000;
        Assert.AreEqual(-60.0, MpegTsTimestamp.DeltaSeconds(liveEdge, rewound));
    }
}
