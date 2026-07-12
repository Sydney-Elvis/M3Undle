namespace M3Undle.Core.MpegTs;

public static class MpegTsTimestamp
{
    public const long Wrap = 1L << 33;

    public const double TicksPerSecond = 90000.0;

    /// <summary>
    /// Signed shortest distance from <paramref name="from"/> to <paramref name="to"/> on the
    /// 33-bit PES timestamp circle, in 90 kHz ticks. Result is in [-Wrap/2, Wrap/2), so a
    /// timestamp that wrapped mid-comparison still resolves to the small signed delta.
    /// </summary>
    public static long Delta(long from, long to)
        => ((to - from + Wrap + Wrap / 2) % Wrap) - Wrap / 2;

    public static double DeltaSeconds(long from, long to)
        => Delta(from, to) / TicksPerSecond;
}
