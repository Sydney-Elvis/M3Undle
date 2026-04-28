namespace M3Undle.Web.Streaming.Buffering;

public sealed class RingBuffer
{
    private readonly object _gate = new();
    private readonly LinkedList<BufferChunk> _chunks = [];
    private readonly int _maxBytes;

    private int _usedBytes;
    private long _sequence;
    private long? _safeStartSequence;
    private int _generation;
    private bool _completed;

    public RingBuffer(int maxBytes)
    {
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        _maxBytes = maxBytes;
    }

    public int MaxBytes => _maxBytes;

    public int UsedBytes
    {
        get
        {
            lock (_gate)
            {
                return _usedBytes;
            }
        }
    }

    public int CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public BufferLease Write(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
            throw new ArgumentException("Buffer writes must contain data.", nameof(data));

        var chunk = default(BufferChunk);

        lock (_gate)
        {
            if (_completed)
                throw new InvalidOperationException("Buffer has been completed.");

            chunk = BufferChunk.Create(data, _sequence++, _generation);
            _chunks.AddLast(chunk);
            _usedBytes += chunk.Length;

            while (_usedBytes > _maxBytes && _chunks.First is { } head)
            {
                _chunks.RemoveFirst();
                _usedBytes -= head.Value.Length;
                head.Value.Release();
            }

            // Caller lease.
            chunk.Retain();
        }

        return new BufferLease(chunk);
    }

    public BufferSnapshot CreateSnapshot()
    {
        var leases = new List<BufferLease>();

        lock (_gate)
        {
            foreach (var chunk in _chunks)
            {
                chunk.Retain();
                leases.Add(new BufferLease(chunk));
            }

            return new BufferSnapshot(_generation, leases, _usedBytes, _maxBytes);
        }
    }

    public BufferSnapshot CreateLiveEdgeSnapshot()
    {
        lock (_gate)
        {
            return new BufferSnapshot(_generation, [], 0, _maxBytes);
        }
    }

    public void MarkSafeStart(BufferLease lease)
    {
        if (!lease.HasValue)
            return;

        MarkSafeStart(lease.Generation, lease.Sequence);
    }

    public void MarkSafeStart(int generation, long sequence)
    {
        lock (_gate)
        {
            if (generation == _generation)
                _safeStartSequence = sequence;
        }
    }

    public BufferSnapshot CreateSafeStartSnapshot()
    {
        var leases = new List<BufferLease>();

        lock (_gate)
        {
            if (_safeStartSequence is null)
                return new BufferSnapshot(_generation, [], 0, _maxBytes);

            var usedBytes = 0;
            foreach (var chunk in _chunks)
            {
                if (chunk.Generation != _generation || chunk.Sequence < _safeStartSequence.Value)
                    continue;

                chunk.Retain();
                leases.Add(new BufferLease(chunk));
                usedBytes += chunk.Length;
            }

            return new BufferSnapshot(_generation, leases, usedBytes, _maxBytes);
        }
    }

    public void ResetGeneration()
    {
        lock (_gate)
        {
            _generation++;
            ClearNoLock();
            _sequence = 0;
            _safeStartSequence = null;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            ClearNoLock();
        }
    }

    private void ClearNoLock()
    {
        foreach (var chunk in _chunks)
            chunk.Release();

        _chunks.Clear();
        _usedBytes = 0;
    }
}
