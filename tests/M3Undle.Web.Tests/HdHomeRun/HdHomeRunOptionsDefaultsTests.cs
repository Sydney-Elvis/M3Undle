using M3Undle.Web.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.HdHomeRun;

/// <summary>
/// Regression coverage for the "no tuners available" bug (toontown-int-srv2, NextPVR):
/// when no stream limit is enforced (no DB tuner-count override, no active provider
/// MaxConcurrentStreams), HdHomeRunTunerCountResolver falls back to the static
/// HdHomeRun:TunerCount config value as the *advertised* virtual tuner count. That value
/// previously shipped as 1 in appsettings.json, which leaves zero headroom for a second
/// concurrent tuner consumer (e.g. NextPVR's EPG/PSIP scan running alongside live TV) and
/// causes the client to self-reject with "no tuner available" before ever contacting M3Undle.
/// These tests guard against that value silently regressing back to 1 in either the shipped
/// config file or the C# default.
/// </summary>
[TestClass]
public sealed class HdHomeRunOptionsDefaultsTests
{
    // A single virtual tuner leaves no room for a client to run EPG/PSIP scanning
    // concurrently with live playback; require fallback configs to advertise more than one.
    private const int MinimumSaneUnenforcedTunerCount = 2;

    [TestMethod]
    public void ShippedAppSettings_HdHomeRunTunerCount_IsAboveMinimum()
    {
        // Loads the actual appsettings.json that ships with the app (copied to the test
        // output directory via the M3Undle.Web project reference), not a value constructed
        // in-test, so this catches a regression in the real config file.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var tunerCount = configuration.GetValue<int?>("M3Undle:HdHomeRun:TunerCount");

        Assert.IsNotNull(tunerCount, "appsettings.json must define M3Undle:HdHomeRun:TunerCount.");
        Assert.IsGreaterThanOrEqualTo(
            MinimumSaneUnenforcedTunerCount,
            tunerCount.Value,
            "Shipped HdHomeRun:TunerCount is the advertised tuner count when no limit is " +
            "enforced; a value of 1 leaves no headroom for concurrent client use (e.g. EPG " +
            "scan + live TV) and reproduces the toontown-int-srv2 'no tuners available' bug.");
    }

    [TestMethod]
    public void DefaultHdHomeRunOptions_TunerCount_IsAboveMinimum()
    {
        var defaults = new HdHomeRunOptions();

        Assert.IsGreaterThanOrEqualTo(
            MinimumSaneUnenforcedTunerCount,
            defaults.TunerCount,
            "The HdHomeRunOptions.TunerCount C# default is used whenever a deployment omits " +
            "the config key entirely; it must stay above 1 for the same reason as the shipped " +
            "appsettings.json value.");
    }
}
