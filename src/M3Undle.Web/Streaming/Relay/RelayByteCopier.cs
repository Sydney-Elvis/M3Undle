using System.Buffers;

namespace M3Undle.Web.Streaming.Relay;

internal static class RelayByteCopier
{
    private const int BufferSize = 64 * 1024;
    private const long ReportIntervalMs = 1000;

    public static async Task CopyWithByteReportingAsync(
        Stream source,
        Stream destination,
        Action<long> reportTotalBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        var lastReportAt = Environment.TickCount64;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;

                var now = Environment.TickCount64;
                if (now - lastReportAt >= ReportIntervalMs)
                {
                    lastReportAt = now;
                    reportTotalBytes(total);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            reportTotalBytes(total);
        }
    }
}
