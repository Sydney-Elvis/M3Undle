using System.IO.Pipelines;
using System.Threading.Channels;
using M3Undle.Web.Streaming.Buffering;
using M3Undle.Web.Streaming.Observability;

namespace M3Undle.Web.Streaming.Subscribers;

public sealed class SubscriberConnection
{
    private readonly HttpContext _context;
    private readonly PipeWriter _writer;
    private readonly Channel<BufferLease> _outbound;
    private readonly Func<SubscriberConnection, SubscriberDisconnectReason, Task> _onCompleted;
    private readonly object _gate = new();

    private bool _started;
    private int _completed;
    private Task? _pumpTask;
    private long _bytesSent;
    private int _queueDepth;

    public SubscriberConnection(
        string sessionId,
        string requestedRoute,
        HttpContext context,
        int queueCapacity,
        Func<SubscriberConnection, SubscriberDisconnectReason, Task> onCompleted)
    {
        SessionId = sessionId;
        RequestedRoute = requestedRoute;
        _context = context;
        _writer = context.Response.BodyWriter;
        _onCompleted = onCompleted;

        var options = new BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        };

        _outbound = Channel.CreateBounded<BufferLease>(options);
        ClientId = Guid.NewGuid().ToString("N");
        ConnectedUtc = DateTimeOffset.UtcNow;
        RemoteIp = context.Connection.RemoteIpAddress?.ToString();
        UserAgent = context.Request.Headers.UserAgent.ToString();
    }

    public string ClientId { get; }

    public string SessionId { get; }

    public string RequestedRoute { get; }

    public DateTimeOffset ConnectedUtc { get; }

    public string? RemoteIp { get; }

    public string? UserAgent { get; }

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public int QueueDepth => Math.Max(0, Volatile.Read(ref _queueDepth));

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

        Interlocked.Increment(ref _queueDepth);
        return true;
    }

    public Task CompleteAsync(SubscriberDisconnectReason reason)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
            return Task.CompletedTask;

        _outbound.Writer.TryComplete();
        return _onCompleted(this, reason);
    }

    public StreamClientSnapshot Snapshot()
        => new(
            ClientId,
            SessionId,
            RequestedRoute,
            RemoteIp,
            UserAgent,
            ConnectedUtc,
            BytesSent,
            QueueDepth);

    private async Task PumpAsync(BufferSnapshot initialSnapshot, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.RequestAborted);
        var token = linkedCts.Token;

        try
        {
            foreach (var lease in initialSnapshot.Chunks)
            {
                using (lease)
                {
                    await WriteLeaseAsync(lease, token);
                }
            }

            while (await _outbound.Reader.WaitToReadAsync(token))
            {
                while (_outbound.Reader.TryRead(out var lease))
                {
                    Interlocked.Decrement(ref _queueDepth);
                    using (lease)
                    {
                        await WriteLeaseAsync(lease, token);
                    }
                }
            }

            await CompleteAsync(SubscriberDisconnectReason.Completed);
        }
        catch (OperationCanceledException) when (_context.RequestAborted.IsCancellationRequested || token.IsCancellationRequested)
        {
            await CompleteAsync(SubscriberDisconnectReason.ClientAborted);
        }
        catch (IOException)
        {
            await CompleteAsync(SubscriberDisconnectReason.WriteFailure);
        }
    }

    private async Task WriteLeaseAsync(BufferLease lease, CancellationToken ct)
    {
        var bytes = lease.Memory;
        if (bytes.IsEmpty)
            return;

        await _writer.WriteAsync(bytes, ct);
        await _writer.FlushAsync(ct);
        Interlocked.Add(ref _bytesSent, bytes.Length);
    }
}
