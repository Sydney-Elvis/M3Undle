using System.Security.Cryptography;
using System.Text;
using M3Undle.Web.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

/// <summary>
/// Xtream stream IDs must work for two clients with conflicting constraints at once:
///   * Android TV / NextPVR (and IPTV Smarters): every advertised ID must resolve back to its
///     own channel — the mapping must be a collision-free bijection.
///   * Roku / Brightscript: IDs render in scientific notation once they hit 1e7, so they must
///     stay below 10,000,000 to format as plain digits inside stream URLs.
///
/// These tests first pin the two regressions we actually shipped (a 31-bit hash broke Roku; a
/// 23-bit hash broke the Android TV / NextPVR / Smarters path via collisions), then prove the
/// current assignment satisfies <b>both</b> at once.
/// </summary>
[TestClass]
public sealed class XtreamStreamIdCacheTests
{
    // At/above this value Brightscript renders a number it parsed as a float in scientific
    // notation (e.g. "1E+07"), which corrupts the stream URL. IDs must stay strictly below it.
    private const int BrightscriptScientificThreshold = 10_000_000;

    private static List<string> GenerateKeys(int count)
    {
        var keys = new List<string>(count);
        for (var i = 0; i < count; i++)
            keys.Add($"stream-key-{i:D7}");
        return keys;
    }

    private static int LegacyThirtyOneBitId(string key)
    {
        var value = BitConverter.ToUInt32(MD5.HashData(Encoding.UTF8.GetBytes(key)), 0);
        return (int)(value & 0x7FFF_FFFF);
    }

    private static int LegacyTwentyThreeBitId(string key)
    {
        var value = BitConverter.ToUInt32(MD5.HashData(Encoding.UTF8.GetBytes(key)), 0);
        return (int)(value & 0x007F_FFFF);
    }

    // ---- Failure mode 1: Roku, broken by the original 31-bit IDs --------------------------

    [TestMethod]
    public void Legacy31BitIds_BreakRoku_BecauseTheyExceedTheScientificNotationThreshold()
    {
        var keys = GenerateKeys(20_000);

        var offenders = keys
            .Select(LegacyThirtyOneBitId)
            .Where(id => id >= BrightscriptScientificThreshold)
            .ToList();

        // The 31-bit scheme routinely produces IDs at/above 1e7 (e.g. WCBS = 815011305), which Roku
        // renders as scientific notation, corrupting the .ts URL.
        Assert.IsTrue(offenders.Count > 0,
            "Expected 31-bit IDs to reach Roku's scientific-notation threshold (>= 1e7).");
    }

    // ---- Failure mode 2: Android TV / NextPVR / Smarters, broken by 23-bit collisions ------

    [TestMethod]
    public void Legacy23BitIds_BreakAndroidTvAndSmarters_BecauseCollisionsDropChannels()
    {
        var keys = GenerateKeys(50_000);

        var distinctIds = keys.Select(LegacyTwentyThreeBitId).Distinct().Count();

        // Fewer distinct IDs than channels means some channels share an ID: the resolver keeps
        // only one, so the others become unreachable for every standard Xtream client.
        Assert.IsTrue(distinctIds < keys.Count,
            $"Expected 23-bit hashing to collide. distinctIds={distinctIds} keys={keys.Count}");
    }

    // ---- The fix: one scheme that satisfies both clients ----------------------------------

    [TestMethod]
    public void BuildAssignment_IsRokuSafe_AllIdsStayBelowTheScientificNotationThreshold()
    {
        var keys = GenerateKeys(50_000);

        var assignment = XtreamStreamIdCache.BuildAssignment(keys);

        foreach (var id in assignment.IdToKey.Keys)
        {
            Assert.IsTrue(id >= 1 && id <= XtreamStreamIdCache.MaxStreamId,
                $"ID {id} is outside the Brightscript-safe range [1, {XtreamStreamIdCache.MaxStreamId}].");
            Assert.IsTrue(id < BrightscriptScientificThreshold,
                $"ID {id} reaches the Brightscript scientific-notation threshold and would break the Roku URL.");
        }
    }

