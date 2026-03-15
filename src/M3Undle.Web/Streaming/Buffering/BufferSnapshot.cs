namespace M3Undle.Web.Streaming.Buffering;

public sealed record BufferSnapshot(
    int Generation,
    IReadOnlyList<BufferLease> Chunks,
    int UsedBytes,
    int MaxBytes)
{
    public int RemainingBytes => Math.Max(0, MaxBytes - UsedBytes);
}

