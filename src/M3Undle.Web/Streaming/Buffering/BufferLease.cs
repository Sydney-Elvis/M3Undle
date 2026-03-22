using System.Threading;

namespace M3Undle.Web.Streaming.Buffering;

public sealed class BufferLease : IDisposable
{
    private BufferChunk? _chunk;

    internal BufferLease(BufferChunk chunk)
    {
        _chunk = chunk;
    }

    public ReadOnlyMemory<byte> Memory => _chunk?.Memory ?? ReadOnlyMemory<byte>.Empty;

    public long Sequence => _chunk?.Sequence ?? -1;

    public int Generation => _chunk?.Generation ?? -1;

    public bool HasValue => _chunk is not null;

    internal BufferLease Duplicate()
    {
        if (_chunk is null)
            throw new InvalidOperationException("Cannot duplicate a released lease.");

        _chunk.Retain();
        return new BufferLease(_chunk);
    }

    public void Dispose()
    {
        var chunk = Interlocked.Exchange(ref _chunk, null);
        chunk?.Release();
    }
}
