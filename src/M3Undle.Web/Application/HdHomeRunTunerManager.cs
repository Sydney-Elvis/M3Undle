using M3Undle.Web.Streaming.Subscribers;
using M3Undle.Web.Observability;

namespace M3Undle.Web.Application;

public sealed record HdHomeRunTunerReservation(
    string ReservationId,
    string VirtualTunerId,
    string StreamKey,
    DateTimeOffset ReservedUtc);

public sealed record HdHomeRunTunerAcquireResult(
    bool Succeeded,
    string? Error,
    HdHomeRunTunerReservation? Reservation,
    SubscriberConnection? PriorSubscriber);

public sealed record HdHomeRunTunerLeaseSnapshot(
    string VirtualTunerId,
    string StreamKey,
    string? ChannelName,
    string? ClientId,
    DateTimeOffset ReservedUtc,
    DateTimeOffset? ActivatedUtc);

public sealed class HdHomeRunTunerManager(
    HdHomeRunTunerCountResolver tunerCountResolver,
    M3UndleMetrics? metrics = null)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TunerLease> _leases = new(StringComparer.Ordinal);

    public HdHomeRunTunerAcquireResult Acquire(string virtualTunerId, string streamKey)
    {
        if (string.IsNullOrWhiteSpace(virtualTunerId))
            virtualTunerId = "hdhr-main";

        lock (_lock)
        {
            _leases.TryGetValue(virtualTunerId, out var priorLease);
            if (priorLease is not null)
            {
                DisposeLeaseResources(priorLease);
                _leases.Remove(virtualTunerId);
            }

            var streamLimit = ResolveStreamLimit();
            if (streamLimit is not null && priorLease is null && _leases.Count >= streamLimit.Value)
            {
                metrics?.RecordHdhrTuneRequest(success: false);
                return new HdHomeRunTunerAcquireResult(
                    Succeeded: false,
                    Error: $"All {streamLimit.Value} HDHomeRun tuner slots are in use.",
                    Reservation: null,
                    PriorSubscriber: null);
            }

            var reservation = new HdHomeRunTunerReservation(
                ReservationId: Guid.NewGuid().ToString("N"),
                VirtualTunerId: virtualTunerId,
                StreamKey: streamKey,
                ReservedUtc: DateTimeOffset.UtcNow);

            _leases[virtualTunerId] = new TunerLease(
                reservation,
                ChannelName: null,
                ClientId: null,
                ActivatedUtc: null,
                Subscriber: null,
                SessionRetention: null,
                PendingReleaseCts: null);

            metrics?.RecordHdhrTuneRequest(success: true);
            return new HdHomeRunTunerAcquireResult(
                Succeeded: true,
                Error: null,
                Reservation: reservation,
                PriorSubscriber: priorLease?.Subscriber);
        }
    }

    public HdHomeRunTunerAcquireResult AcquireAuto(string streamKey)
    {
        lock (_lock)
        {
            var streamLimit = ResolveStreamLimit();

            if (streamLimit is not null && _leases.Count >= streamLimit.Value)
            {
                metrics?.RecordHdhrTuneRequest(success: false);
                return new HdHomeRunTunerAcquireResult(
                    Succeeded: false,
                    Error: $"All {streamLimit.Value} HDHomeRun tuner slots are in use.",
                    Reservation: null,
                    PriorSubscriber: null);
            }

            var searchRange = streamLimit ?? _leases.Count + 1;

            for (var i = 0; i < searchRange; i++)
            {
                var tunerId = FormatTunerId(i);
                if (_leases.ContainsKey(tunerId))
                    continue;

                var reservation = new HdHomeRunTunerReservation(
                    ReservationId: Guid.NewGuid().ToString("N"),
                    VirtualTunerId: tunerId,
                    StreamKey: streamKey,
                    ReservedUtc: DateTimeOffset.UtcNow);

                _leases[tunerId] = new TunerLease(
                    reservation,
                    ChannelName: null,
                    ClientId: null,
                    ActivatedUtc: null,
                    Subscriber: null,
                    SessionRetention: null,
                    PendingReleaseCts: null);

                metrics?.RecordHdhrTuneRequest(success: true);
                return new HdHomeRunTunerAcquireResult(
                    Succeeded: true,
                    Error: null,
                    Reservation: reservation,
                    PriorSubscriber: null);
            }

            metrics?.RecordHdhrTuneRequest(success: false);
            return new HdHomeRunTunerAcquireResult(
                Succeeded: false,
                Error: $"All {streamLimit ?? tunerCountResolver.ResolveTunerCount()} HDHomeRun tuner slots are in use.",
                Reservation: null,
                PriorSubscriber: null);
        }
    }

    public bool IsValidTunerIndex(int tunerIndex)
        => tunerIndex >= 0 && tunerIndex < tunerCountResolver.ResolveTunerCount();

    internal static string FormatTunerId(int tunerIndex) => $"tuner{tunerIndex}";

    public void Activate(
        HdHomeRunTunerReservation reservation,
        SubscriberConnection subscriber,
        string? channelName,
        IDisposable? sessionRetention = null)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(reservation.VirtualTunerId, out var lease) ||
                lease.Reservation.ReservationId != reservation.ReservationId)
            {
                sessionRetention?.Dispose();
                return;
            }

            lease.PendingReleaseCts?.Cancel();
            lease.PendingReleaseCts?.Dispose();
            lease.SessionRetention?.Dispose();

            _leases[reservation.VirtualTunerId] = lease with
            {
                ChannelName = channelName,
                ClientId = subscriber.ClientId,
                ActivatedUtc = DateTimeOffset.UtcNow,
                Subscriber = subscriber,
                SessionRetention = sessionRetention,
                PendingReleaseCts = null,
            };
        }
    }

    public void Release(string reservationId, string? clientId = null)
    {
        IDisposable? retention = null;
        CancellationTokenSource? pendingReleaseCts = null;

        lock (_lock)
        {
            var kvp = _leases.FirstOrDefault(x => x.Value.Reservation.ReservationId == reservationId);
            if (string.IsNullOrWhiteSpace(kvp.Key))
                return;

            if (clientId is not null && kvp.Value.ClientId is not null && !string.Equals(kvp.Value.ClientId, clientId, StringComparison.Ordinal))
                return;

            _leases.Remove(kvp.Key);
            retention = kvp.Value.SessionRetention;
            pendingReleaseCts = kvp.Value.PendingReleaseCts;
        }

        pendingReleaseCts?.Cancel();
        pendingReleaseCts?.Dispose();
        retention?.Dispose();
    }

    public void ReleaseWithGrace(string reservationId, TimeSpan grace, string? clientId = null)
    {
        if (grace <= TimeSpan.Zero)
        {
            Release(reservationId, clientId);
            return;
        }

        string? tunerId = null;
        CancellationTokenSource? priorPendingReleaseCts = null;
        CancellationTokenSource? nextPendingReleaseCts = null;

        lock (_lock)
        {
            var kvp = _leases.FirstOrDefault(x => x.Value.Reservation.ReservationId == reservationId);
            if (string.IsNullOrWhiteSpace(kvp.Key))
                return;

            if (clientId is not null && kvp.Value.ClientId is not null && !string.Equals(kvp.Value.ClientId, clientId, StringComparison.Ordinal))
                return;

            tunerId = kvp.Key;
            priorPendingReleaseCts = kvp.Value.PendingReleaseCts;
            nextPendingReleaseCts = new CancellationTokenSource();

            _leases[tunerId] = kvp.Value with { PendingReleaseCts = nextPendingReleaseCts };
        }

        priorPendingReleaseCts?.Cancel();
        priorPendingReleaseCts?.Dispose();

        _ = Task.Run(async () =>
        {
            IDisposable? retention = null;

            try
            {
                await Task.Delay(grace, nextPendingReleaseCts!.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                nextPendingReleaseCts.Dispose();
            }

            lock (_lock)
            {
                if (tunerId is null
                    || !_leases.TryGetValue(tunerId, out var current)
                    || current.Reservation.ReservationId != reservationId
                    || !ReferenceEquals(current.PendingReleaseCts, nextPendingReleaseCts))
                {
                    return;
                }

                _leases.Remove(tunerId);
                retention = current.SessionRetention;
            }

            retention?.Dispose();
        });
    }

    public IReadOnlyList<HdHomeRunTunerLeaseSnapshot> GetActiveLeases()
    {
        lock (_lock)
        {
            return _leases.Values
                .Select(x => new HdHomeRunTunerLeaseSnapshot(
                    VirtualTunerId: x.Reservation.VirtualTunerId,
                    StreamKey: x.Reservation.StreamKey,
                    ChannelName: x.ChannelName,
                    ClientId: x.ClientId,
                    ReservedUtc: x.Reservation.ReservedUtc,
                    ActivatedUtc: x.ActivatedUtc))
                .OrderBy(x => x.VirtualTunerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private int? ResolveStreamLimit() => tunerCountResolver.ResolveStreamLimit();

    private static void DisposeLeaseResources(TunerLease lease)
    {
        lease.PendingReleaseCts?.Cancel();
        lease.PendingReleaseCts?.Dispose();
        lease.SessionRetention?.Dispose();
    }

    private sealed record TunerLease(
        HdHomeRunTunerReservation Reservation,
        string? ChannelName,
        string? ClientId,
        DateTimeOffset? ActivatedUtc,
        SubscriberConnection? Subscriber,
        IDisposable? SessionRetention,
        CancellationTokenSource? PendingReleaseCts);
}
