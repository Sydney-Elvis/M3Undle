using M3Undle.Web.Streaming.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Settings;

[TestClass]
public sealed class StreamingOptionsValidatorTests
{
    // ── BufferOptionsValidator ────────────────────────────────────────────────

    [TestMethod]
    public void BufferOptions_ValidDefaults_Passes()
    {
        var result = new BufferOptionsValidator().Validate(null, new BufferOptions());
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void BufferOptions_MaxBytesPerSessionExceedsLimit_Fails()
    {
        var options = new BufferOptions { MaxBytesPerSession = StreamingSettingsLimits.MaxBytesPerSession + 1 };
        var result = new BufferOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void BufferOptions_MaxBytesPerSessionZero_Fails()
    {
        var options = new BufferOptions { MaxBytesPerSession = 0 };
        var result = new BufferOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void BufferOptions_ReadChunkSizeBytesExceedsLimit_Fails()
    {
        var options = new BufferOptions { ReadChunkSizeBytes = StreamingSettingsLimits.MaxReadChunkSizeBytes + 1 };
        var result = new BufferOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void BufferOptions_ReadChunkSizeBytesZero_Fails()
    {
        var options = new BufferOptions { ReadChunkSizeBytes = 0 };
        var result = new BufferOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void BufferOptions_HardCapBelowPerSession_Fails()
    {
        var options = new BufferOptions
        {
            MaxBytesPerSession = 8 * 1024 * 1024,
            MaxBytesHardCap = 4 * 1024 * 1024,
        };
        var result = new BufferOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    // ── StreamProxyOptionsValidator ───────────────────────────────────────────

    [TestMethod]
    public void StreamProxyOptions_ValidDefaults_Passes()
    {
        var result = new StreamProxyOptionsValidator().Validate(null, new StreamProxyOptions());
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void StreamProxyOptions_MaxConcurrentSessionsZero_Fails()
    {
        var options = new StreamProxyOptions { MaxConcurrentSessions = 0 };
        var result = new StreamProxyOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void StreamProxyOptions_IdleGraceZero_Fails()
    {
        var options = new StreamProxyOptions { IdleGrace = TimeSpan.Zero };
        var result = new StreamProxyOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void StreamProxyOptions_IdleGraceHardCapBelowIdleGrace_Fails()
    {
        var options = new StreamProxyOptions
        {
            IdleGrace = TimeSpan.FromSeconds(60),
            IdleGraceHardCap = TimeSpan.FromSeconds(30),
        };
        var result = new StreamProxyOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    // ── ReconnectOptionsValidator ─────────────────────────────────────────────

    [TestMethod]
    public void ReconnectOptions_ValidDefaults_Passes()
    {
        var result = new ReconnectOptionsValidator().Validate(null, new ReconnectOptions());
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void ReconnectOptions_ReadStallTimeoutZero_Fails()
    {
        var options = new ReconnectOptions { ReadStallTimeout = TimeSpan.Zero };
        var result = new ReconnectOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void ReconnectOptions_OutageWindowBelowReadStallTimeout_Fails()
    {
        var options = new ReconnectOptions
        {
            ReadStallTimeout = TimeSpan.FromSeconds(60),
            OutageWindow = TimeSpan.FromSeconds(30),
        };
        var result = new ReconnectOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void ReconnectOptions_ConnectTimeoutZero_Fails()
    {
        var options = new ReconnectOptions { ConnectTimeout = TimeSpan.Zero };
        var result = new ReconnectOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    // ── GeneratedHlsOptionsValidator ──────────────────────────────────────────

    [TestMethod]
    public void GeneratedHlsOptions_ValidDefaults_Passes()
    {
        var result = new GeneratedHlsOptionsValidator().Validate(null, new GeneratedHlsOptions());
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_EmptyDirectory_Fails()
    {
        var options = new GeneratedHlsOptions { Directory = string.Empty };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_NonPositiveSegmentDuration_Fails()
    {
        var options = new GeneratedHlsOptions { SegmentDurationSeconds = 0 };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_EmptyFfmpegPath_Fails()
    {
        var options = new GeneratedHlsOptions { FfmpegPath = string.Empty };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_NonPositivePlaylistSize_Fails()
    {
        var options = new GeneratedHlsOptions { PlaylistSize = 0 };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_DeleteThresholdBelowOne_Fails()
    {
        var options = new GeneratedHlsOptions { DeleteThreshold = 0 };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_NonPositiveStartupTimeout_Fails()
    {
        var options = new GeneratedHlsOptions { StartupTimeoutSeconds = 0 };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_NonPositiveInactivityTimeout_Fails()
    {
        var options = new GeneratedHlsOptions { InactivityTimeoutSeconds = 0 };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_NonPositiveCleanupInterval_Fails()
    {
        var options = new GeneratedHlsOptions { CleanupIntervalSeconds = 0 };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void GeneratedHlsOptions_NonPositiveStartupStaleAge_Fails()
    {
        var options = new GeneratedHlsOptions { StartupStaleAgeHours = 0 };
        var result = new GeneratedHlsOptionsValidator().Validate(null, options);
        Assert.IsFalse(result.Succeeded);
    }
}
