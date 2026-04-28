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
}
