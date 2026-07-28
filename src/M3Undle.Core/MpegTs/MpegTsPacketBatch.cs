namespace M3Undle.Core.MpegTs;

public sealed record MpegTsPacketBatch(
    byte[] Data,
    MpegTsStartupKind StartupKind,
    int DroppedByteCount,
    bool SyncLost,
    bool HasKnownH264VideoStream = false,
    long? LatestVideoDts90k = null,
    long? IdrDts90k = null)
{
    public long? EarliestVideoDts90k { get; init; }

    /// <summary>
    /// Smallest signed step between consecutive video PES timestamps inside this batch,
    /// or null when the batch carries fewer than two of them. <see cref="EarliestVideoDts90k"/>
    /// and <see cref="LatestVideoDts90k"/> only describe the batch's outer span, which hides
    /// everything that happens between them: a read chunk that opens with normally-paced
    /// frames and then crosses into an FFmpeg-clamped (last+1) run still shows a large,
    /// perfectly healthy-looking first-to-last delta. The minimum step exposes that crossing.
    /// </summary>
    public long? MinVideoDtsStep90k { get; init; }
}
