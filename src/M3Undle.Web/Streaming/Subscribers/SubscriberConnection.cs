using System.IO.Pipelines;
using System.Threading.Channels;
using M3Undle.Web.Streaming.Buffering;
using M3Undle.Web.Streaming.Observability;

namespace M3Undle.Web.Streaming.Subscribers;

public sealed record HdhrSubscriberDiagnostics(
    string TunerId,
    string ReservationId,
    string StreamKey,
    string VirtualPath);

public enum SubscriberQueueOverflowResult
{
    /// <summary>First overflow after the client was keeping up — the grace window just started.</summary>
    EnteredGrace,

    /// <summary>Still within the grace window — skip the overflowing chunk but keep the client.</summary>
    WithinGrace,

    /// <summary>Queue has stayed full past the grace window — evict as a slow client.</summary>
    GraceExceeded,
}

public sealed class SubscriberConnection
{
    private readonly HttpContext _context;
    private readonly PipeWriter _writer;
    private readonly Channel<BufferLease> _outbound;
    private readonly Func<SubscriberConnection, SubscriberDisconnectReason, Task> _onCompleted;
    private readonly TimeSpan _writeStallTimeout;
    private readonly CancellationTokenSource _serverCompletionCts = new();
    private readonly object _gate = new();

    private bool _started;
    private int _completed;
    private Task? _pumpTask;
    private long _bytesSent;
    private int _queueDepth;
    private long _queueFullSinceTicks;
    private readonly int _queueLowWaterMark;
    private int _resyncPending;
    private int _resyncLastDepth;
    private HdhrSubscriberDiagnostics? _hdhrDiagnostics;

    public SubscriberConnection(
        string sessionId,
        string requestedRoute,
        HttpContext context,
        int queueCapacity,
        TimeSpan writeStallTimeout,
        Func<SubscriberConnection, SubscriberDisconnectReason, Task> onCompleted,
        bool isInternal = false)
    {
        SessionId = sessionId;
        RequestedRoute = requestedRoute;
        IsInternal = isInternal;
        _context = context;
        _writer = context.Response.BodyWriter;
        _onCompleted = onCompleted;
        _writeStallTimeout = writeStallTimeout;

        var effectiveCapacity = Math.Max(1, queueCapacity);
        _queueLowWaterMark = Math.Max(1, effectiveCapacity / 2);
        var options = new BoundedChannelOptions(effectiveCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        };

        _outbound = Channel.CreateBounded<BufferLease>(options);
        ClientId = Guid.NewGuid().ToString("N");
        ConnectedUtc = DateTimeOffset.UtcNow;
        RemoteIp = RemoteIpAddressFormatter.Format(context.Connection.RemoteIpAddress);
        UserAgent = context.Request.Headers.UserAgent.ToString();
        RequestPath = context.Request.Path.Value ?? requestedRoute;
    }

    public string ClientId { get; }

    public string SessionId { get; }

    public string RequestedRoute { get; }

    public string RequestPath { get; }

    public bool IsInternal { get; }

    public DateTimeOffset ConnectedUtc { get; }

    public string? RemoteIp { get; }

    public string? UserAgent { get; }

    public HdhrSubscriberDiagnostics? HdhrDiagnostics => Volatile.Read(ref _hdhrDiagnostics);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public int QueueDepth => Math.Max(0, Volatile.Read(ref _queueDepth));

    public int LowWaterMark => _queueLowWaterMark;

    public bool IsCompleted => Volatile.Read(ref _completed) == 1;

    public bool IsResyncPending => Volatile.Read(ref _resyncPending) == 1;

    public Task Completion => _pumpTask ?? Task.CompletedTask;

    public void InitializeResponse(string? contentType, string? cacheControl)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            _context.Response.ContentType = contentType;
        if (!string.IsNullOrWhiteSpace(cacheControl))
            _context.Response.Headers.CacheControl = cacheControl;

