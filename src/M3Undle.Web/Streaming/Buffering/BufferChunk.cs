using System.Buffers;
using System.Threading;

namespace M3Undle.Web.Streaming.Buffering;

internal sealed class BufferChunk
{
    private readonly byte[] _buffer;
    private int _refCount;

    private BufferChunk(byte[] buffer, int length, long sequence, int generation)
    {
        _buffer = buffer;
        Length = length;
        Sequence = sequence;
        Generation = generation;
        _refCount = 1; // ring ownership
    }

    public int Length { get; }

    public long Sequence { get; }

    public int Generation { get; }

    public ReadOnlyMemory<byte> Memory => new(_buffer, 0, Length);

    public static BufferChunk Create(ReadOnlyMemory<byte> source, long sequence, int generation)
    {
        var rented = ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(rented);
        return new BufferChunk(rented, source.Length, sequence, generation);
    }

    public void Retain() => Interlocked.Increment(ref _refCount);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) != 0)
            return;

        ArrayPool<byte>.Shared.Return(_buffer);
    }
}

