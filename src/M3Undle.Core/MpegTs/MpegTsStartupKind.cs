namespace M3Undle.Core.MpegTs;

public enum MpegTsStartupKind
{
    None = 0,
    PacketBoundary = 1,
    PatPmt = 2,
    H264Idr = 3,
}