    [TestMethod]
    public void BuildAssignment_IsAndroidTvSafe_IsACollisionFreeBijection_EveryChannelRoundTrips()
    {
        var keys = GenerateKeys(50_000);

        var assignment = XtreamStreamIdCache.BuildAssignment(keys);

        // No channel is dropped and no two channels share an ID.
        Assert.AreEqual(keys.Count, assignment.KeyToId.Count, "Some channels were dropped from the assignment.");
        Assert.AreEqual(assignment.KeyToId.Count, assignment.IdToKey.Count, "Two channels collided onto one ID.");

        // Every ID advertised by player_api (key -> id) resolves back to that same channel
        // (id -> key) — the property that NextPVR/Smarters/Jellyfin rely on to tune.
        foreach (var (key, id) in assignment.KeyToId)
            Assert.AreEqual(key, assignment.IdToKey[id], $"ID {id} did not resolve back to its own channel.");
    }

    [TestMethod]
    public void BuildAssignment_IsDeterministic_RegardlessOfInputOrder()
    {
        var keys = GenerateKeys(5_000);
        var reversed = Enumerable.Reverse(keys).ToList();

        var first = XtreamStreamIdCache.BuildAssignment(keys);
        var second = XtreamStreamIdCache.BuildAssignment(reversed);

        CollectionAssert.AreEquivalent(
            first.KeyToId.ToList(),
            second.KeyToId.ToList(),
            "Assignment must not depend on channel enumeration order.");
    }

    [TestMethod]
    public void BuildAssignment_RegressionForWcbs_IsBelowTenMillion_AndNotTheBroken31BitId()
    {
        // The real key that broke playback on toontown-tv-srv1.
        const string wcbsKey = "hu4q9YO4P3veelAx";

        var assignment = XtreamStreamIdCache.BuildAssignment([wcbsKey]);
        var id = assignment.KeyToId[wcbsKey];

        Assert.IsTrue(id < BrightscriptScientificThreshold, "WCBS ID must stay below 1e7 for Roku.");
        Assert.AreNotEqual(815011305, id, "Must not regress to the old 31-bit ID that broke Roku.");
        Assert.AreEqual(wcbsKey, assignment.IdToKey[id], "WCBS ID must resolve back to WCBS.");
    }

    // ---- Backward-compatibility bridge: cached clients keep working without a re-pull ----------

    [TestMethod]
    public void LegacyBridge_OldHighIds_StillResolveToTheirChannel_SoCachedClientsKeepWorking()
    {
        var keys = GenerateKeys(50_000);

        var assignment = XtreamStreamIdCache.BuildAssignment(keys);

        // Pick keys whose pre-cap (31-bit) ID is above the cap — exactly the IDs NextPVR/Smarters
        // still request from their cached lists.
        var bridged = keys
            .Where(k => XtreamStreamIdCache.ToLegacyStreamId(k) > XtreamStreamIdCache.MaxStreamId)
            .Take(500)
            .ToList();

        Assert.IsTrue(bridged.Count > 0, "Expected some keys with pre-cap legacy IDs.");
        foreach (var key in bridged)
        {
            var legacyId = XtreamStreamIdCache.ToLegacyStreamId(key);
            Assert.AreEqual(key, assignment.LegacyIdToKey[legacyId],
                "A cached pre-cap ID must still resolve to its own channel.");
        }
    }

    [TestMethod]
    public void LegacyBridge_OnlyBridgesAboveTheCap_SoItCannotCollideWithCurrentIds()
    {
        var keys = GenerateKeys(50_000);

        var assignment = XtreamStreamIdCache.BuildAssignment(keys);

        // Every bridged legacy ID is above the cap, hence disjoint from current (<= cap) IDs.
        foreach (var legacyId in assignment.LegacyIdToKey.Keys)
            Assert.IsTrue(legacyId > XtreamStreamIdCache.MaxStreamId,
                $"Legacy ID {legacyId} is within the current ID range and would be ambiguous.");
    }

    [TestMethod]
    public void LegacyBridge_ResolvesTheExactWcbsIdThatBrokePlayback()
    {
        // 815011305 is the old 31-bit ID NextPVR cached for WCBS and kept requesting.
        const string wcbsKey = "hu4q9YO4P3veelAx";

        var assignment = XtreamStreamIdCache.BuildAssignment([wcbsKey]);

        Assert.AreEqual(815011305, XtreamStreamIdCache.ToLegacyStreamId(wcbsKey));
        Assert.AreEqual(wcbsKey, assignment.LegacyIdToKey[815011305],
            "The old WCBS ID must still resolve so cached clients recover without a re-pull.");
    }
}