        _context.Response.StatusCode = StatusCodes.Status200OK;
    }

    public Task StartAsync(BufferSnapshot initialSnapshot, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_started)
                return _pumpTask ?? Task.CompletedTask;

            _started = true;
            _pumpTask = PumpAsync(initialSnapshot, ct);
            return _pumpTask;
        }
    }

    public bool TryEnqueue(BufferLease lease)
    {
        if (Volatile.Read(ref _completed) == 1)
            return false;

        if (!_outbound.Writer.TryWrite(lease))
            return false;

        var depth = Interlocked.Increment(ref _queueDepth);

        // Once the client has drained back below the low-water mark it is keeping up
        // again, so clear any slow-client grace timer. Requiring real headroom (not just
        // a single accepted chunk) means a consumer that stays pinned near capacity keeps
        // the timer running and is eventually evicted.
        if (depth <= _queueLowWaterMark)
            Volatile.Write(ref _queueFullSinceTicks, 0);

        return true;
    }

    /// <summary>
    /// Records that a live chunk could not be enqueued because the outbound queue is full.
    /// The queue stays bounded (the overflowing chunk is dropped by the caller); this only
    /// decides whether a sustained backlog has lasted long enough to evict the subscriber.
    /// </summary>
    public SubscriberQueueOverflowResult RegisterQueueOverflow(TimeSpan grace)
    {
        var now = Environment.TickCount64;
        var since = Volatile.Read(ref _queueFullSinceTicks);
        if (since == 0)
        {
            // TickCount64 is 0 only in the first millisecond after boot; map it to 1 so 0
            // stays a reliable "not currently backed up" sentinel.
            Volatile.Write(ref _queueFullSinceTicks, now == 0 ? 1 : now);
            return SubscriberQueueOverflowResult.EnteredGrace;
        }

        return now - since >= (long)grace.TotalMilliseconds
            ? SubscriberQueueOverflowResult.GraceExceeded
            : SubscriberQueueOverflowResult.WithinGrace;
    }

    /// <summary>
    /// Resets the slow-client grace timer without clearing resync state. Used while a
    /// subscriber is resyncing and still draining: it is keeping up and merely waiting for
    /// the next clean TS boundary, so it must not accumulate grace it cannot influence.
    /// </summary>
    public void ResetSlowClientGrace()
        => Volatile.Write(ref _queueFullSinceTicks, 0);

    /// <summary>
    /// Reports whether the outbound queue has drained since the previous resync tick. A
    /// strictly smaller depth means the client is making progress (keep its grace timer
    /// reset); a depth that holds steady or grows means it is genuinely stuck (let the grace
    /// timer run). Single-threaded: only called from the session publish loop while resyncing.
    /// </summary>
    public bool RegisterResyncDrainProgress()
    {
        var depth = Volatile.Read(ref _queueDepth);
        var previous = _resyncLastDepth;
        _resyncLastDepth = depth;
        return depth < previous;
    }

    /// <summary>
    /// Marks this subscriber as needing a resync: no more chunks will be enqueued until
    /// <see cref="ClearResyncPending"/> is called at a clean TS boundary. Captures the
    /// current queue depth as the baseline for drain-progress tracking.
    /// </summary>
    public void SetResyncPending()
    {
        _resyncLastDepth = Volatile.Read(ref _queueDepth);
        Volatile.Write(ref _resyncPending, 1);
    }

    /// <summary>
    /// Clears the resync-pending flag and resets the slow-client grace timer so the
    /// subscriber starts fresh after a successful boundary resync.
    /// </summary>
    public void ClearResyncPending()
    {
        Volatile.Write(ref _resyncPending, 0);
        Volatile.Write(ref _queueFullSinceTicks, 0);
    }

    public Task CompleteAsync(SubscriberDisconnectReason reason)
        => CompleteCoreAsync(reason, cancelPump: false);

    public Task CompleteFromServerAsync(SubscriberDisconnectReason reason)
        => CompleteCoreAsync(reason, cancelPump: true);

    public bool HasSameClientFingerprint(SubscriberConnection other)
        => !IsInternal
           && !other.IsInternal
           && !string.IsNullOrWhiteSpace(RemoteIp)
           && !string.IsNullOrWhiteSpace(UserAgent)
           && string.Equals(RemoteIp, other.RemoteIp, StringComparison.OrdinalIgnoreCase)
           && string.Equals(UserAgent, other.UserAgent, StringComparison.Ordinal)
           && string.Equals(RequestPath, other.RequestPath, StringComparison.OrdinalIgnoreCase);

    private Task CompleteCoreAsync(SubscriberDisconnectReason reason, bool cancelPump)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
            return Task.CompletedTask;

        _outbound.Writer.TryComplete();
        if (cancelPump)
            _serverCompletionCts.Cancel();
        return _onCompleted(this, reason);
    }

    public void AttachHdhrDiagnostics(HdhrSubscriberDiagnostics diagnostics)
        => Volatile.Write(ref _hdhrDiagnostics, diagnostics);

    public StreamClientSnapshot Snapshot()
        => new(
            ClientId,
            SessionId,
            RequestedRoute,
            RemoteIp,
            UserAgent,
            ConnectedUtc,
            BytesSent,
            QueueDepth,
            IsInternal,
            Delivery: DeliveryMethod.RawTs,
            DeliveryReason: "Direct MPEG-TS passthrough (no remux)");

    private async Task PumpAsync(BufferSnapshot initialSnapshot, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            _context.RequestAborted,
            _serverCompletionCts.Token);
        // One stall CTS per pump, linked to the outer token so it fires on disconnect too.
        // TryReset() re-arms the timer before each write without allocating a new source.
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
        var token = linkedCts.Token;
        var snapshotGeneration = initialSnapshot.Generation;
        var snapshotLastSequence = initialSnapshot.LastSequence;

        try
        {
            foreach (var lease in initialSnapshot.Chunks)
            {
                using (lease)
                {
                    await WriteLeaseAsync(lease, stallCts, token);
                }
            }

            while (await _outbound.Reader.WaitToReadAsync(token))
            {
                while (_outbound.Reader.TryRead(out var lease))
                {
                    Interlocked.Decrement(ref _queueDepth);
                    using (lease)
                    {
                        if (lease.Generation == snapshotGeneration && lease.Sequence <= snapshotLastSequence)
                            continue;

                        await WriteLeaseAsync(lease, stallCts, token);
                    }
                }
            }

            await CompleteAsync(SubscriberDisconnectReason.Completed);
        }
        catch (OperationCanceledException) when (_context.RequestAborted.IsCancellationRequested || token.IsCancellationRequested)
        {
            await CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        }
        catch (WriteStallException)
        {
            await CompleteAsync(SubscriberDisconnectReason.WriteStall);
        }
        catch (IOException)
        {
            await CompleteAsync(SubscriberDisconnectReason.WriteFailure);
        }
    }

    private async Task WriteLeaseAsync(BufferLease lease, CancellationTokenSource stallCts, CancellationToken outerCt)
    {
        var bytes = lease.Memory;
        if (bytes.IsEmpty)
            return;

        // Re-arm the shared stall CTS for this write. TryReset clears the previous
        // timer on the hot path; if it returns false the outer token already fired and
        // CancelAfter is a no-op — the next await will throw immediately, which is fine.
        stallCts.TryReset();
        stallCts.CancelAfter(_writeStallTimeout);

        try
        {
            await _writer.WriteAsync(bytes, stallCts.Token);
            await _writer.FlushAsync(stallCts.Token);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested && stallCts.IsCancellationRequested)
        {
            // The per-write stall timer fired; the client's socket is wedged.
            throw new WriteStallException();
        }

        Interlocked.Add(ref _bytesSent, bytes.Length);
    }

    private sealed class WriteStallException : Exception;
}
