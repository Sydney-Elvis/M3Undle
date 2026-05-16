namespace M3Undle.Core.MpegTs;

public sealed record MpegTsPacketBatch(
    byte[] Data,
    MpegTsStartupKind StartupKind,
    int DroppedByteCount,
    bool SyncLost,
    bool HasKnownH264VideoStream = false);
