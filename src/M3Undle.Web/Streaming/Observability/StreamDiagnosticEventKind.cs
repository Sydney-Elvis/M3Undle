namespace M3Undle.Web.Streaming.Observability;

public enum StreamDiagnosticEventKind
{
    SessionCreated = 0,
    SubscriberAttached = 1,
    SubscriberRemoved = 2,
    UpstreamConnectStarted = 3,
    UpstreamConnected = 4,
    FirstUpstreamByte = 5,
    UpstreamFailure = 6,
    ReconnectScheduled = 7,
    ReconnectRecovered = 8,
    CooldownRecorded = 9,
    AdmissionRejected = 10,
    StopTriggered = 11,
    SessionClosed = 12,
    MpegTsSyncLost = 13,
    MpegTsSafeStartSelected = 14,
    MpegTsPacketizerDisabled = 15,
    FfmpegRelayStarted = 16,
    FfmpegRelayFallbackToDirect = 17,
    RecoveryStarted = 18,
    RecoveryOutputHeld = 19,
    RecoverySafeStartFound = 20,
    RecoveryOutputResumed = 21,
    RecoveryHoldLimitExceeded = 22,
    RecoveryFailedUnsafe = 23,
    SubscriberQueueFull = 24,
    RecoveryForcedRetune = 25,
    ClientAbortAfterRecovery = 26,
    ControlledDownstreamRetune = 27,
    CleanWatchCompleted = 28,
    RecoveryOverlapTrimmed = 29,
    RecoveryOverlapTrimAbandoned = 30,
    InProcessRelayTimelineRewind = 31,
    ClampedDtsRampRecoveryAbandoned = 32,

    /// <summary>
    /// A recovery gave up requiring a fresh restart point (whole-outage catch-up deadline
    /// or DTS-progress stall expired) and resumed on the best decoder-safe boundary
    /// available instead — a deliberate, logged discontinuity rather than a silent one.
    /// </summary>
    RecoveryStaleResumeAccepted = 33,
}
